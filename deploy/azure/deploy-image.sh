#!/usr/bin/env bash
# Image-only release: points the existing nightly job at a new image and updates
# its service.version. This is what CI runs on every deploy. Everything else
# (secrets, cron, sizing, registry credentials, other env vars) is left exactly as
# deploy.sh last set it, so CI never needs to hold the app's secrets.
#
# Required env:  IMAGE  (ghcr.io/hobbesmd/precipitation-notification:<git-sha>; a sha
#                tag, not a digest: the migration guard maps the tag to a commit)
# Optional env:  AZ_RG (rg-precip)  AZ_JOB (job-precip-nightly)
#                MIGRATIONS_APPLIED (false) - true once `Worker --migrate` has been run
#
# Run from the repo root with full git history: the migration guard diffs the
# deployed commit against the target.
#
# Never creates the job. The first deploy (and any configuration change) is
# deploy.sh, run by hand; if the job isn't there, this stops.
#
# Exit codes: 0 deployed; 3 blocked by the migration guard (next steps printed);
# 1 anything else. No `set -x`, ever: the job's env holds secretref names next to
# plain values, and only OTEL_RESOURCE_ATTRIBUTES is ever printed.
set -euo pipefail

here="$(dirname "$0")"
# shellcheck source=deploy/azure/lib.sh
source "$here/lib.sh"

AZ_RG="${AZ_RG:-rg-precip}"
AZ_JOB="${AZ_JOB:-job-precip-nightly}"
MIGRATIONS_APPLIED="${MIGRATIONS_APPLIED:-false}"

: "${IMAGE:?set IMAGE, e.g. ghcr.io/hobbesmd/precipitation-notification:<git-sha>}"
validate_image "$IMAGE"
# validate_image also accepts a digest (deploy.sh allows one for a manual deploy),
# but a digest can't be mapped back to a commit, so the guard couldn't run.
if [[ "$IMAGE" == *@sha256:* ]]; then
  echo "deploy-image.sh needs a git sha tag, not a digest: the migration guard maps the tag to a commit." >&2
  exit 1
fi
if [[ "$MIGRATIONS_APPLIED" != "true" && "$MIGRATIONS_APPLIED" != "false" ]]; then
  echo "MIGRATIONS_APPLIED must be 'true' or 'false'." >&2
  exit 1
fi
target_sha=$(image_service_version "$IMAGE")
service_version="$target_sha"

# What the job runs now. Assigned inside `if !` so a failed show (expired login,
# throttling, job missing) is caught here with a clear message rather than read
# as an empty image. Never falls through to create: that's deploy.sh's job.
if ! current_image=$(az containerapp job show -n "$AZ_JOB" -g "$AZ_RG" \
    --query "properties.template.containers[0].image" -o tsv); then
  echo "Could not read job $AZ_JOB in $AZ_RG. If it doesn't exist yet, run deploy.sh first." >&2
  exit 1
fi
current_image="${current_image//$'\r'/}"
current_image="${current_image%$'\n'}"
if [[ -z "$current_image" ]]; then
  echo "Job $AZ_JOB reports no container image; stopping. Run deploy.sh to repair it." >&2
  exit 1
fi
echo "Job $AZ_JOB currently runs $current_image"

# Refuse to put new code in front of an un-migrated schema (D28). The guard's own
# exit code is passed through: 3 means "blocked, next steps printed", which the
# workflow distinguishes from a plain failure.
guard_code=0
bash "$here/migration-guard.sh" "$current_image" "$target_sha" "$MIGRATIONS_APPLIED" || guard_code=$?
if [[ $guard_code -ne 0 ]]; then
  exit "$guard_code"
fi

# OTEL_RESOURCE_ATTRIBUTES is a plain env var (no secret in it), set by deploy.sh
# as a comma-separated key=value list. Only its service.version entry changes;
# anything else in it (deployment.environment.name, ...) is kept as is.
otel_query="properties.template.containers[0].env[?name=='OTEL_RESOURCE_ATTRIBUTES'].value | [0]"
read_otel() {
  local v
  v=$(az containerapp job show -n "$AZ_JOB" -g "$AZ_RG" --query "$otel_query" -o tsv) || return 1
  v="${v//$'\r'/}"
  printf '%s' "${v%$'\n'}"
}
if ! current_otel=$(read_otel); then
  echo "Could not read OTEL_RESOURCE_ATTRIBUTES from $AZ_JOB; stopping." >&2
  exit 1
fi
if [[ -z "$current_otel" || "$current_otel" == "None" || "$current_otel" == "null" ]]; then
  echo "Job $AZ_JOB has no OTEL_RESOURCE_ATTRIBUTES. Run deploy.sh once to set the job's full configuration." >&2
  exit 1
fi

new_entries=()
found=0
IFS=',' read -r -a entries <<<"$current_otel"
for entry in "${entries[@]}"; do
  if [[ "$entry" == service.version=* ]]; then
    new_entries+=("service.version=$service_version")
    found=1
  else
    new_entries+=("$entry")
  fi
done
if [[ $found -eq 0 ]]; then
  echo "OTEL_RESOURCE_ATTRIBUTES on $AZ_JOB has no service.version entry. Run deploy.sh once to set the job's full configuration." >&2
  exit 1
fi
new_otel=$(IFS=','; printf '%s' "${new_entries[*]}")

echo "Updating $AZ_JOB to $IMAGE (OTEL_RESOURCE_ATTRIBUTES=$new_otel)..."
# --set-env-vars MERGES into the existing list: only OTEL_RESOURCE_ATTRIBUTES
# changes. --replace-env-vars would drop every variable not named here, including
# the secretref: ones (database, ntfy topic, heartbeat, OTLP header), and the next
# run would fail. No other flag is passed, so secrets, cron, sizing and registry
# credentials are untouched.
az containerapp job update -n "$AZ_JOB" -g "$AZ_RG" \
  --image "$IMAGE" \
  --set-env-vars "OTEL_RESOURCE_ATTRIBUTES=$new_otel" -o none

# Read back rather than trust the update's exit code: a deploy that says "done"
# while the job still runs the old image is the failure this whole block is about.
if ! after_image=$(az containerapp job show -n "$AZ_JOB" -g "$AZ_RG" \
    --query "properties.template.containers[0].image" -o tsv); then
  echo "Could not read back $AZ_JOB after the update; verify it by hand." >&2
  exit 1
fi
after_image="${after_image//$'\r'/}"
after_image="${after_image%$'\n'}"
if [[ "$after_image" != "$IMAGE" ]]; then
  echo "Read-back mismatch: $AZ_JOB runs '$after_image', expected '$IMAGE'." >&2
  exit 1
fi
if ! after_otel=$(read_otel); then
  echo "Could not read back OTEL_RESOURCE_ATTRIBUTES on $AZ_JOB; verify it by hand." >&2
  exit 1
fi
# Compare whole entries: service.version=abc1234 must not match abc1234ff.
if [[ ",$after_otel," != *",service.version=$service_version,"* ]]; then
  echo "Read-back mismatch: OTEL_RESOURCE_ATTRIBUTES is '$after_otel', expected service.version=$service_version." >&2
  exit 1
fi

summary="Deployed $IMAGE (was $current_image)"
echo "$summary"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  echo "$summary" >>"$GITHUB_STEP_SUMMARY"
fi
