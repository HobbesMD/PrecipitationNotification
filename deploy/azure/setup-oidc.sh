#!/usr/bin/env bash
# Creates or updates the identity GitHub Actions uses to deploy: an Entra app
# registration + service principal, trusted via a federated credential (OIDC), with
# a custom role that can read and update the nightly job's definition, and nothing
# else (no secret values).
# Safe to re-run: every object is looked up first, then created only if missing.
#
# Optional env: AZ_RG (rg-precip)  AZ_ENV (cae-precip)  AZ_JOB (job-precip-nightly)
#               GH_REPO (HobbesMD/PrecipitationNotification)  APP_NAME (gh-precip-deploy)
#               JOIN_ENV=1  also create and assign the environment-join custom role
#
# Usage: deploy/azure/setup-oidc.sh      (see deploy/azure/README.md, Block 7)
#
# There is no secret anywhere in this flow: no client secret, no certificate, no
# PAT. GitHub presents a short-lived OIDC token and Azure checks its subject
# against the federated credential below. No `set -x`, ever.
set -euo pipefail

# Git Bash on Windows rewrites arguments that look like POSIX paths, which would
# mangle Azure resource ids such as /subscriptions/... before az sees them.
export MSYS_NO_PATHCONV=1

AZ_RG="${AZ_RG:-rg-precip}"
AZ_ENV="${AZ_ENV:-cae-precip}"
AZ_JOB="${AZ_JOB:-job-precip-nightly}"
GH_REPO="${GH_REPO:-HobbesMD/PrecipitationNotification}"
APP_NAME="${APP_NAME:-gh-precip-deploy}"
JOIN_ENV="${JOIN_ENV:-0}"

ISSUER="https://token.actions.githubusercontent.com"
CRED_NAME="github-production"
# The subject is the lock. Only a workflow job that runs against the GitHub
# environment `production` (itself restricted to the main branch) can present it.
SUBJECT="repo:${GH_REPO}:environment:production"
JOINER_ROLE="Container Apps Environment Joiner"
DEPLOYER_ROLE="Precip Job Deployer"

# `show` exits non-zero for "doesn't exist" AND for real errors (expired login,
# throttling, missing permission). Only the first may fall through to create;
# anything else aborts with az's own message. (Same contract as deploy.sh; Graph
# says "does not exist", ARM says "could not be found".)
exists() {
  local err
  if err=$("$@" -o none 2>&1); then
    return 0
  elif grep -qiE 'ResourceNotFound|ResourceGroupNotFound|could not be found|was not found|does not exist' <<<"$err"; then
    return 1
  else
    echo "$err" >&2
    exit 1
  fi
}

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# az reads @file arguments itself. With MSYS_NO_PATHCONV set, Windows az can't
# see a Git Bash path like /tmp/..., so hand it the native path when cygpath
# exists (Git Bash); elsewhere the path is already native.
native() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

# Resolve the Azure targets first, so a typo or an undeployed job fails before
# anything is created in Entra.
if ! job_id=$(az containerapp job show -n "$AZ_JOB" -g "$AZ_RG" --query id -o tsv | tr -d '\r') || [[ -z "$job_id" ]]; then
  echo "Could not read job $AZ_JOB in $AZ_RG. Deploy it first (deploy.sh); stopping." >&2
  exit 1
fi
subscription_id=$(az account show --query id -o tsv | tr -d '\r')
tenant_id=$(az account show --query tenantId -o tsv | tr -d '\r')

# --- App registration -------------------------------------------------------
# Looked up by display name, which is not unique in Entra. Two matches means we
# can't tell which one is ours, so stop rather than guess.
app_ids=$(az ad app list --display-name "$APP_NAME" --query "[].appId" -o tsv | tr -d '\r')
app_count=$(grep -c . <<<"$app_ids" || true)
if [[ "$app_count" -gt 1 ]]; then
  echo "More than one app registration is named '$APP_NAME'. Delete the extras or set APP_NAME; stopping." >&2
  exit 1
elif [[ "$app_count" -eq 1 ]]; then
  app_id="$app_ids"
  echo "App registration $APP_NAME exists."
else
  echo "Creating app registration $APP_NAME..."
  app_id=$(az ad app create --display-name "$APP_NAME" --sign-in-audience AzureADMyOrg \
    --query appId -o tsv | tr -d '\r')
fi

# --- Service principal ------------------------------------------------------
# The app registration is the definition; the service principal is the identity
# that actually receives role assignments.
if exists az ad sp show --id "$app_id"; then
  echo "Service principal exists."
else
  echo "Creating service principal..."
  az ad sp create --id "$app_id" -o none
fi
sp_object_id=$(az ad sp show --id "$app_id" --query id -o tsv | tr -d '\r')

# --- Federated credential ---------------------------------------------------
# Never overwrite one with the same name but a different trust: that would
# silently change who can deploy. Surface it and let a human decide.
existing=$(az ad app federated-credential list --id "$app_id" \
  --query "[?name=='$CRED_NAME'].[issuer,subject]" -o tsv | tr -d '\r')
if [[ -n "$existing" ]]; then
  want=$(printf '%s\t%s' "$ISSUER" "$SUBJECT")
  if [[ "$existing" != "$want" ]]; then
    echo "Federated credential '$CRED_NAME' exists with a different issuer/subject:" >&2
    echo "  found:    ${existing//$'\t'/  }" >&2
    echo "  expected: $ISSUER  $SUBJECT" >&2
    echo "Delete it yourself if the change is intended; stopping." >&2
    exit 1
  fi
  echo "Federated credential $CRED_NAME exists."
else
  echo "Creating federated credential $CRED_NAME ($SUBJECT)..."
  cat >"$tmp/fedcred.json" <<JSON
{
  "name": "$CRED_NAME",
  "issuer": "$ISSUER",
  "subject": "$SUBJECT",
  "description": "GitHub Actions deploy, environment production",
  "audiences": ["api://AzureADTokenExchange"]
}
JSON
  az ad app federated-credential create --id "$app_id" --parameters "@$(native "$tmp/fedcred.json")" -o none
fi

# --- Roles ---------------------------------------------------------------------
# Custom roles, not Contributor (review, D53). Contributor on the job includes
# Microsoft.App/jobs/listSecrets/action, which reads every secret value (database
# string, ntfy topic, heartbeat URL, OTLP token). The deployer needs to read and
# write the job definition and nothing more. Even so, write access can point the
# job at any image, and that image runs with the secrets mounted: the real lock
# is the federated subject above (main-only `production` environment), not this
# role. The role narrows the blast radius; it is not the boundary.
#
# Both roles are scoped to one resource (assignable there and assigned there),
# never the resource group or subscription.

# usage: ensure_role NAME DESCRIPTION SCOPE ACTION...
ensure_role() {
  local name="$1" desc="$2" scope="$3"; shift 3
  local n actions
  n=$(az role definition list --custom-role-only true --name "$name" \
    --query "length(@)" -o tsv | tr -d '\r')
  if [[ "$n" -gt 0 ]]; then
    echo "Custom role '$name' exists."
    return
  fi
  echo "Creating custom role '$name'..."
  actions=$(printf '"%s",' "$@")
  cat >"$tmp/role.json" <<JSON
{
  "Name": "$name",
  "IsCustom": true,
  "Description": "$desc",
  "Actions": [${actions%,}],
  "NotActions": [],
  "DataActions": [],
  "NotDataActions": [],
  "AssignableScopes": ["$scope"]
}
JSON
  az role definition create --role-definition "@$(native "$tmp/role.json")" -o none
}

has_role() {  # usage: has_role ROLE SCOPE
  local n
  n=$(az role assignment list --assignee-object-id "$sp_object_id" --scope "$2" \
    --role "$1" --query "length(@)" -o tsv 2>/dev/null | tr -d '\r') || n=0
  [[ "${n:-0}" -gt 0 ]]
}

# usage: ensure_assignment ROLE SCOPE LABEL
ensure_assignment() {
  local role="$1" scope="$2" label="$3" attempt
  if has_role "$role" "$scope"; then
    echo "'$role' on $label already assigned."
    return
  fi
  echo "Assigning '$role' on $label..."
  # A new custom role definition can take a minute to propagate, and the
  # assignment is refused until it has. Retry briefly, then fail loudly.
  for attempt in 1 2 3 4 5 6; do
    if az role assignment create --assignee-object-id "$sp_object_id" \
         --assignee-principal-type ServicePrincipal \
         --role "$role" --scope "$scope" -o none; then
      return
    fi
    echo "  not visible yet (attempt $attempt/6); waiting 10s..." >&2
    sleep 10
  done
  echo "Could not assign '$role' on $label; re-run in a few minutes." >&2
  exit 1
}

ensure_role "$DEPLOYER_ROLE" \
  "Read and update one Container Apps job's definition. No secret values (no listSecrets)." \
  "$job_id" \
  "Microsoft.App/jobs/read" "Microsoft.App/jobs/write"
ensure_assignment "$DEPLOYER_ROLE" "$job_id" "$AZ_JOB"

# --- Optional: environment join ---------------------------------------------
# Updating a job can require permission to "join" its managed environment. We try
# the deploy without it first (least privilege) and only set JOIN_ENV=1 if
# `job update` is refused for missing Microsoft.App/managedEnvironments/join/action.
if [[ "$JOIN_ENV" == "1" ]]; then
  if ! env_id=$(az containerapp env show -n "$AZ_ENV" -g "$AZ_RG" --query id -o tsv | tr -d '\r') || [[ -z "$env_id" ]]; then
    echo "Could not read environment $AZ_ENV in $AZ_RG; stopping." >&2
    exit 1
  fi
  ensure_role "$JOINER_ROLE" \
    "Join a Container Apps managed environment, and nothing else." \
    "$env_id" \
    "Microsoft.App/managedEnvironments/join/action"
  ensure_assignment "$JOINER_ROLE" "$env_id" "$AZ_ENV"
fi

echo
echo "Done. Add these as VARIABLES (not secrets) on the GitHub 'production' environment:"
echo "  AZURE_CLIENT_ID       = $app_id"
echo "  AZURE_TENANT_ID       = $tenant_id"
echo "  AZURE_SUBSCRIPTION_ID = $subscription_id"
echo
# They are identifiers, not credentials. With OIDC there is no secret to leak:
# knowing these values lets nobody deploy, because Azure only accepts a token
# whose subject matches the federated credential above.
echo "These are identifiers, not credentials. The federated subject is the lock:"
echo "  $SUBJECT"
