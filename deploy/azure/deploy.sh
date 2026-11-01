#!/usr/bin/env bash
# Creates or updates the nightly Azure Container Apps Job that runs the alerter.
# Safe to re-run: every resource is checked with `show`, then created or updated.
#
# Required env:  IMAGE  ConnectionStrings__Precip  PRECIP_NTFY_TOPIC  PRECIP_USER_AGENT
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
: "${ConnectionStrings__Precip:?set ConnectionStrings__Precip (the Neon connection string)}"
: "${PRECIP_NTFY_TOPIC:?set PRECIP_NTFY_TOPIC}"
: "${PRECIP_USER_AGENT:?set PRECIP_USER_AGENT (NWS rejects requests without contact info)}"

# The job must pin a commit so every run is traceable to one: a git sha tag
# (7-40 hex) or a digest. Branch-like tags (:main, :latest) are mutable on GHCR.
# Take the last path segment, so a registry port (host:5000/img) isn't read as a tag.
last_segment="${IMAGE##*/}"
if [[ "$IMAGE" =~ @sha256:[0-9a-f]{64}$ ]]; then
  :  # digest reference: immutable by construction
elif [[ "$last_segment" == *:* && "${last_segment##*:}" =~ ^[0-9a-f]{7,40}$ ]]; then
  :  # git sha tag
else
  echo "IMAGE must end in a git sha tag (:<7-40 hex>) or @sha256:<digest>, so a run maps to a commit." >&2
  exit 1
fi

# `show` exits non-zero for "doesn't exist" AND for real errors (unregistered
# provider, expired login, throttling). Only the first may fall through to
# create; anything else aborts with az's own message.
exists() {
  local err
  if err=$("$@" -o none 2>&1); then
    return 0
  elif grep -qiE 'ResourceNotFound|ResourceGroupNotFound|could not be found|was not found' <<<"$err"; then
    return 1
  else
    echo "$err" >&2
    exit 1
  fi
}

# Without these the show calls fail as above; say what to run instead.
for ns in Microsoft.App Microsoft.OperationalInsights; do
  state=$(az provider show -n "$ns" --query registrationState -o tsv)
  if [[ "$state" != "Registered" ]]; then
    echo "Resource provider $ns is '$state'. One-time fix: az provider register -n $ns --wait" >&2
    exit 1
  fi
done

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

# Secret names are limited to 20 chars. The ntfy topic and the DB connection string
# (it carries the password) are secrets; env vars reference them with secretref:.
# Coordinates, window and threshold are rows in the database now, not job config.
secrets=(
  "ntfy-topic=$PRECIP_NTFY_TOPIC"
  "db-conn=$ConnectionStrings__Precip"
)
env_vars=(
  "PRECIP_NTFY_TOPIC=secretref:ntfy-topic"
  "ConnectionStrings__Precip=secretref:db-conn"
  "PRECIP_USER_AGENT=$PRECIP_USER_AGENT"
)

# The job runs one pass and exits. `--args=--once` (with the `=`): the CLI's parser
# rejects a separate `--args --once` as an unrecognized argument. This replaces the
# image's default arguments only; the ENTRYPOINT (dotnet Worker.dll) still applies.
# Migrations are NOT run here: apply them explicitly with `Worker --migrate`
# (deploy/azure/README.md) so a deploy never changes the schema as a side effect.
job_args=(--args=--once)

# One retry covers an NWS blip. The database's UNIQUE (location, night) key
# (D25/D26) is what stops a retry or a second execution from alerting twice.
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

if exists az containerapp env show -n "$AZ_ENV" -g "$AZ_RG"; then
  echo "Environment $AZ_ENV exists."
else
  echo "Creating environment $AZ_ENV (Consumption, auto-created Log Analytics)..."
  az containerapp env create -n "$AZ_ENV" -g "$AZ_RG" -l "$AZ_LOCATION" \
    --enable-workload-profiles false -o none
fi

if exists az containerapp job show -n "$AZ_JOB" -g "$AZ_RG"; then
  echo "Updating job $AZ_JOB..."
  # `job update` has no --secrets or registry flags, so those go through their
  # own commands first (secrets must exist before an env var can secretref them).
  az containerapp job secret set -n "$AZ_JOB" -g "$AZ_RG" --secrets "${secrets[@]}" -o none
  if [[ $use_registry -eq 1 ]]; then
    az containerapp job registry set -n "$AZ_JOB" -g "$AZ_RG" \
      --server ghcr.io --username "$GHCR_USER" --password "$GHCR_PAT" -o none
  # Assigned separately: `if [[ -n "$(cmd)" ]]` ignores a failing cmd, so a broken list would
  # skip the cleanup silently and still print "Deployed." (D24: fail closed).
  elif ! ghcr_entries=$(az containerapp job registry list -n "$AZ_JOB" -g "$AZ_RG" --query "[?server=='ghcr.io'].server" -o tsv); then
    echo "Could not list registry credentials on $AZ_JOB; stopping." >&2
    exit 1
  elif [[ -n "${ghcr_entries//[$'\r\n']/}" ]]; then
    # Public image now: drop the old credential. GHCR rejects a pull with a bad
    # PAT even for a public image, so a stale one would break the job when it expires.
    echo "Removing ghcr.io registry credentials (public image)..."
    az containerapp job registry remove -n "$AZ_JOB" -g "$AZ_RG" --server ghcr.io -o none
  fi
  # --replace-env-vars drops any stale variables not listed here.
  az containerapp job update -n "$AZ_JOB" -g "$AZ_RG" \
    --image "$IMAGE" \
    --cron-expression "$CRON" \
    "${sizing[@]}" \
    "${job_args[@]}" \
    --replace-env-vars "${env_vars[@]}" -o none
  # Block 3's coordinate secrets are dead weight now. Azure refuses to remove a
  # secret something still references, so this must come after the update above
  # (whose --replace-env-vars dropped the old secretref: variables). Names only:
  # `secret list` hides values unless --show-values is passed. A failing list
  # aborts the script (set -e), it is never read as "nothing to remove".
  current_secrets=$(az containerapp job secret list -n "$AZ_JOB" -g "$AZ_RG" --query "[].name" -o tsv | tr -d '\r')
  for old in precip-lat precip-lon; do
    if grep -qxF "$old" <<<"$current_secrets"; then
      echo "Removing obsolete secret $old..."
      az containerapp job secret remove -n "$AZ_JOB" -g "$AZ_RG" --secret-names "$old" --yes -o none
    fi
  done
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
    "${job_args[@]}" \
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
