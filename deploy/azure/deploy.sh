#!/usr/bin/env bash
# Creates or updates the nightly Azure Container Apps Job that runs the alerter.
# Safe to re-run: every resource is checked with `show`, then created or updated.
#
# Required env:  IMAGE  PRECIP_LAT  PRECIP_LON  PRECIP_NTFY_TOPIC  PRECIP_USER_AGENT
# Optional env:  AZ_RG (rg-precip)  AZ_LOCATION (eastus)  AZ_ENV (cae-precip)
#                AZ_JOB (job-precip-nightly)  GHCR_USER + GHCR_PAT (both or neither)
#
# Usage: deploy/azure/deploy.sh      (see deploy/azure/README.md)
#
# Secret values are passed as az arguments (visible to local process listings and
# shell history only) and are never echoed. No `set -x`, ever.
set -euo pipefail

AZ_RG="${AZ_RG:-rg-precip}"
AZ_LOCATION="${AZ_LOCATION:-eastus}"
AZ_ENV="${AZ_ENV:-cae-precip}"
AZ_JOB="${AZ_JOB:-job-precip-nightly}"

: "${IMAGE:?set IMAGE, e.g. ghcr.io/hobbesmd/precipitation-notification:<git-sha>}"
: "${PRECIP_LAT:?set PRECIP_LAT}"
: "${PRECIP_LON:?set PRECIP_LON}"
: "${PRECIP_NTFY_TOPIC:?set PRECIP_NTFY_TOPIC}"
: "${PRECIP_USER_AGENT:?set PRECIP_USER_AGENT (NWS rejects requests without contact info)}"

# The job must pin an immutable tag so every run is traceable to a commit.
# Take the last path segment, so a registry port (host:5000/img) isn't read as a tag.
last_segment="${IMAGE##*/}"
if [[ "$IMAGE" == *@sha256:* ]]; then
  :  # digest reference: immutable by construction
elif [[ "$last_segment" != *:* ]]; then
  echo "IMAGE has no tag; pin a git sha tag (an untagged image means :latest)." >&2
  exit 1
elif [[ "${last_segment##*:}" == "latest" ]]; then
  echo "IMAGE uses :latest; pin a git sha tag so a run maps to a commit." >&2
  exit 1
fi

# Registry credentials: both or neither (neither = public image).
use_registry=0
if [[ -n "${GHCR_USER:-}" && -n "${GHCR_PAT:-}" ]]; then
  use_registry=1
elif [[ -n "${GHCR_USER:-}" || -n "${GHCR_PAT:-}" ]]; then
  echo "Set both GHCR_USER and GHCR_PAT, or neither (public image)." >&2
  exit 1
fi

# Container Apps cron is UTC only. 23:00 UTC = 7 PM EDT / 6 PM EST, so the run
# is always before the 8 PM local window opens, in both halves of the DST year.
CRON="0 23 * * *"

# Secret names are limited to 20 chars. Lat/lon are secrets because they pin the
# owner's home. Env vars reference them with secretref:.
secrets=(
  "ntfy-topic=$PRECIP_NTFY_TOPIC"
  "precip-lat=$PRECIP_LAT"
  "precip-lon=$PRECIP_LON"
)
env_vars=(
  "PRECIP_NTFY_TOPIC=secretref:ntfy-topic"
  "PRECIP_LAT=secretref:precip-lat"
  "PRECIP_LON=secretref:precip-lon"
  "PRECIP_USER_AGENT=$PRECIP_USER_AGENT"
  "PRECIP_WINDOW_START=20:00"
  "PRECIP_WINDOW_END=07:00"
)

# One retry covers an NWS blip. A retry can duplicate an alert only if ntfy
# accepted the push but the response was lost; accepted until Block 4's
# DB-enforced idempotency.
sizing=(
  --replica-timeout 300
  --replica-retry-limit 1
  --parallelism 1
  --replica-completion-count 1
  --cpu 0.25
  --memory 0.5Gi
)

echo "Resource group: $AZ_RG ($AZ_LOCATION)"
az group create -n "$AZ_RG" -l "$AZ_LOCATION" -o none

if az containerapp env show -n "$AZ_ENV" -g "$AZ_RG" -o none 2>/dev/null; then
  echo "Environment $AZ_ENV exists."
else
  echo "Creating environment $AZ_ENV (Consumption, auto-created Log Analytics)..."
  az containerapp env create -n "$AZ_ENV" -g "$AZ_RG" -l "$AZ_LOCATION" \
    --enable-workload-profiles false -o none
fi

if az containerapp job show -n "$AZ_JOB" -g "$AZ_RG" -o none 2>/dev/null; then
  echo "Updating job $AZ_JOB..."
  # `job update` has no --secrets or registry flags, so those go through their
  # own commands first (secrets must exist before an env var can secretref them).
  az containerapp job secret set -n "$AZ_JOB" -g "$AZ_RG" --secrets "${secrets[@]}" -o none
  if [[ $use_registry -eq 1 ]]; then
    az containerapp job registry set -n "$AZ_JOB" -g "$AZ_RG" \
      --server ghcr.io --username "$GHCR_USER" --password "$GHCR_PAT" -o none
  fi
  # --replace-env-vars drops any stale variables not listed here.
  az containerapp job update -n "$AZ_JOB" -g "$AZ_RG" \
    --image "$IMAGE" \
    --cron-expression "$CRON" \
    "${sizing[@]}" \
    --replace-env-vars "${env_vars[@]}" -o none
else
  echo "Creating job $AZ_JOB..."
  registry_args=()
  if [[ $use_registry -eq 1 ]]; then
    # The CLI stores the password as a job secret itself; it is not kept in
    # plain text in the job definition.
    registry_args=(--registry-server ghcr.io --registry-username "$GHCR_USER"
                   --registry-password "$GHCR_PAT")
  fi
  az containerapp job create -n "$AZ_JOB" -g "$AZ_RG" \
    --environment "$AZ_ENV" \
    --trigger-type Schedule --cron-expression "$CRON" \
    --image "$IMAGE" \
    "${sizing[@]}" \
    ${registry_args[@]+"${registry_args[@]}"} \
    --secrets "${secrets[@]}" \
    --env-vars "${env_vars[@]}" -o none
fi

echo
echo "Deployed."
echo "  Job:      $AZ_JOB (resource group $AZ_RG)"
echo "  Schedule: $CRON (UTC)"
echo "  Image:    $IMAGE"
echo
echo "Next: trigger a manual run, then inspect it:"
echo "  az containerapp job start -n $AZ_JOB -g $AZ_RG"
echo "  az containerapp job execution list -n $AZ_JOB -g $AZ_RG -o table"
