#!/usr/bin/env bash
# Tests for deploy-image.sh against a fake `az` that keeps the job's state in
# files. Plain bash, no framework, no Azure. Builds a throwaway git repo so the
# migration guard has a known history:
#
#   A  initial; adds src/Persistence/Migrations/0001.cs
#   B  changes only src/Worker/x.cs
#   C  adds src/Persistence/Migrations/0002.cs
#
# What it proves (D49, D51):
#   - only the image and the service.version entry change; other entries stay
#   - the update uses --set-env-vars (merge), never --replace-env-vars
#   - a schema change stops before any update call (exit 3)
#   - a read-back mismatch, a missing OTEL_RESOURCE_ATTRIBUTES, a failing show,
#     a digest target and a bad flag all fail closed (exit 1) without updating
#
# Usage: bash deploy/azure/test-deploy-image.sh   (exit 0 = all passed)
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/deploy-image.sh"
[[ -f "$script" ]] || { echo "deploy-image.sh not found at $script" >&2; exit 1; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
repo="$tmp/repo"
mkdir -p "$repo" "$tmp/bin"

# --- fake az: state lives in $FAKE_STATE; FAKE_MODE injects a failure ---------
cat >"$tmp/bin/az" <<'EOF'
#!/usr/bin/env bash
st="$FAKE_STATE"
printf '%s\n' "az $*" >>"$st/calls"
case "$*" in
  "containerapp job show"*"containers[0].image"*)
    [[ "${FAKE_MODE:-}" == show_fails ]] && { echo "ERROR: (AuthorizationFailed)" >&2; exit 3; }
    printf '%s\r\n' "$(cat "$st/image")" ;;
  "containerapp job show"*"OTEL_RESOURCE_ATTRIBUTES"*)
    if [[ -f "$st/otel" ]]; then printf '%s\r\n' "$(cat "$st/otel")"; else printf 'None\r\n'; fi ;;
  "containerapp job update"*)
    [[ "$*" == *--replace-env-vars* ]] && { echo "replace-env-vars used" >&2; exit 97; }
    img="" env=""
    while [[ $# -gt 0 ]]; do
      case "$1" in --image) img="$2"; shift ;; --set-env-vars) env="$2"; shift ;; esac
      shift
    done
    # mismatch: the env var lands but the image doesn't, so only the image
    # read-back can catch it.
    [[ "${FAKE_MODE:-}" != mismatch ]] && echo "$img" >"$st/image"
    echo "${env#OTEL_RESOURCE_ATTRIBUTES=}" >"$st/otel" ;;
  *) echo "unexpected az call: $*" >&2; exit 98 ;;
esac
EOF
chmod +x "$tmp/bin/az"

# --- a repo with a known history -----------------------------------------------
cd "$repo"
git init -q .
git config user.name "deploy-image test"
git config user.email "test@example.invalid"
git config commit.gpgsign false
git config core.autocrlf false
commit() { git add -A; git commit -q -m "$1"; git rev-parse --short=12 HEAD; }
mkdir -p src/Persistence/Migrations src/Worker
echo "// 0001" >src/Persistence/Migrations/0001.cs
echo "// worker" >src/Worker/x.cs
A=$(commit A)
echo "// worker v2" >src/Worker/x.cs
B=$(commit B)
echo "// 0002" >src/Persistence/Migrations/0002.cs
C=$(commit C)

img="ghcr.io/hobbesmd/precipitation-notification"
otel="service.version=$A,deployment.environment.name=production"

failures=0
# run NAME MODE CURRENT_IMAGE OTEL(or -) TARGET_IMAGE MIGRATIONS_APPLIED
# Sets: code, out, calls, final_image, final_otel
run() {
  local name="$1" mode="$2" cur="$3" o="$4" target="$5" ma="$6"
  local st="$tmp/st-$name"
  mkdir -p "$st"
  echo "$cur" >"$st/image"
  [[ "$o" != - ]] && echo "$o" >"$st/otel"
  : >"$st/calls"
  : >"$st/summary"
  code=0
  out=$(env PATH="$tmp/bin:$PATH" FAKE_STATE="$st" FAKE_MODE="$mode" \
          GITHUB_STEP_SUMMARY="$st/summary" IMAGE="$target" MIGRATIONS_APPLIED="$ma" \
          bash "$script" 2>&1) || code=$?
  calls=$(cat "$st/calls")
  final_image=$(cat "$st/image")
  final_otel=$(cat "$st/otel" 2>/dev/null || true)
  summary=$(cat "$st/summary")
}
expect() {  # expect NAME CONDITION-RESULT(0/1) DETAIL
  if [[ "$2" -eq 0 ]]; then echo "PASS  $1"; else
    echo "FAIL  $1: $3"
    while IFS= read -r line; do echo "        $line"; done <<<"$out"
    failures=$((failures + 1))
  fi
}
updated() { [[ "$calls" == *"containerapp job update"* ]]; }

run happy "" "$img:$A" "$otel" "$img:$B" false
ok=1
[[ $code -eq 0 && "$final_image" == "$img:$B" \
   && "$final_otel" == "service.version=$B,deployment.environment.name=production" \
   && "$summary" == "Deployed $img:$B (was $img:$A)" ]] && ok=0
expect "happy path: image and service.version change, other entries kept" $ok "exit $code, image $final_image, otel $final_otel, summary '$summary'"
ok=1; [[ "$calls" == *"--set-env-vars OTEL_RESOURCE_ATTRIBUTES="* && "$calls" != *--replace-env-vars* \
         && "$calls" != *secret* && "$calls" != *--cron* && "$calls" != *registry* ]] && ok=0
expect "happy path: merge-only update, nothing else touched" $ok "calls: $calls"

run block "" "$img:$B" "$otel" "$img:$C" false
ok=1; [[ $code -eq 3 && "$out" == *0002.cs* ]] && ! updated && [[ "$final_image" == "$img:$B" ]] && ok=0
expect "schema change: exit 3, no update call" $ok "exit $code"

run override "" "$img:$B" "$otel" "$img:$C" true
ok=1; [[ $code -eq 0 && "$final_image" == "$img:$C" ]] && ok=0
expect "schema change with migrations_applied=true deploys" $ok "exit $code"

run no-otel "" "$img:$A" - "$img:$B" false
ok=1; [[ $code -eq 1 && "$out" == *"Run deploy.sh once"* ]] && ! updated && ok=0
expect "missing OTEL_RESOURCE_ATTRIBUTES: exit 1, no update" $ok "exit $code"

run no-sv "" "$img:$A" "deployment.environment.name=production" "$img:$B" false
ok=1; [[ $code -eq 1 ]] && ! updated && ok=0
expect "no service.version entry: exit 1, no update" $ok "exit $code"

run mismatch mismatch "$img:$A" "$otel" "$img:$B" false
ok=1; [[ $code -eq 1 && "$out" == *"Read-back mismatch"* ]] && ok=0
expect "update that didn't take: read-back fails it" $ok "exit $code"

run show-fails show_fails "$img:$A" "$otel" "$img:$B" false
ok=1; [[ $code -eq 1 ]] && ! updated && ok=0
expect "failing show: exit 1, never creates or updates" $ok "exit $code"

run digest "" "$img:$A" "$otel" "$img@sha256:$(printf 'd%.0s' {1..64})" false
ok=1; [[ $code -eq 1 && -z "$calls" ]] && ok=0
expect "digest target: exit 1 before any az call" $ok "exit $code, calls: $calls"

run bad-flag "" "$img:$A" "$otel" "$img:$B" yes
ok=1; [[ $code -eq 1 && -z "$calls" ]] && ok=0
expect "bad MIGRATIONS_APPLIED: exit 1 before any az call" $ok "exit $code"

run latest "" "$img:$A" "$otel" "$img:latest" false
ok=1; [[ $code -eq 1 && -z "$calls" ]] && ok=0
expect "mutable :latest target: exit 1 before any az call" $ok "exit $code"

echo
if [[ $failures -ne 0 ]]; then
  echo "$failures check(s) FAILED"
  exit 1
fi
echo "All checks passed."
