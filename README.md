# PrecipitationNotification

A project for sending notifications when it is predicted to rain or snow, particularly overnight.

## How this was built

Built with an AI pair (Claude Code); commits it wrote carry a `Co-Authored-By`
trailer. Design decisions are mine: each one is recorded in the
[decision log](docs/decisions.md#decision-log) with the alternative I rejected and the tests
that prove it.

## Running

```
cp .env.example .env   # then fill in real values
docker compose up -d                                     # local Postgres 17
dotnet run --project src/Worker -- --migrate             # apply EF migrations (never automatic)
dotnet run --project src/Api                             # admin API: POST /locations (X-Api-Key)
dotnet run --project src/Worker -- --once                # one pass over every active location
dotnet run --project src/Worker -- --once --dry-run      # print the message, don't send or record it
dotnet run --project src/Worker                          # long-running: a pass now, then every Worker__Interval
dotnet test                                              # DB tests need Docker (Testcontainers)

docker compose up -d aspire-dashboard                    # local traces/metrics/logs UI: http://localhost:18888
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 dotnet run --project src/Worker -- --once --dry-run

docker build -t precip .
docker run --rm precip --self-check                # image has real time-zone data? (exit 0/5)
docker run --rm --env-file .env precip --once --dry-run   # full pipeline in a UTC container
                                                          # (ConnectionStrings__Precip must reach the DB
                                                          #  from inside the container, e.g. host.docker.internal)
```

Deployed as an Azure Container Apps Job on a nightly cron: see
[deploy/azure/README.md](deploy/azure/README.md).

For each active location in Postgres, it fetches the NWS hourly forecast and
raw gridpoint amounts, works out the next unfinished overnight window (8 PM →
7 AM by default) in the location's own time zone, and publishes one ntfy message
if any hour in it is at or above the location's probability threshold. The
message says rain, snow or ice, and how much. A unique `(location, night)` row
in the database makes any later run for the same night a no-op. The one
deliberate exception is a lost ntfy response, which is resent rather than
risk a missed alert ([D25](docs/decisions.md#d25-one-alert-per-location-per-night-a-locked-row-at-least-once)).

