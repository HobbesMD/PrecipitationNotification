# Azure deployment (Container Apps Job)

`deploy.sh` creates or updates a nightly Azure Container Apps Job that runs the
alerter container once (`Worker --once`) and exits: 0 on success, non-zero on
failure (1–6 from the app, or a crash or timeout). It is safe to re-run: it
checks each resource, then creates or updates it. A failed check that isn't
"not found" (expired login, unregistered provider) stops the script instead of
creating anything.

State lives in Postgres on **Neon**, not in the job. Locations, coordinates, the
overnight window and the alert threshold are database rows, managed through the
local-only admin API (`src/Api`). The job's only configuration is the
connection string, the ntfy topic and the NWS User-Agent. The script never runs
migrations: you apply them explicitly (see [Migrate](#migrate)).

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
  `ghcr.io/hobbesmd/precipitation-notification:<git-sha>`. CI pushes it now, on
  every merge to main ([Block 7](#block-7-continuous-deployment)); push by hand
  only for the very first deploy or in an emergency. The script only
  accepts a 7–40 character hex tag or an `@sha256:` digest, so every run maps
  to a commit. `:latest`, `:main` and untagged images are rejected.
- The GHCR package set to **public** (once, in the package's settings on
  GitHub; new packages start private even on a public repo).
- A Neon project and database (Postgres 17, AWS US East, the direct host, with
  connection pooling **off**). Neon's Connect panel gives a
  `postgresql://user:pass@host/db?sslmode=require&channel_binding=require` URL.
  Npgsql doesn't take URLs, so convert it to key=value form:
  `Host=...;Port=5432;Database=...;Username=...;Password=...;SSL Mode=VerifyFull;Channel Binding=Require`.
  `VerifyFull` also checks the certificate and hostname; `Require` only
  encrypts. Put it in a gitignored **`.env.neon`** as the single line
  `ConnectionStrings__Precip=...`. It is kept apart from `.env` because `.env`
  points at your local Docker database, and the two must never be confused.
  `.env.*` is already in `.gitignore` and `.dockerignore`. See D29.

## Variables

Required:

| Variable | Notes |
|---|---|
| `IMAGE` | Full image reference with a sha tag |
| `ConnectionStrings__Precip` | Neon connection string; stored as the job secret `db-conn` (it carries the password) |
| `PRECIP_NTFY_TOPIC` | Stored as the job secret `ntfy-topic` |
| `PRECIP_USER_AGENT` | Contact info for NWS; plain env var |
| `PRECIP_HEARTBEAT_URL` | healthchecks.io ping URL; stored as the job secret `heartbeat-url` (see [Block 6](#block-6-heartbeat-and-telemetry)) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Grafana Cloud OTLP gateway URL; plain env var |
| `OTEL_EXPORTER_OTLP_HEADERS` | `Authorization=Basic%20...`; stored as the job secret `otlp-headers` |

Optional (defaults): `AZ_RG` (`rg-precip`), `AZ_LOCATION` (`eastus`),
`AZ_ENV` (`cae-precip`), `AZ_JOB` (`job-precip-nightly`).

The script also sets `OTEL_EXPORTER_OTLP_PROTOCOL` (`http/protobuf`),
`OTEL_SERVICE_NAME` (`precip-worker`) and `OTEL_RESOURCE_ATTRIBUTES`
(`service.version` taken from the image's sha tag or digest, plus
`deployment.environment.name=production`), so every trace names the commit that
produced it.

`PRECIP_API_KEY` and `Worker__Interval` are local-only (the admin API and the
long-running mode). They never go to Azure.

The job passes `--once` as its container argument, so each execution runs one
pass and exits. On an existing job the script also removes the obsolete
`precip-lat` and `precip-lon` secrets (Block 3), after the update that stops
referencing them.

**Never type a secret into `export NAME=value` at a prompt:** it lands in
`~/.bash_history`. The values already live in the gitignored `.env` and
`.env.neon`, so load them from there.

## Loading values from `.env`

Define this once per terminal. It is a function, not a value; it holds no
secrets:

```bash
load_env() {   # usage: load_env FILE...   later files win
  local f k v
  for f in "$@"; do
    while IFS='=' read -r k v; do
      v="${v#\"}"; v="${v%\"}"          # strip one pair of quotes, as the app does
      export "$k=$v"
    done < <(grep -E '^(PRECIP_[A-Z_]+|OTEL_[A-Z_]+|ConnectionStrings__[A-Za-z]+|Worker__[A-Za-z]+)=' "$f" | tr -d '\r')
  done
}
```

Every command below calls it inside a subshell `( ... )`, so the values are
never typed, never in history, and gone afterwards. It reads each file line by
line instead of `source`-ing it: the User-Agent contains spaces and
parentheses, which `source` would try to execute, and `read -r k v` keeps the
`=` and `;` inside a connection string intact. The app's own `.env` loader
never overwrites a variable that is already set, so exporting the Neon string
this way beats the local one in `.env`.

## Block 6: heartbeat and telemetry

One-time manual setup, done before the first deploy that includes Block 6.
`deploy.sh` refuses to run without all three values: there is no opt-out, because
a job with no dead-man's switch is exactly the silent failure this block exists to
prevent.

### healthchecks.io (the dead-man's switch)

1. Create a free account and a check. Schedule: cron `0 23 * * *`, time zone
   `UTC`, grace time 1 hour. That matches the job's own cron.
2. Copy the check's ping URL into your gitignored `.env` as
   `PRECIP_HEARTBEAT_URL=...`. **It is a secret:** anyone holding it can report
   "healthy" for you, so it goes in the job as the secret `heartbeat-url`, never
   in source or a log.
3. Add two integrations: **email** and **ntfy**. For ntfy use a different long
   random topic from the alerts topic. Email is the second channel on purpose: a
   dead ntfy can't report that it is dead.

What a run sends: `/start` when it begins, then `/<exit code>` when it ends
(`/0` is success). A non-zero code alerts immediately. A missing ping, because the
job never started or hung, alerts only after the grace period. Azure's one retry
(`replicaRetryLimit` 1) can therefore show "down" after a failed first attempt,
then "up" when the retry succeeds. That is correct, not a bug.

### Grafana Cloud (traces)

1. Create a free stack and open its **OpenTelemetry (OTLP)** connection tile.
2. Set `OTEL_EXPORTER_OTLP_ENDPOINT` in `.env` to the gateway URL it shows.
3. Generate a token with write-only scopes (no read), and set
   `OTEL_EXPORTER_OTLP_HEADERS` to `Authorization=Basic%20<base64 of instanceId:token>`.
   Note the `%20`: the .NET exporter needs the space URL-encoded. Build the base64
   without putting the token on a command line or in history, e.g. read it with
   `read -rs` and pipe it through `base64 -w0`.

Then deploy as in [Running the script](#running-the-script).

### Verify after deploying

```bash
az containerapp job show -n "$AZ_JOB" -g "$AZ_RG" --query "properties.configuration.secrets[].name" -o tsv
az containerapp job start -n "$AZ_JOB" -g "$AZ_RG"
```

`job show` lists `heartbeat-url` and `otlp-headers` by name only, never values.
After the execution finishes: in Grafana, Explore, the Tempo data source, search
service `precip-worker` and find the trace; in healthchecks.io the check turns
green.

### Drill: prove the alarm reaches your phone

Make one run exit non-zero and confirm the healthchecks.io alert reaches the
phone and the inbox. The reliable way is a **1 ms pass deadline**. The location
is skipped as "not evaluated", and the run exits **7** and pings `/7`. It
touches no data and calls no provider. Set it on the job, start one run, and
remove it straight away:

```bash
az containerapp job update -n "$AZ_JOB" -g "$AZ_RG" \
  --set-env-vars Worker__PassDeadline=00:00:00.001
az containerapp job start -n "$AZ_JOB" -g "$AZ_RG"
# wait for the execution to finish (az containerapp job execution list ...), then:
az containerapp job update -n "$AZ_JOB" -g "$AZ_RG" \
  --remove-env-vars Worker__PassDeadline
az containerapp job show -n "$AZ_JOB" -g "$AZ_RG" \
  --query "properties.template.containers[0].env[].name" -o tsv   # back to the deployed set
```

Re-running `deploy.sh` also reverts it: its `--replace-env-vars` drops the
variable. Do the drill well before 23:00 UTC, so the scheduled run never sees
the override.

Expected:
- **Execution status `Failed`.** Azure's one retry (D20) runs too and also exits 7.
- **An immediate "down" alert** on the phone and by email.

Then start a normal run (`az containerapp job start -n "$AZ_JOB" -g "$AZ_RG"`)
and watch the check return to "up".

**What doesn't work** (learned the hard way, 2026-11-02):
- **Per-execution overrides** (`az containerapp job start --env-vars ...`).
  They *replace* the container's whole environment instead of merging into it.
  The run then dies with `Config error: Missing required environment variable`
  before the host starts, so no ping is sent: only the grace period catches it.
  Restating the variables doesn't help either, because an execution override
  can't resolve the job's secrets (`ContainerAppSecretRefNotFound`).
- **Pointing `Nws__BaseUrl` at a dead host.** For a location that has run before,
  the hourly forecast URL is cached on its row (D27), so the base URL is used only
  for a fresh `/points` lookup and NWS still answers.

## Block 7: continuous deployment

### How it works

Every push to main runs the tests, builds the image and pushes
`ghcr.io/hobbesmd/precipitation-notification:<sha12>` (the first 12 characters
of the commit sha) using the workflow's `GITHUB_TOKEN`. The `deploy` job, which
runs in the GitHub environment `production`, then logs in to Azure with OIDC and
runs `deploy/azure/deploy-image.sh`. That script updates only the image and
`service.version` (inside `OTEL_RESOURCE_ATTRIBUTES`); the secrets, the cron and
the sizing stay exactly as `deploy.sh` set them.

`deploy.sh` remains the tool for creating the job or changing its configuration.
After you change configuration with it, the next CI deploy just swaps the image.

### One-time setup

1. Run `setup-oidc.sh` (logged in with `az login`, with rights to create app
   registrations and role assignments):

   ```bash
   deploy/azure/setup-oidc.sh
   ```

   It creates the Entra app `gh-precip-deploy`, its service principal, one
   federated credential (`github-production`, trusting only
   `repo:HobbesMD/PrecipitationNotification:environment:production`), and the
   `Contributor` role scoped to the job alone, not the resource group. It never
   creates a client secret or certificate. Re-running is safe; if a credential
   with that name exists but trusts something else, it stops rather than
   overwrite it. It prints three values at the end.

   If a CI deploy is later refused for missing the
   `Microsoft.App/managedEnvironments/join/action` permission, re-run it with
   `JOIN_ENV=1`. That adds a custom role that grants only that action, assigned
   on the environment. It is off by default because we try the least privilege
   first. A new custom role can take a minute to propagate; the script retries.

2. In GitHub: Settings, Environments, create **`production`**. Under deployment
   branches choose "Selected branches and tags" and add `main`. Add the three
   printed values as environment **variables** (not secrets): `AZURE_CLIENT_ID`,
   `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`. They are identifiers, not
   credentials. With OIDC there is no secret at all; the federated subject is the
   lock, and the branch rule is what stops any other branch from presenting it.

3. In the GHCR package's settings, under "Manage Actions access", add this
   repository with **Write**. The package was first pushed by hand, so it isn't
   linked to the repository's `GITHUB_TOKEN` yet and the first CI push would be
   denied.

### When the migration guard stops a deploy

`deploy-image.sh` compares the commit being deployed with the one currently
deployed. If migration files changed in between, it stops with **exit 3** and
lists them. That means the new image expects a schema that Neon may not have yet,
and a deploy must never change the schema as a side effect. To proceed:

1. Apply the migrations: [`--migrate`](#migrate).
2. In GitHub: Actions, CI, "Run workflow" on `main`, with `sha` set to the
   12-character sha and `migrations_applied` ticked.

### Rollback

Run the workflow again (Actions, CI, "Run workflow") with `sha` set to the
previous 12-character sha. To see what is deployed right now:

```bash
az containerapp job show -n job-precip-nightly -g rg-precip \
  --query "properties.template.containers[0].image" -o tsv
```

A rollback across a schema change also stops at the migration guard.

### Running it locally

For a rollback without GitHub, or if Actions is down, from the repo root with
full git history (the guard needs it):

```bash
IMAGE=ghcr.io/hobbesmd/precipitation-notification:<sha12> bash deploy/azure/deploy-image.sh
```

### What CI never does

- **Hold app secrets.** The connection string, ntfy topic and heartbeat URL live
  only on the job. CI holds nothing that can read them.
- **Run migrations.** Schema changes are a deliberate, separate step (above).
- **Start an execution.** An execution override replaces the job's whole
  environment (see the [Drill](#drill-prove-the-alarm-reaches-your-phone)
  section), so a smoke run from CI would be a real run, not a test. The 23:00 UTC
  run plus the heartbeat is the smoke test: a bad deploy alerts by 00:00 UTC.

## Migrate

Run this before the first deploy, and again whenever a release adds a
migration. It applies pending migrations to Neon and exits; the job never does
it, so a deploy can't change the schema as a side effect.

```bash
( load_env .env .env.neon
  dotnet run --project src/Worker -- --migrate )
```

## Running the script

From the repo root:

```bash
( load_env .env .env.neon
  IMAGE=ghcr.io/hobbesmd/precipitation-notification:<git-sha> deploy/azure/deploy.sh )
```

It creates the resource group, a Consumption-plan environment (with a Log
Analytics workspace created automatically), and the job on the cron
`0 23 * * *`. Container Apps cron is UTC only: 23:00 UTC is 7 PM EDT / 6 PM EST,
always before the 8 PM local window opens.

To ship a new version, re-run it with the new `IMAGE`. If the release has a
migration, [Migrate](#migrate) first.

## Add the location

The job has nothing to forecast until a location row exists. Add it through the
admin API, run locally against Neon. Terminal A starts the API:

```bash
( load_env .env .env.neon
  ASPNETCORE_URLS=http://localhost:5080 dotnet run --project src/Api )
```

Terminal B posts the location. The key comes from `.env`, and the coordinates
are `PRECIP_LAT` / `PRECIP_LON` in your own `.env` (the app no longer reads
them, so they are not in `.env.example`; only this step uses them). They go in
through stdin, so they are never typed and never on a command line:

```bash
( load_env .env
  printf '{"name":"Home","latitude":%s,"longitude":%s,"rule":{"probabilityThreshold":50,"windowStart":"20:00","windowEnd":"07:00"}}' \
    "$PRECIP_LAT" "$PRECIP_LON" |
    curl -sS -o /dev/null -w '%{http_code}\n' -X POST http://localhost:5080/locations \
      -H "X-Api-Key: $PRECIP_API_KEY" -H 'Content-Type: application/json' --data-binary @- )
```

Expect `201`. A `400` names the bad field without echoing its value (D22).
Then list locations to find the id:

```bash
( load_env .env
  curl -sS http://localhost:5080/locations -H "X-Api-Key: $PRECIP_API_KEY" )
```

The response contains the coordinates. That is fine on your own screen; do not
paste it into logs, issues or a chat. Coordinates are immutable: to move a
location, `DELETE /locations/{id}` and create a new one (D26). A location that
already has alert history can't be deleted; its history is kept.

Stop the API (Ctrl+C) when done. It is a local admin tool and is not deployed.

## Private image (optional)

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

The threshold now lives in the location's rule (a database row), not in the job,
so the test never touches the job definition. Set the threshold to 0 so any
forecast triggers an alert, start the job, then put the rule back. With the API
running as in [Add the location](#add-the-location), and `<id>` from
`GET /locations`:

```bash
# 1. Threshold 0. The PUT replaces the whole rule, so restate the window.
( load_env .env
  curl -sS -o /dev/null -w '%{http_code}\n' -X PUT http://localhost:5080/locations/<id>/rule \
    -H "X-Api-Key: $PRECIP_API_KEY" -H 'Content-Type: application/json' \
    -d '{"probabilityThreshold":0,"windowStart":"20:00","windowEnd":"07:00"}' )

# 2. Run it.
az containerapp job start -n "$AZ_JOB" -g "$AZ_RG"

# 3. After the execution finishes, revert to the real rule (50 / 20:00 / 07:00).
( load_env .env
  curl -sS -o /dev/null -w '%{http_code}\n' -X PUT http://localhost:5080/locations/<id>/rule \
    -H "X-Api-Key: $PRECIP_API_KEY" -H 'Content-Type: application/json' \
    -d '{"probabilityThreshold":50,"windowStart":"20:00","windowEnd":"07:00"}' )
```

Do not forget the revert, or every night will alert regardless of the forecast.
Unlike Block 3's env-var override, `deploy.sh` does **not** clear this: it is
data, and a re-deploy leaves it alone.

**A successful alert uses up that night's key.** Once a run has alerted for a
location and night, the unique key `(location, night)` is taken, so the real
23:00 UTC run the same night logs "already sent" and stays silent even if the
forecast changed. If you test in the afternoon, you are spending tonight's
alert. To re-arm, delete that night's `alert_history` row in Neon's SQL editor.

For a plain run with the real rule:

```bash
az containerapp job start -n "$AZ_JOB" -g "$AZ_RG"
```

## Prove idempotency

Exactly one alert per location per night, enforced by the database. To see it
from the deployed job, with the threshold at 0 as above and tonight's key not
yet used, start the job twice in a row:

```bash
az containerapp job start -n "$AZ_JOB" -g "$AZ_RG"
# wait for it to finish (execution list below), then:
az containerapp job start -n "$AZ_JOB" -g "$AZ_RG"
az containerapp job execution list -n "$AZ_JOB" -g "$AZ_RG" -o table
```

Expected: both executions `Succeeded`, the first logs that it sent the alert, the
second logs that the night was already sent, and the phone buzzes exactly once.
Check each execution's log with `--execution <name>` (see below). Revert the
threshold afterwards.

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

Execution status `Succeeded` means exit 0; `Failed` means a non-zero exit (1–6 from the app, or a crash or timeout) after the one
retry.

## Cost

Consumption plan, billed per second of use within a monthly free grant. A job at
0.25 vCPU / 0.5 GiB running seconds a night should fall inside the free grant.
Log Analytics ingestion is the only likely cost, and this app logs very little.
Neon's free tier scales the database to zero when idle, so the first connection
of the night can be slow; the job's 300-second timeout covers that.

## Teardown

```bash
az group delete -n "$AZ_RG"
```

This removes the environment, job, secrets and the Log Analytics workspace. It
does not touch the Neon database; delete that in the Neon console.
