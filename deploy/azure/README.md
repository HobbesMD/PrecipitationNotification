# Azure deployment (Container Apps Job)

`deploy.sh` creates or updates a nightly Azure Container Apps Job that runs the
alerter container once and exits: 0 on success, non-zero on failure (1–5 from
the app, or a crash or timeout). It is safe to re-run: it checks each resource,
then creates or updates it. A failed check that isn't "not found" (expired
login, unregistered provider) stops the script instead of creating anything.

## Prerequisites

- Azure CLI, logged in: `az login`
- The containerapp extension and resource providers. This is one-time, and
  required before the first run; the script checks it and stops if they
  aren't registered:

  ```bash
  az extension add --name containerapp --upgrade
  az provider register -n Microsoft.App --wait
  az provider register -n Microsoft.OperationalInsights --wait
  ```

- The image pushed to GHCR, tagged with a git sha:
  `ghcr.io/hobbesmd/precipitation-notification:<git-sha>`. The script only
  accepts a 7–40 character hex tag or an `@sha256:` digest, so every run maps
  to a commit. `:latest`, `:main` and untagged images are rejected.
- The GHCR package set to **public** (once, in the package's settings on
  GitHub; new packages start private even on a public repo).

## Variables

Required:

| Variable | Notes |
|---|---|
| `IMAGE` | Full image reference with a sha tag |
| `PRECIP_LAT`, `PRECIP_LON` | Stored as job secrets (they pin your home) |
| `PRECIP_NTFY_TOPIC` | Stored as a job secret |
| `PRECIP_USER_AGENT` | Contact info for NWS; plain env var |

Optional (defaults): `AZ_RG` (`rg-precip`), `AZ_LOCATION` (`eastus`),
`AZ_ENV` (`cae-precip`), `AZ_JOB` (`job-precip-nightly`).

The window is fixed by the script at `PRECIP_WINDOW_START=20:00` and
`PRECIP_WINDOW_END=07:00`.

**Never type a secret into `export NAME=value` at a prompt:** it lands in
`~/.bash_history`. The values already live in the gitignored `.env`, so load
them from there.

## Running the script

From the repo root, in a subshell, so the values are never typed, never in
history, and gone afterwards:

```bash
(
  while IFS='=' read -r k v; do
    v="${v#\"}"; v="${v%\"}"          # strip one pair of quotes, as the app does
    export "$k=$v"
  done < <(grep -E '^PRECIP_[A-Z_]+=' .env | tr -d '\r')
  IMAGE=ghcr.io/hobbesmd/precipitation-notification:<git-sha> deploy/azure/deploy.sh
)
```

This reads `.env` line by line instead of `source`-ing it. The User-Agent
contains spaces and parentheses, which `source` would try to execute.

It creates the resource group, a Consumption-plan environment (with a Log
Analytics workspace created automatically), and the job on the cron
`0 23 * * *`. Container Apps cron is UTC only: 23:00 UTC is 7 PM EDT / 6 PM EST,
always before the 8 PM local window opens.

To ship a new version, re-run it with the new `IMAGE`.

### Private image (optional)

The image is public, so no registry credentials are needed, and none should be
set. If it ever goes private, Azure needs a GitHub **classic** PAT with only
`read:packages` (fine-grained tokens don't cover GHCR). Give it a 1-year expiry
and a calendar reminder: an expired PAT makes the job silently stop pulling,
with no run, no alert and no error pushed to you. Read it without echo or
history:

```bash
read -rp 'GitHub user: ' GHCR_USER; read -rsp 'PAT: ' GHCR_PAT; echo
export GHCR_USER GHCR_PAT
```

Set both or neither. To rotate, read the new one and re-run (`registry set`).
If both are unset on an existing job that still has `ghcr.io` credentials,
the script removes them. GHCR rejects a pull with a bad PAT even for a public
image, so a stale one would break the job when it expires. `registry remove`
may leave the old password secret on the job; it's harmless, but
`az containerapp job secret list` shows it.

## Manual test run

Raise the chance of a notification by forcing the threshold to 0, so any
forecast triggers an alert. The reliable form is update, start, revert:

```bash
az containerapp job update -n "$AZ_JOB" -g "$AZ_RG" --set-env-vars PRECIP_PROBABILITY_THRESHOLD=0
az containerapp job start  -n "$AZ_JOB" -g "$AZ_RG"
# after the execution finishes:
az containerapp job update -n "$AZ_JOB" -g "$AZ_RG" --remove-env-vars PRECIP_PROBABILITY_THRESHOLD
```

Do not forget the revert, or every night will alert regardless of the forecast.
(Note: `deploy.sh` also clears the override, because it uses `--replace-env-vars`.)

`az containerapp job start` itself accepts `--image`, `--container-name`, `--cpu`,
`--memory`, `--args`, `--command` and `--env-vars`, so a one-off override is
possible without touching the job definition, e.g.
`--image "$IMAGE" --container-name "$AZ_JOB" --env-vars ...`. However, the
override replaces the container template, so you would have to restate every env
var (including the `secretref:` ones), and this form has not been verified
against a live job; prefer update-then-revert.

For a plain run with the real threshold:

```bash
az containerapp job start -n "$AZ_JOB" -g "$AZ_RG"
```

## Checking results

```bash
az containerapp job execution list -n "$AZ_JOB" -g "$AZ_RG" -o table
az containerapp job logs show -n "$AZ_JOB" -g "$AZ_RG" --container "$AZ_JOB" --format text --tail 100
```

`logs show` requires `--container` (the container name; `create` names it after
the job unless told otherwise) and defaults to the latest execution; use
`--execution <name>` for another. Console logs can lag by a minute or two.

Or query Log Analytics (portal: the environment's workspace, Logs). The table is
usually `ContainerAppConsoleLogs_CL`, but the name can differ by workspace or log
destination setting, so check the table list first:

```kusto
ContainerAppConsoleLogs_CL
| where ContainerJobName_s == "job-precip-nightly"
| order by TimeGenerated desc
| take 100
```

(Column names such as `ContainerJobName_s` may vary too; `ContainerAppConsoleLogs_CL
| take 5` shows the real schema.)

Execution status `Succeeded` means exit 0; `Failed` means a non-zero exit (1–5 from the app, or a crash or timeout) after the one
retry.

## Cost

Consumption plan, billed per second of use within a monthly free grant. A job at
0.25 vCPU / 0.5 GiB running seconds a night should fall inside the free grant.
Log Analytics ingestion is the only likely cost, and this app logs very little.

## Teardown

```bash
az group delete -n "$AZ_RG"
```

This removes the environment, job, secrets and the Log Analytics workspace.
