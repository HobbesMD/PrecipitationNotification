# Azure deployment (Container Apps Job)

`deploy.sh` creates or updates a nightly Azure Container Apps Job that runs the
alerter container once and exits (0 on success, 1-5 on failure). It is safe to
re-run: it checks each resource and creates or updates it.

## Prerequisites

- Azure CLI, logged in: `az login`
- The containerapp extension and resource providers (one-time):

  ```bash
  az extension add --name containerapp --upgrade
  az provider register --namespace Microsoft.App
  az provider register --namespace Microsoft.OperationalInsights
  ```

- Docker, and the image already pushed to GHCR, tagged with a git sha:
  `ghcr.io/hobbesmd/precipitation-notification:<git-sha>`. The script rejects
  `:latest` and untagged images so every run maps to a commit.

## Variables to export

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

### Registry credentials (private image only)

While the repo is private, Azure needs credentials to pull from GHCR. Create a
GitHub **classic** personal access token with only the `read:packages` scope and
a 1-year expiry, and set a calendar reminder for renewal. An expired PAT makes
the job silently stop pulling the image: no run, no alert, no error pushed to
you. Export it in your own shell; never write it to a file:

```bash
export GHCR_USER=<github-username>
export GHCR_PAT=<token>   # type it at the prompt or paste it; do not save it
```

Set both or neither. If the repo/image becomes public, set neither.

To rotate the PAT, export the new one and re-run `deploy.sh` (it runs
`az containerapp job registry set` on an existing job).

## Running the script

```bash
export IMAGE=ghcr.io/hobbesmd/precipitation-notification:<git-sha>
export PRECIP_LAT=... PRECIP_LON=... PRECIP_NTFY_TOPIC=... PRECIP_USER_AGENT=...
deploy/azure/deploy.sh
```

It creates the resource group, a Consumption-plan environment (Log Analytics
workspace auto-created), and the job on the cron `0 23 * * *`. Container Apps
cron is UTC only: 23:00 UTC is 7 PM EDT / 6 PM EST, always before the 8 PM local
window opens.

To ship a new version, export the new `IMAGE` and re-run.

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

Execution status `Succeeded` means exit 0; `Failed` means exit 1-5 after the one
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
