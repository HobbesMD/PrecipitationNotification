#!/usr/bin/env bash
# Tests for migration-guard.sh. Plain bash, no framework: builds a throwaway git
# repo with a known history and runs the guard against it.
#
#   A  initial; adds src/Persistence/Migrations/0001.cs
#   B  changes only src/Worker/x.cs
#   C  adds src/Persistence/Migrations/0002.cs
#   D  changes only README.md
#
# Usage: bash deploy/azure/test-migration-guard.sh   (exit 0 = all passed)
set -euo pipefail

guard="$(cd "$(dirname "$0")" && pwd)/migration-guard.sh"
[[ -f "$guard" ]] || { echo "migration-guard.sh not found at $guard" >&2; exit 1; }

repo=$(mktemp -d)
trap 'rm -rf "$repo"' EXIT
cd "$repo"

# The guard must watch its default path, so the test doesn't override it.
unset MIGRATIONS_PATH

git init -q .
git config user.name "migration-guard test"
git config user.email "test@example.invalid"
git config commit.gpgsign false
git config core.autocrlf false

commit() {
  git add -A
  git commit -q -m "$1"
  git rev-parse HEAD
}

mkdir -p src/Persistence/Migrations src/Worker
echo "// 0001" > src/Persistence/Migrations/0001.cs
echo "// worker" > src/Worker/x.cs
echo "readme" > README.md
A=$(commit A)
echo "// worker v2" > src/Worker/x.cs
B=$(commit B)
echo "// 0002" > src/Persistence/Migrations/0002.cs
C=$(commit C)
echo "readme v2" > README.md
D=$(commit D)

img="ghcr.io/hobbesmd/precipitation-notification"
digest="sha256:$(printf 'a%.0s' {1..64})"
unknown="0123456789abcdef0123456789abcdef01234567"

failures=0
# check NAME EXPECTED_EXIT EXPECTED_SUBSTRING -- GUARD_ARGS...
check() {
  local name="$1" want_code="$2" want_text="$3"
  shift 4
  local out code=0
  out=$(bash "$guard" "$@" 2>&1) || code=$?
  if [[ "$code" -eq "$want_code" && "$out" == *"$want_text"* ]]; then
    echo "PASS  $name"
  else
    echo "FAIL  $name: want exit $want_code containing '$want_text', got exit $code:"
    while IFS= read -r line; do echo "        $line"; done <<<"$out"
    failures=$((failures + 1))
  fi
}

check "B->B already deployed"            0 "already deployed"        -- "$img:$B" "$B" false
check "A->B no migration change"          0 "No migration changes"    -- "$img:$A" "$B" false
check "B->C new migration blocks"         3 "Persistence/Migrations/0002.cs" -- "$img:$B" "$C" false
check "B->C prints the re-run command"    3 "sha=${C:0:12} and migrations_applied=true" -- "$img:$B" "$C" false
check "B->D spans a migration, blocks"    3 "0002.cs"                 -- "$img:$B" "$D" false
check "C->D no migration change"          0 "No migration changes"    -- "$img:$C" "$D" false
check "D->B rollback across one blocks"   3 "0002.cs"                 -- "$img:$D" "$B" false
check "D->B rollback mentions rollback"   3 "rollback"                -- "$img:$D" "$B" false
check "B->C with migrations_applied=true" 0 "overridden"              -- "$img:$B" "$C" true
check "unknown deployed sha fails closed" 1 "Can't find the deployed commit" -- "$img:$unknown" "$B" false
check "unknown target sha fails closed"   1 "Can't find the target commit"   -- "$img:$B" "$unknown" false
check "digest-only ref fails closed"      1 "migrations_applied=true" -- "$img@$digest" "$B" false
check "tag plus digest counts as digest"  1 "pinned by digest"        -- "$img:$B@$digest" "$C" false
check "mutable :latest fails closed"      1 "no git sha tag"          -- "$img:latest" "$B" false
check "untagged registry port rejected"   1 "no git sha tag"          -- "myreg:5000/precip" "$B" false
check "invalid flag value"                1 "must be 'true' or 'false'" -- "$img:$B" "$C" yes
check "non-hex target rejected"           1 "target-sha must be"      -- "$img:$B" "HEAD" false
check "missing argument"                  1 "Usage"                   -- "$img:$B" "$C"
check "12-char refs: B->C blocks"         3 "0002.cs"                 -- "$img:${B:0:12}" "${C:0:12}" false
check "12-char refs: C->D passes"         0 "No migration changes"    -- "$img:${C:0:12}" "${D:0:12}" false
check "12-char vs full: same commit"      0 "already deployed"        -- "$img:${B:0:12}" "$B" false

# Running outside any repository must fail closed, not report "no changes".
outside=$(mktemp -d)
code=0
out=$(cd "$outside" && GIT_CEILING_DIRECTORIES="$outside" bash "$guard" "$img:$B" "$C" false 2>&1) || code=$?
rm -rf "$outside"
if [[ "$code" -eq 1 && "$out" == *"Not inside a git repository"* ]]; then
  echo "PASS  outside a repo fails closed"
else
  echo "FAIL  outside a repo fails closed: got exit $code: $out"
  failures=$((failures + 1))
fi

echo
if [[ $failures -ne 0 ]]; then
  echo "$failures check(s) FAILED"
  exit 1
fi
echo "All checks passed."
