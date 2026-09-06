# Overnight Precipitation Alerter

Checks the NWS forecast for tonight's overnight window and pushes a notification
to my phone if rain or snow is coming — enough detail to know whether it's
patio cushions or a shovel.

This is a portfolio project for a senior backend job search. That changes how it
gets built. Read the next section before writing any code.

## How we work on this repo

**I write the parts I'd have to defend in an interview. You scaffold.**

| You (Claude) | Me (Michael) |
|---|---|
| `.csproj`, solution files, folder layout | The decision engine |
| Dockerfile, docker-compose | All tests |
| GitHub Actions yaml | Time-zone / overnight-window logic |
| EF Core plumbing, migrations boilerplate | Idempotency design and enforcement |
| Azure CLI command sequences | The README design doc |
| Answering questions, reviewing my code, pushing back on design | |

If I ask you to write something in the right-hand column, say so and ask whether
I'm sure. Slower is the point — I get asked about this repo in interviews.

Review is always welcome. Write my tests for me is not.

## Stack

- **.NET 10** (LTS). Not .NET 8 — it goes EOL Nov 10, 2026.
- Single solution: `Api` (minimal API) + `Worker` (`BackgroundService`)
- **Weather:** NWS `api.weather.gov`. Two-step: `/points/{lat},{lon}` →
  `properties.forecastHourly`. **Requires a `User-Agent` header with contact
  info or it rejects the request.** Gridpoint mapping is stable — cache it.
  Open-Meteo goes behind the same interface later.
- **Notifications:** ntfy. Public topics are unauthenticated — anyone who guesses
  the topic can read *and* publish. Long random topic, never committed.
- **Data:** Postgres via EF Core. Locations, AlertRules, AlertHistory.
- **Resilience:** Polly — retry with backoff, circuit breaker, timeout.
- **Observability:** OpenTelemetry, traces spanning fetch → evaluate → notify.
- **Deploy:** Azure Container Apps Job on a nightly cron.
- **Testing:** xUnit. WireMock.Net for the provider. Testcontainers for the DB.

### Worker design note

A scheduled Container Apps Job is the right shape for a once-a-night workload,
but `BackgroundService` is what interviews ask about. The worker supports a
`--once` flag: long-running mode locally, once-and-exit deployed. Keep both
working — the ability to explain the choice is part of the deliverable.

## The three problems that make this more than a script

1. **Time zones.** The window is local (8pm–7am), the API returns ISO-8601 with
   offsets, DST shifts it twice a year. Tests must prove this.
2. **Idempotency.** Exactly one alert per location per night. Key on
   `(locationId, forecastNightDate)`, enforced in the **database**, not memory.
3. **Silence is ambiguous.** No notification means "no precipitation" *or* "the
   job died three weeks ago." Needs a heartbeat or dead-man's-switch. This is the
   most senior thing in the project — don't quietly drop it.

## Block plan

One block per weekend. Ship working, then harden — infrastructure-first is how
side projects die.

| # | Block | Done when |
|---|---|---|
| 1 | Walking skeleton | Console app, NWS → dumb threshold → ntfy. **Notification on my phone.** Everything hardcoded. |
| 2 | Decision engine | Pure function. Overnight window + DST correct. Table-driven tests over captured real payloads. |
| 3 | Containerize + deploy | Dockerfile, Container Apps Job on nightly cron. Runs without my machine. |
| 4 | Persistence + API | EF Core, migrations, minimal API. Idempotency constraint in the schema. Testcontainers test. |
| 5 | Resilience + 2nd provider | Polly, Open-Meteo, WireMock tests for retry and breaker paths. |
| 6 | Observability | OTel traces + metrics. Heartbeat. |
| 7 | CI/CD | Actions: build → test → container → push → deploy. |
| 8 | README as design doc | What I built, what I traded away, why. Diagrams. |

**Current block: 1.**

Block 1 leaves debt *on purpose* — hardcoded coordinates, naive time handling,
topic in source, no tests, no retry. Block 2 fixes it. Don't fix it early; the
commit where it was wrong is part of the story.

## Config

Set before Block 1, none committed:

- `PRECIP_LAT` / `PRECIP_LON` — 4 decimal places max (NWS limit)
- `PRECIP_NTFY_TOPIC` — long and random
- `PRECIP_USER_AGENT` — contact info for NWS
- Overnight window: 8:00 PM → 7:00 AM local (America/New_York)
- Block 1 threshold: any hour in window with `probabilityOfPrecipitation >= 50`

## Conventions

- Commit early and often. Steady weekly history is itself a hiring signal.
- Public repo from commit one. Nothing secret in source, ever.
- Every non-obvious tradeoff gets a line in the README when it's made, not at
  block 8 when I've forgotten why.
