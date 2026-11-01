# PrecipitationNotification

A project for sending notifications when it is predicted to rain or snow, particularly overnight.

## How this was built

Built with an AI pair (Claude Code); commits it wrote carry a `Co-Authored-By`
trailer. Design decisions are mine: each one is recorded in the
[decision log](#decision-log) with the alternative I rejected and the tests
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
risk a missed alert ([D25](#d25-one-alert-per-location-per-night-a-locked-row-at-least-once)).

## Debt ledger

Block 1 was a walking skeleton with deliberate shortcuts; each is repaid in a
later block. Repaid rows stay, struck through, so the history is visible.

| Shortcut | Repaid in |
|---|---|
| ~~Overnight window is always *today 20:00 → tomorrow 07:00*, regardless of when the job runs.~~ | ✅ Block 2 ([D2](#d2-which-night-a-run-evaluates)) |
| ~~On the fall-back night the notification prints two identical "1 AM" lines.~~ *(Originally logged as "double-counts the repeated 01:00–02:00 hour". Captured payloads showed that was wrong: both 1 AM hours are real, distinct hours, and that night's window is genuinely 12 hours long. See [D3](#d3-window-edges-are-utc-instants-dst-handled-at-the-edges-only).)* | ✅ Block 2 ([D8](#d8-the-notification-answers-cushions-or-shovel-and-stays-unambiguous-on-dst-nights)) |
| ~~`PrecipDecision` is throwaway naive code, not a tested pure function. No captured-payload tests.~~ | ✅ Block 2 ([D9](#d9-probability-decides-whether-to-alert-amounts-decide-how-loudly)) |
| ~~NWS gridpoint URL is re-fetched every run instead of cached.~~ *(Moved from Block 2: the cache belongs on the Location row, and a container has no persistent disk to cache on.)* | ✅ Block 4 ([D27](#d27-the-gridpoint-and-time-zone-are-cached-on-the-location-row)) |
| Spring-forward test payload is hand-built (`tests/Fixtures/nws/synthetic-dst-springforward`). *(New in Block 2.)* | Real capture ~2027-03-08 |
| ~~Coordinates and topic come from `.env` with no schema; only the window and threshold are validated.~~ Coordinates, window and threshold are now rows with schema constraints. The topic deliberately stays a job secret. | ✅ Block 4 ([D26](#d26-rules-and-coordinates-are-data-their-invariants-live-in-the-schema)) |
| ~~No retry, backoff, circuit breaker, or timeout tuning on the HTTP calls.~~ | ✅ Block 5 ([D33](#d33-every-forecast-get-runs-through-one-polly-pipeline-10-s-attempt-3-tries-30-s-total-a-breaker-per-provider), [D34](#d34-ntfy-is-retried-only-when-the-request-provably-never-arrived)) |
| ~~Single weather provider, no abstraction seam.~~ ~~Seam exists ([D7](#d7-nws-responses-map-onto-provider-neutral-types-surprises-fail-loudly)); second provider not yet plugged in.~~ | ✅ Block 5 ([D36](#d36-open-meteo-is-the-fallback-a-fallback-night-keeps-the-same-key-and-says-where-it-came-from)) |
| ~~No persistence and no idempotency — every run that clears the threshold sends a notification.~~ | ✅ Block 4 ([D25](#d25-one-alert-per-location-per-night-a-locked-row-at-least-once)) |
| ~~No tracing or metrics.~~ | ✅ Block 6 ([D41](#d41-telemetry-speaks-otlp-only-the-backend-is-configuration), [D43](#d43-whats-measured-and-why-metric-tags-never-name-a-location)) |
| ~~Silence is ambiguous: "no precip" and "the job is dead" look identical. No heartbeat.~~ | ✅ Block 6 ([D39](#d39-the-heartbeat-is-an-outside-watcher-healthchecksio-pinged-at-start-and-with-the-exit-code)) |
| ~~Console app only — no container, no schedule, runs from a dev machine.~~ | ✅ Block 3 ([D18](#d18-runtime-image-chiseled--extra-proved-by---self-check), [D19](#d19-the-nightly-run-is-2300-utc)) |
| Image build, push and deploy are manual (`docker push`, `deploy/azure/deploy.sh`). CI only tests and builds. *(New in Block 3.)* | Block 7 |
| A lost ntfy response plus the job's one retry can send the same alert twice ([D20](#d20-one-retry-and-the-duplicate-it-can-cause)). *(New in Block 3.)* Narrowed by D25 to that one ambiguous case, which is now kept on purpose: a duplicate is better than a missed alert. | Accepted ([D25](#d25-one-alert-per-location-per-night-a-locked-row-at-least-once)) |
| Actions are pinned to major tags, not commit SHAs. Low risk while CI holds no secrets ([D24](#d24-review-deploysh-fails-closed)). *(New in Block 3.)* | Block 7 |
| The admin API runs only on the dev box; it isn't deployed ([D30](#d30-the-admin-api-is-local-only-behind-an-api-key)). *(New in Block 4.)* | Block 7 |
| One ntfy topic for every location. Per-location topics would mean storing a publish-capable secret in the database. *(New in Block 4.)* | Unscheduled |
| The WireMock-based resilience tests (`NtfyResilienceTests`, `NwsBreakerIsolationTests`) occasionally time out on a cold first build; reproduced on `main` before Block 6 touched anything. *(New in Block 6.)* | Unscheduled |
| The Open-Meteo capture is a dry week, so wet-night mapping is proven only with hand-built payloads ([D35](#d35-open-meteo-maps-onto-the-same-types-and-its-hours-end-at-their-timestamp)). *(New in Block 5.)* | Real wet capture, when one comes |

## Decision log

Each entry is the decision, the alternative rejected, why, what would break it,
and the tests that prove it. Written to be read cold before an interview.

### Block 2: decision engine

#### D1. Time handling uses the BCL, not NodaTime

**Decision:** `TimeZoneInfo` + `DateTimeOffset` + `DateOnly`/`TimeOnly`, with
`TimeProvider` at the composition root for the clock.

**Rejected:** NodaTime. Its types (`LocalDate`, `ZonedDateTime`, explicit
ambiguity resolvers) make local-vs-instant mistakes hard to write, and it's the
textbook "senior" answer.

**Why:** No dependency, nothing extra to map in EF Core (Block 4), and the BCL
has had IANA zone ids on every OS since .NET 6. The price is discipline: the
type system won't stop a wall-clock comparison, so the rule is *every
comparison is between `DateTimeOffset` instants*. The tests (D2, D3) are what
enforce it.

**Watch out for:** Block 3's container image needs time-zone data (`tzdata`).
Slim/chiseled .NET images can ship without it, and then
`FindSystemTimeZoneById("America/New_York")` throws.

#### D2. Which night a run evaluates

**Decision:** the *next window that hasn't finished*. `NightWindow.Resolve`
takes the local date of "now" in the location's zone, builds the windows that
start yesterday and today, and returns the first whose end is still in the
future. `NightDate` is the local date the window **starts**. A 5 PM run gets
tonight. A 2 AM run gets the night already in progress, with
`NightDate = yesterday`. A run at exactly 7:00:00 gets the next night, because
the end is exclusive.

**Rejected:** "always tonight's date," which is what Block 1 did. It's simpler,
but any run between midnight and 7 AM looks at the *following* evening and
never alerts about the night it's actually in.

**Why it matters beyond this block:** `NightDate` becomes half of the
idempotency key `(locationId, forecastNightDate)` in Block 4. A run at 11 PM and
a retry at 1 AM must produce the *same* key, or the unique constraint can't stop
the second alert.

**What would break it:** taking "today" from the UTC date. At 8 PM in New
York it's already tomorrow in UTC, so every evening run would pick the wrong
night. A Sydney test row exists specifically to catch this.

**Tests:** `NightWindowTests`. Runs at 17:00, 20:00, 23:00, 02:00, 06:59:59
and 07:00:00, with exact UTC start, end and `NightDate` asserted for each.

#### D3. Window edges are UTC instants; DST handled at the edges only

**Decision:** convert the two local edges (8 PM, 7 AM) to UTC instants once,
then compare forecast hours as instants. Never compare local wall-clock times.
Two rules decide edges that land on a DST transition:
- **Skipped time** (spring-forward gap): move forward by the gap. 02:30 in a
  02:00→03:00 gap becomes 03:30.
- **Repeated time** (fall-back): the start takes the *first* occurrence and the
  end takes the *second*. Both choices make the window as wide as possible:
  when in doubt, warn.

**Why the fall-back night is 12 hours:** clocks go 1:59 EDT → 1:00 EST, so
there really are two different 1 AM hours, and 8 PM → 7 AM spans 12 real
hours. Counting both is correct; Block 1's debt list was wrong to call it a
double-count. Spring-forward is 10 hours, because 2 AM never happens.

**Rejected:** comparing wall-clock `DateTime`s, which is what Block 1 did. It
happens to work at 8 PM and 7 AM in US zones, but it's wrong in general and
it hides the reasoning.

**Subtle bits:**
- Wall-clock values are built with `DateTimeKind.Unspecified`. With `Local`,
  `TimeZoneInfo` would read them in the *machine's* zone, which is UTC in Azure,
  so results would depend on where the code runs.
- For an ambiguous time the code picks the larger or smaller offset instead of
  trusting array order. The larger offset is always the earlier instant.
- For a skipped time, .NET doesn't document which offset `GetUtcOffset`
  returns, so the code probes both sides of the gap.

**Tests:**
- Fall-back night 2026-10-31: 12 h, 00:00Z → 12:00Z, including runs during
  both copies of the 1 AM hour.
- Spring-forward night 2027-03-13: 10 h.
- Custom zones with a transition *exactly* on 20:00 or 07:00, proving each rule.
  The wrong rule changes the length by 1 h and fails.
- Sydney (southern hemisphere, positive offset), both transitions.
- The suite was mutation-checked: swapping first/second, removing the gap
  shift, `>`→`>=`, and UTC-date-for-today each made tests fail.

**In production:** on the real fall-back night, a manual run on 2026-11-01
(execution `job-precip-nightly-s0crjtz`, Block 5 image) logged
`Night 2026-10-31: 2026-11-01T00:00:00Z -> 2026-11-01T12:00:00Z (length 12:00:00)`.
That's the 12-hour window this entry predicted, computed live in a UTC container.

#### D4. Impossible case throws instead of falling back

**Decision:** if neither yesterday's nor today's window ends after "now",
`Resolve` throws `UnreachableException`.

**Rejected:** silently trying tomorrow's window.

**Why:** today's window ends on tomorrow's date, so it *always* ends after now.
The case can only happen through a bug. A silent fallback would turn that bug
into a wrong `NightDate`, which means a wrong idempotency key and duplicate or
missing alerts with no error anywhere. Failing loudly is the only safe
behavior.

#### D5. The window must cross midnight, checked at startup

**Decision:** `AppConfig.ValidateWindow` refuses to start unless the window
start is later than the end. 20:00→07:00 is OK. 01:00→05:00, 07:00→20:00
(swapped) and 20:00→20:00 are rejected.

**Rejected:** validating inside `Resolve` on every run.

**Why:** `Resolve` always puts the end on the day after the start, so a
same-day window would silently become 28 hours. Bad config should fail once,
at startup, with a message naming the settings, not be re-discovered every
night by the decision engine.

**Tests:** `AppConfigTests`.

#### D6. Amounts are split across the window pro-rata

*(Revised by [D11](#d11-review-amounts-are-optional-a-gridpoint-failure-never-blocks-an-alert): an absent series now makes amounts unavailable rather than zero. A null value inside a present series is still 0.)*

**Context:** NWS gridpoint data gives accumulations (mm) over UTC intervals,
usually 6 hours: `2026-11-01T00:00:00+00:00/PT6H` is 8 PM–2 AM EDT. Intervals
straddle the window edges, so some fraction of each has to be counted.

**Decision:** each interval contributes `mm × overlap / duration`, which
assumes precipitation falls at an even rate across the interval. Overlap is
computed on instants, as `min(end, windowEnd) − max(start, windowStart)`, so
DST can't distort it. The window is half-open: an interval ending exactly at
8 PM counts zero.

**Rejected:** weighting each interval by the hourly probability of
precipitation. Probability is a *chance*, not an *intensity*, so using it as a
weight would invent a distribution the data doesn't contain.

**Known limitation:** if 6 mm falls in the last hour of a 6-hour interval that
straddles 7 AM, the even-rate assumption misattributes it. That's acceptable
for "cushions or shovel" precision.

**Missing values:** a null amount counts as 0, so a gap in NWS data reads as
"no precipitation." This can undercount. It's the conservative choice for
totals, because the *alert trigger* is the hourly probability, not the amount
(see D9).

**Parsing:** `validTime` is split on `/`. The start is parsed with
`DateTimeOffset` and the ISO-8601 duration with `XmlConvert.ToTimeSpan`, which
handles `PT6H` and `P1DT6H` without a hand-written regex. Malformed, zero and
negative durations throw `FormatException`.

**Tests:** `PrecipAmountsTests` covers:
- intervals fully inside, fully outside, straddling either edge, and containing
  the whole window;
- zero-length and inverted windows;
- offsets other than UTC;
- the real fixture over the fall-back night. Every value in it is 0 mm, so a
  second case with every value set to 6 mm checks that the arithmetic yields
  12 mm.

#### D7. NWS responses map onto provider-neutral types; surprises fail loudly

*(Revised by [D11](#d11-review-amounts-are-optional-a-gridpoint-failure-never-blocks-an-alert): gridpoint problems now degrade instead of failing the run.)*

**Decision:** `NwsMapper` translates NWS DTOs into `HourlyForecast` and
`PrecipAmounts`. The decision engine never sees an NWS type, which is the seam
Open-Meteo plugs into in Block 5. `NwsClient` fetches `/points`, then the hourly
forecast and the raw gridpoint data concurrently (`Task.WhenAll`), because the
two don't depend on each other.

**Fail-loud choices:**
- A gridpoint series whose unit isn't `wmoUnit:mm` throws. A silent m→mm
  conversion would hide an API change, and a 1000× error in snowfall is worse
  than a failed run.
- A missing `timeZone` throws rather than defaulting to New York, because a
  wrong zone means a wrong window and a wrong idempotency key.
- A missing probability stays `null`. It isn't coerced to 0% here because "no
  data" and "0%" are different facts. The evaluator decides what null means
  (D9).

**Rejected:** mapping inside the evaluator, or passing NWS DTOs through. Either
would couple the decision engine to one provider's JSON shape.

**Tests:** `NwsMappingTests` run against the captured payloads, with no live
HTTP:
- 156 hours, with the first and last start times checked;
- both Nov 1 01:00 hours, at −04:00 and −05:00, each 1 h long and contiguous;
- the zone id is `America/New_York`;
- 13 entries in each amount series;
- a null probability maps to null;
- a non-mm unit throws.

The HTTP path itself is untested until WireMock arrives in Block 5.

#### D8. The notification answers "cushions or shovel" and stays unambiguous on DST nights

*(Revised by [D12](#d12-review-one-rounding-rule-for-the-decision-and-the-text): one rounding rule for thresholds and text, and an ice+snow title.)*

**Decision:** `NotificationComposer` is a pure function from `NightAssessment`
to title, body, tags and priority.
- **Title leads with the kind** and shows the amount for that kind:
  `Overnight snow: 2.1" expected, up to 80%`. A snow night reports snow inches,
  not liquid. NWS's liquid figure *includes* melted snow, so leading with it
  would undersell a snowstorm by ~10×.
- **Repeated hours get a suffix.** On the fall-back night the two 1 AM hours
  print as `1 AM EDT` and `1 AM EST`; unambiguous hours get no suffix. This
  fixes the Block 1 "two identical 1 AM lines" debt.
- **The abbreviation table is keyed by the zone's standard offset, not the
  hour's offset.** −5 h is both EST and CDT, so "offset → name" alone would
  label Chicago summer time as Eastern. Zones outside the table fall back to
  `UTC+1`-style labels.
- **"trace" and "0.0" can't disagree.** Amounts under 0.05" print as `trace`
  in the body, and the same constant drops the amount clause from the title.
  The title can never say "0.0" while the body says "trace."
- **Title and tags are ASCII.** ntfy reads HTTP headers as Latin-1, so non-ASCII
  characters would arrive garbled. The body is UTF-8 and may use `·`.
- **Composing with no hits throws.** Callers must check `ShouldAlert`, so
  inventing a "nothing to report" message would hide that bug.

**Rejected:** formatting inside `Program.cs`, which is what Block 1 did. It
was untestable, and it's where the ambiguous 1 AM labels came from.

**Tests:** `NotificationComposerTests` covers:
- titles for rain, snow and ice;
- dropping the amount clause at 0, and the trace boundary (0.01, 0.049);
- leaving zero series out of the amounts line;
- the fall-back night body (EDT/EST suffixes, none on 3 AM) and its window line;
- a `UTC+1` fallback label for London;
- every title and tag character ≤ 0x7F;
- priority mapping;
- empty hits throws.

#### D9. Probability decides *whether* to alert; amounts decide *how loudly*

*(Revised by [D12](#d12-review-one-rounding-rule-for-the-decision-and-the-text): thresholds compare rounded values. Revised by [D13](#d13-review-no-data-for-part-of-the-night-is-a-failed-run-never-a-dry-one): incomplete hourly data now fails the run.)*

**Decision:** `ForecastEvaluator.Evaluate` is the decision engine: a pure
function of window, forecast, rule and clock, with no I/O and no `DateTime.Now`.
- **Effective range:** `[max(window start, now), window end)`. A run at 2 AM
  reports only what's still ahead, and the hits and the totals use the same
  range, so the message can't mention rain that already fell.
- **An hour counts if it overlaps the range at all.** The hour containing "now"
  still counts. Requiring `start >= now` would drop it.
- **Hit:** probability ≥ threshold (50, inclusive). A missing probability
  counts as 0.
- **The alert fires on hits only.** Amounts never gate it, because NWS amounts
  are coarse (6 h blocks) and often 0 while hourly probability is high.
  Amounts only pick the kind and priority.
- **Kind:** Ice if any ice; else Snow if ≥ 0.1"; else Rain. Ice wins because
  it's the hazard.
- **Priority high:** snow ≥ 1", any ice, or liquid ≥ 0.5".

**Rejected:**
- Alerting on amount thresholds. It looks more precise, but it misses nights
  where NWS has high hourly probability and hasn't filled in amounts.
- Treating a null probability as "skip". That makes a threshold of 0
  inconsistent.

**Known fragility:** inch thresholds are compared as doubles. The boundary test
rows land exactly on 0.1 / 0.5 / 1.0 today; changing the mm→inch path could
nudge one a hair under.

**Tests:** `ForecastEvaluatorTests`:
- Real fall-back payload: exactly 12 in-range hours, including both 1 AM hours.
- Synthetic spring-forward payload: exactly 10 in-range hours, with planted
  probabilities giving known hits. This payload is hand-built, so it's
  tracked as debt below.
- Runs at 01:59:59, 02:00, 02:30 and 06:30 (the hour containing now counts).
- Hours ending exactly at 20:00 or starting exactly at 07:00 are excluded; hours
  straddling either edge count.
- Probability 49/50/51; null probability; hits sorted even from shuffled input.
- Apportioned totals checked against hand arithmetic, shrinking as "now" moves
  into the window.
- Every kind and priority boundary (0.09/0.1 snow, 0.99/1.0 snow, 0.49/0.5
  liquid, 0.001 ice).
- A night with amounts but probability 10 stays silent.

#### D10. The window's time zone comes from the location, not from config

**Decision:** the zone is the one NWS reports for the coordinates
(`/points` → `properties.timeZone`, e.g. `America/New_York`). `PRECIP_TIMEZONE`
is gone.

**Rejected:** a configured zone. That would mean "8 PM–7 AM on *my* clock,"
which stops making sense the moment a location in another zone is added, and
it's one more setting that can disagree with reality.

**Why:** the question is whether precipitation falls at that place overnight,
so "overnight" is that place's night. In Block 4 the zone becomes a column on
the Locations table, cached alongside the gridpoint.

**What would break it:** an NWS response without `timeZone` throws (D7), and an
id the OS doesn't know exits with code 2 rather than guessing. In a container,
missing `tzdata` would make *every* id unknown (see D1).

**Also in this commit:** `--dry-run` prints the composed message without
publishing, so the full pipeline can be checked against live data without
buzzing a phone.

### Block 2: independent review

After D1–D10, a separate reviewer that wrote none of the code checked the
branch for correctness. It found no defects in the time-zone and DST core. It
did find seven real problems around that core, plus two weak tests. Each
entry below is the finding and what changed.

#### D11. Review: amounts are optional; a gridpoint failure never blocks an alert

**Finding:** D7's "fail loudly" contradicted D9's "amounts never gate the
alert." A gridpoint 500 or an unexpected unit aborted the whole run, so a 90%
rain night sent *nothing*.

**Decision:**
- The hourly forecast alone decides whether to alert.
- If the gridpoint fetch fails (HTTP error, timeout, bad JSON, empty body, no
  `forecastGridData` URL) or its data can't be trusted (a series absent, a unit
  other than mm, a malformed `validTime`), amounts become **unavailable**:
  `NwsForecast.Amounts` (`ProviderForecast.Amounts` since Block 5) is null and carries a reason. The run logs a warning
  and the message says "Amounts unavailable from NWS."
- A series that is *present but empty* is still genuine zero.
- Points and hourly failures still fail the run (exit 2).
- Real cancellation still propagates; only a timeout is treated as a gridpoint
  failure.

**Kind without amounts:** taken from the hits' forecast text, matching whole
words and phrases only (tightened in [D15](#d15-review-round-2-whole-word-keywords-catch-all-gridpoint-degradation)).
"freezing rain" or "freezing drizzle" means Ice; "snow", "sleet", "ice
pellets", "flurry/flurries" or "wintry mix" means Snow; anything else means
Rain. Priority is High only for Ice. This is the Block 1 keyword heuristic,
kept as a fallback so the title isn't always "rain."

**Rejected:** keeping D7's fail-loud behavior for the gridpoint. A flaky
*secondary* endpoint would then suppress a real alert.

**Tests:** `NwsClientTests` use a stub HTTP handler serving the captured
fixtures:
- gridpoint 500, bad JSON, null body and a missing grid URL each give no
  amounts with a reason, while all 156 hours still map;
- hourly 500 and points 500 still throw;
- a cancelled token propagates.

`NwsMappingTests` cover each unavailable cause, and empty-vs-absent series.
`ForecastEvaluatorTests` cover the keyword table and its priorities. The timeout
path is not yet tested; that comes in Block 5 with WireMock.

#### D12. Review: one rounding rule for the decision and the text

**Finding:**
- Thresholds compared raw inches while the message rounded to 0.1". 0.09" snow
  was kind Rain while the body printed `0.1" snow`.
- 0.96" snow printed `1.0"` at default priority.
- 0.001" of ice, printed as "trace," forced Ice and high priority, and hid 6"
  of snow from the title.

**Decision:**
- Every threshold and every displayed number goes through
  `PrecipRounding.Tenths` (0.1", midpoints away from zero), so the decision and
  the text cannot disagree.
- "trace" means raw > 0 but rounds to 0.0. A trace of ice is no longer Ice or
  high priority.
- When the kind is Ice and snow rounds to ≥ 1", the title names both:
  `Overnight ice (0.2") + 6.0" snow, up to 80%`.

**Rejected:** keeping separate display and decision precision, which is the
mismatch the review found.

**Tests:** boundary rows assert the assessment *and* the composed message
together: 2.3 mm snow, 24.5 mm snow, 12.5 mm liquid, 0.001" ice and 0.05" ice.
Both ice+snow title forms are covered.

#### D13. Review: no data for part of the night is a failed run, never a dry one

*(Revised by [D16](#d16-review-round-2-a-gap-blocks-a-dry-verdict-not-an-alert): a gap only fails the run when there are no hits.)*

**Finding:** missing, stale or partial hourly data produced no hits, so the
job logged "No notification sent" and exited 0. That's the silence-is-ambiguous
problem from the brief, created by the decision engine itself.

**Decision:** `Evaluate` checks that the hours cover the effective range
`[max(start, now), end)` with no gaps. Overlaps and duplicates are fine. On the
first gap it throws `IncompleteForecastException`, with the gap's instants, and
the job exits with code **4**. A dry night is now only ever reported from a
complete forecast. In Block 6, the heartbeat will see code 4 as a failed run.

**Rejected:** warning but continuing. It still turns "no data" into "no rain"
whenever the data is entirely missing.

**Tests:**
- mid-window gap, missing tail, empty hours, and data starting late, each with
  exact gap instants;
- unsorted, overlapping and duplicate hours pass;
- a 2 AM run needs only 2–7 AM covered, and a 2:30 run is covered by the
  2 AM hour;
- both DST payloads pass.

#### D14. Review: strict timestamps and a validated threshold

**Timestamps:** a timestamp with no UTC offset (`2026-10-29T12:00:00`) was
parsed in the *host's* zone. That passes on a New York dev box and is off by
4 hours in a UTC container. `StrictTimestamps.Parse` now requires `Z` or
`±hh:mm`/`±hhmm`. It applies to gridpoint `validTime` starts and, through a JSON
converter, to hourly `startTime`/`endTime`. NWS sends offsets today, so this
closes a latent bug rather than a live one. **Tests:** offset forms parse to the
right instants; offset-less strings throw in both the parser and JSON.

**Threshold:** `PRECIP_PROBABILITY_THRESHOLD=60%` silently became 50, and 150
was accepted and never alerted. `AppConfig.ParseThreshold` now uses 50 when
unset, accepts integers 0–100, and otherwise refuses to start with a message
naming the variable, the same as the window check (D5). **Tests:**
`AppConfigTests`.

**Weak tests fixed:** the ASCII-header test only ever fed ASCII. It now
includes a hit with `Pluie légère ❄`, and the title and tags stay ASCII. The
kind and priority boundary tests now assert on the composed text as well
(D12).

#### D15. Review round 2: whole-word keywords, catch-all gridpoint degradation

The reviewer re-checked the D11–D14 fixes. All seven original findings were
confirmed fixed, but three new issues turned up. Two are fixed here; the third
is D16.

**Keyword false positives:** plain substring matching made "Patchy Freezing
Fog," "Ice Fog" and "Ice Crystals" (real Alaska wording) into Ice at high
priority. Fog is not precipitation. It also matched "ice" inside "Nice."
Matching is now a whole-word regex on precipitation *phrases*: "freezing
rain"/"freezing drizzle" for Ice, and "ice pellets" alongside "sleet" for Snow.
They're the same thing, and the earlier rule had "Sleet" under Snow but "Ice
Pellets" under Ice. **Tests:** the fog, crystals and "Nice" rows now give Rain.

**Gridpoint exception gaps:** two failures still aborted the run despite D11:
- an invalid response charset (`InvalidOperationException`);
- a `null` `validTime` (`NullReferenceException` in the mapper).

Both the fetch and the mapping now catch *everything* except real cancellation
and degrade to "amounts unavailable." D11 promises no gridpoint failure can cost
an alert, so a list of anticipated exception types was the wrong shape.
**Tests:** `NwsClientTests` cover both inputs.

**Noted, not changed:** because thresholds compare 0.1"-rounded values (D12),
the effective cut-offs are ≥ 0.45" liquid, ≥ 0.95" snow and ≥ 0.05" ice. That's
intended: the number you read in the message is the number the decision used.

#### D16. Review round 2: a gap blocks a "dry" verdict, not an alert

**Finding:** D13 overreached. Any gap threw before hits were counted, so 90% rain
every hour from 8 PM to 3 AM, with NWS missing 3–7 AM, exited 4 and sent
*nothing*. That's the same failure class as the original D11 finding: an input
that doesn't decide the alert ended up preventing it.

**Decision:** gaps are collected, not thrown on sight.
- **Gaps and no hits:** `IncompleteForecastException`, exit 4, exactly as D13.
  We can't call the night dry when part of it is unknown.
- **Gaps and hits:** the alert goes out. The body adds a line naming the
  unknown stretch in local time, e.g.
  `Forecast incomplete: no data 3:00 AM-7:00 AM`, and the run logs a warning
  and exits 0. The hits we have are enough evidence to warn; the rest is
  flagged, not hidden.

**Rejected:**
- Alert *and* exit 4. Monitoring would then call a run that did its job a
  failure, and that blurs what exit 4 means.
- Keeping D13 as-is. It's the strictest about data quality, but it can
  suppress a real rain warning.

**The principle, now stated once:** *no input that doesn't decide the alert may
prevent it.* Neither amounts (D11) nor coverage (here) can stop a probability
hit from reaching the phone. And no missing data may ever produce a "dry"
verdict (D13).

**Tests:**
- the reviewer's case (exact gap, 7 hits);
- every gap collected (three holes);
- a gap with only 49% hours still throws;
- a complete forecast has no gaps;
- the message line appears in local time when there are gaps, and is absent
  without them.

### Block 3: containerize + deploy

#### D17. CI runs the tests under three host time zones

**Decision:** every PR to `main`, and every push to it, runs build and test in
GitHub Actions (`.github/workflows/ci.yml`). The `test` job runs three times,
with the runner's `TZ` set to `UTC`, `America/New_York` and
`Australia/Sydney`. A separate `image` job builds the container and runs
`--self-check` (D18). Nothing is pushed anywhere.

**Rejected:** a single test run. It passes in whatever zone the runner happens
to be in, but the rule since D1 is that the *host's* zone must never matter.
Production is a UTC container and the dev box is in New York, so a bug that
reads the host zone can pass on one and fail on the other.

**Why these three:**
- **UTC** is production.
- **New York** is the dev box. It's where a host-zone bug is easiest to miss,
  because there the host zone and the location's zone agree.
- **Sydney** is the far side: a positive offset with opposite-season DST.

**What the mutation check showed:** I made "today" come from the host's zone
(`now.LocalDateTime`) instead of the location's, then ran the suite in a Linux
container under each zone:

| Host `TZ` | Clean | Mutated |
|---|---|---|
| UTC | 230 pass | 1 fails |
| America/New_York | 230 pass | 1 fails |
| Australia/Sydney | 230 pass | **all pass** |

The test that fails is D2's Sydney row ("08:00 local is still yesterday in
UTC"). *(Reasoning corrected by [D23](#d23-review-corrections-to-d17-d19-and-d20).)*
With a host-zone "today", `Resolve` only goes wrong in one of two cases:
- the host's date is *behind* the location's after 7 AM local, which needs a
  host more than 7 hours west of the location;
- the host's date is *ahead* before 7 AM local, which needs a host more than
  17 hours east.

The suite's real locations are New York and Sydney, and neither is more than
7 hours east of a Sydney host or more than 17 hours west of it. So the Sydney
leg can't catch this bug *with these tests*. A Honolulu row would let it. The
legs aren't redundant: one host zone can be blind to a bug that another
catches. Production's leg, UTC, is one that catches it.

**Limitation:** the matrix multiplies the existing tests; it doesn't add
cases. Which bugs a leg can see depends on which zones the rows use.

**Enforcement:** the repo is now public, so a ruleset on `main` ("main: CI
must pass") requires all three `test` legs and `image` to pass before any
merge. It also blocks force-pushes to `main` and deleting it, because the
commit history is part of the story.

#### D18. Runtime image: chiseled `-extra`, proved by `--self-check`

**Decision:** a multi-stage `Dockerfile`. The .NET 10 SDK image publishes
`src/Worker`, and the runtime image is
`mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra`.
- **Chiseled** means distroless: no shell, no package manager, and it runs as
  the non-root user `app` (UID 1654).
- **`-extra`** adds tzdata and ICU.

A new flag, `--self-check`, looks up `America/New_York` and checks that its
offset is −4 h on 2026-07-01 and −5 h on 2026-01-15. It needs no network and
no config, and it exits 0, or **5** on failure. CI runs it against every image
it builds.

**Rejected:**
- **Plain chiseled.** It has no tzdata, so `FindSystemTimeZoneById` throws and
  every night exits 2. That's the trap D1 warned about.
- **The full `runtime:10.0` image.** It works, but it's bigger and ships a
  shell and package manager that a nightly job never uses.
- **A live `--dry-run` in CI.** That proves more, but it calls NWS, so an NWS
  outage would fail PRs for reasons unrelated to the change.

**Why check offsets, not just the lookup:** a lookup can succeed against a
zone with no rules, which would report a fixed offset all year. Checking a
summer *and* a winter offset proves the DST rules are actually there.

**Mutation check:** on plain `-noble-chiseled` the self-check prints
`FAIL America/New_York: The time zone ID 'America/New_York' was not found on
the local computer.` and exits 5. Back on `-extra`, it exits 0.

**Also:**
- `.dockerignore` keeps `.env` (secrets), `.git`, tests and tools out of the
  build context. The exported image filesystem contains no `.env`, and `/app`
  holds only the four published files.
- `.gitattributes` forces LF on `*.sh` and the `Dockerfile`. A Windows
  checkout with `core.autocrlf` was turning `tools/capture-nws.sh` into CRLF,
  which bash can't run.

**Tests:**
- `RuntimeSelfCheckTests` proves the check passes on the dev host.
- CI's `image` job proves it passes in the real runtime image.

#### D19. The nightly run is 23:00 UTC

**Decision:** cron `0 23 * * *`. Container Apps cron schedules are always UTC,
with no time-zone setting, so the local run time moves with DST: 7 PM EDT in
summer, 6 PM EST in winter. Both are before the 8 PM window opens. The alert
always arrives before the night starts, on a forecast at most an hour or two
older than it could be.

**Rejected:**
- **One cron per DST season**, e.g. 00:00 UTC in summer and 01:00 UTC in
  winter to land on exactly 8 PM. That means editing the schedule twice a
  year, or having the app skip whichever run is in the wrong season. Both are
  brittle.
- **Running hourly and letting the app decide it's evening.** That's correct,
  but every extra run is another chance at a duplicate alert, and duplicates
  aren't prevented until Block 4's idempotency key.

**What would break it** *(corrected by [D23](#d23-review-corrections-to-d17-d19-and-d20))*:
a location where 23:00 UTC isn't early evening.
- **UTC−3 through UTC+7:** 23:00 UTC falls inside the window (London gets it
  at 11 PM or midnight). The evaluator still does the right thing, because D9
  only looks at hours still ahead, but the alert arrives after the night has
  started.
- **UTC+8 and further east:** 23:00 UTC is already the next morning (9 AM in
  Guam, which NWS serves). `Resolve` picks the coming night, so the alert
  arrives about 11 hours early, on a forecast that old.

Per-location schedules belong with Block 4's Locations table.

**In production:** the first scheduled execution started at
2026-10-29 23:00:00Z (7 PM EDT) and succeeded in 38 s:
- night `2026-10-29`, window `00:00Z → 11:00Z`;
- 11 hours checked, no hits;
- no notification sent.

An earlier manual run with the threshold forced to 0 did reach the phone,
which proves the Azure → ntfy path.

**Tests:** `NightWindowTests.Utc_cron_run_precedes_that_evenings_window_in_New_York`
runs at 23:00Z on six days: an ordinary summer day, an ordinary winter day, the
day before and the day of fall-back, and the day before and the day of spring
forward. Each row asserts:
- the night date is that same local date;
- the exact window instants;
- the run happens before the window starts.

#### D20. One retry, and the duplicate it can cause

**Decision:** one replica, a 300 s timeout, and `replicaRetryLimit = 1`. Any
non-zero exit makes Azure retry once. That means 1–6 from the app *(6, "database not
ready", is new in Block 4; corrected by [D32](#d32-review-exit-codes-docs-and-two-overstated-claims))*, but also a
crash (an unhandled exception exits 134) or the timeout killing the replica.
If the retry also fails, the execution is marked Failed.

**Rejected:**
- **No retries:** one NWS hiccup loses the night's alert, and there's no
  in-process retry until Polly in Block 5.
- **More retries:** each one widens the duplicate window below for little gain.

**The duplicate it can cause:** if ntfy accepts the message but the response
is lost (a timeout or a reset connection), the app exits 3, Azure runs it
again, and the phone gets the alert twice. Every other failure (config, NWS,
incomplete forecast, a crash) happens before anything is sent, so retrying it
is safe. After a successful publish, the only code left is one `WriteLine`
and `return 0`. The 300 s timeout can't cut a publish short either: three
30 s HTTP timeouts cap the run's network time at about 90 s. *(With several
locations (Block 4) the budget is per location, so many slow locations could
reach the 300 s timeout. See [D32](#d32-review-exit-codes-docs-and-two-overstated-claims).)*
This is accepted for now and logged as debt. Block 4's database-enforced
`(locationId, forecastNightDate)` key turns the retry into a no-op.

**Why non-zero exits matter:** a Failed execution is state Azure keeps. Block
6's heartbeat will read it, and that's how "the job died" becomes
distinguishable from "no rain."

**Proof:** the retry is Azure's behavior, not code, so there's no unit test.
The job definition after deploy (`az containerapp job show`) shows the
settings.

#### D21. Secrets live in the job, not in the image, the repo or the logs

*(Revised by [D22](#d22-review-the-coordinates-were-in-the-logs-every-night): the logs part, and the public repo.)*

**Decision:**
- The ntfy topic and the coordinates are Container Apps **job secrets**,
  injected as env vars with `secretref:`. The coordinates count as secrets
  because four decimal places pin a house to about 11 m.
- The NWS User-Agent and the window are plain env vars.
- The repo and its GHCR image are **public**, so Azure pulls the image with
  no registry credentials. Going public was the plan from the start; it also
  removes a GitHub PAT that would have expired after a year and silently
  stopped the job.
- `deploy/azure/deploy.sh` reads every value from the environment, never
  echoes one, and never uses `set -x`. Its README loads the values from the
  gitignored `.env` in a subshell, rather than having you type
  `export NAME=value` into shell history.

**Rejected:**
- **Key Vault with a managed identity.** That's the right answer for many
  secrets or several apps. For three values it adds a vault, an identity and
  role assignments, and protects nothing more in this block.
- **Plain env vars.** Anyone with Reader on the job could read them in its
  definition.
- **A private image and a PAT.** It hides nothing (the code is public anyway)
  and adds an expiring credential.

**Proof:**
- `.dockerignore` keeps `.env` out of the image (D18).
- After deploy, `az containerapp job show` lists the secret names, not their
  values. Checked on the first deploy, 2026-10-29: three secrets, listed by
  name only. The topic and coordinates are `secretRef`s with no plain value,
  and the job has no registry credentials.
- The app never prints the coordinates (D22). Checked in the first Azure run's
  logs.

### Block 3: independent review

After D17–D21, a separate reviewer that wrote none of the code checked the
branch. It confirmed the D19 instants, that .NET on Linux honors `TZ`, that a
failing test fails the CI job under the MTP runner, the D20 duplicate argument,
and that re-running `deploy.sh` keeps existing secrets. It found two real
problems and several smaller ones.

#### D22. Review: the coordinates were in the logs every night

**Finding:** D21 made the coordinates a job secret, but the app printed them
on every run (`Fetching NWS forecast for <lat>,<lon>`). In Azure, stdout
goes to the Log Analytics workspace. So the exact location was copied there
nightly, readable by anyone who can query the workspace. A malformed coordinate
leaked too, because `double.Parse`'s error message includes the input, and
config errors are printed. The secret only protected the job definition.

**Decision:**
- The coordinates are never written to stdout or stderr. The fetch log line no
  longer includes them.
- `AppConfig.ParseCoordinate` names the variable in its error but never the
  value, unlike the threshold error (D14), which echoes the value because it
  isn't secret.
- It also stopped accepting thousands separators. `12,3456` (a European
  decimal comma) used to parse silently as `123456`.

Nothing else leaks: `HttpRequestException` messages don't include the
`/points/{lat},{lon}` URL, and the gridpoint failure reasons don't either.

**Rejected:** rounding the coordinates in the log. Any precision useful for
debugging is precise enough to locate a house, and the time zone that's
already logged covers what debugging needs.

**What would break it:** any new log line that prints `config` or the points
URL. Block 6's structured logging must keep coordinates out of its fields.

**Tests:** `AppConfigTests`:
- a valid coordinate parses exactly;
- `12.3456x` and `12,3456` are rejected with the variable named and `3456`
  absent from the message;
- missing and empty values are rejected, naming the variable.

#### D23. Review: corrections to D17, D19 and D20

**Corrections:**
- **D17** said a host-zone "today" bug needs a host 7 hours west of the
  location. That was half the story: a host more than 17 hours *east* also
  triggers it. So the Sydney leg's blindness comes from the suite having no
  far-west location (like Honolulu), not from Sydney itself.
- **D19** said 23:00 UTC is late everywhere at UTC−3 and further east. From
  UTC+8 east it's actually the next *morning*, so the alert comes about 11
  hours early.
- **D20** said "exit 1–5". Any non-zero exit retries, including a crash
  (exit 134) and the replica timeout. All of them happen before the send, so
  the duplicate argument stands.

The entries above are corrected in place, each marked.

#### D24. Review: `deploy.sh` fails closed

**Findings and fixes:**
- **"Doesn't exist" vs "broken."** `az ... show` failing for *any* reason
  (an expired login, throttling, an unregistered provider) sent the script
  down the create path and hid the real error. It now creates only when az
  says not-found, and otherwise stops with az's message. It also checks up
  front that the `Microsoft.App` and `Microsoft.OperationalInsights`
  providers are registered. This subscription's weren't, which is how the
  reviewer found the problem.
- **Tags.** It rejected only `:latest`, so `:main` passed, and a mutable tag
  breaks "every run maps to a commit." It now accepts only a 7–40 character
  hex tag or an `@sha256:` digest.
- **Stale credentials.** On an existing job with no PAT set, it now removes
  any old `ghcr.io` registry entry. GHCR rejects a pull with a bad PAT even
  for a public image, so a stale one would break the job when it expired.
- **Small fixes:**
  - the script is now executable (`100755`);
  - `.dockerignore` excludes `.env` at any depth (`**/.env`), not just at the
    root.

**Proof:** stub runs with a fake `az` on `PATH`:
- `:main`, `:latest` and an untagged registry-port image are rejected; a sha
  tag and a digest pass.
- An unregistered provider stops the script with the fix command.
- Not-found takes the create path; a "not registered" show error stops the
  script without creating anything.
- A public image with an old `ghcr.io` entry triggers `registry remove`.

A probe build with `.env` files at the root and in `src/Worker/` copied only
the `.env.example` files.

**Not changed:** actions are pinned to major version tags rather than commit
SHAs. The workflow uses no secrets and a read-only token, so a compromised
action has little to steal. Revisit in Block 7, when CI gets push credentials.

### Block 4: persistence + API

#### D25. One alert per location per night: a locked row, at-least-once

**Decision:** the `alert_history` row for `(location_id, night_date)` is both
the record and the lock. `PostgresAlertLedger.SendOnceAsync` does this in one
transaction:
1. **Claim:** `INSERT … ON CONFLICT (location_id, night_date) DO NOTHING`
   against the unique index from D26.
2. **Lock:** `SELECT … FOR UPDATE` on that row. If it's already `Sent`, stop
   and return `AlreadySent`.
3. **Publish to ntfy while still holding the lock.**
4. **Commit:** only a 2xx from ntfy marks the row `Sent`, in the same
   transaction.

A second sender for the same key, whether a retry, an overlapping manual run or
the hourly local loop, blocks at step 1 or 2 until the first one finishes. It
then sees `Sent` and publishes nothing.

**Why both statements:**
- **The insert** is the serialization point for a brand-new night. Postgres
  makes a second `INSERT` of the same key *wait* for the first transaction's
  uncommitted insert, rather than fail or pass.
- **`FOR UPDATE`** covers a night that already has a `Pending` row from an
  earlier failed attempt. `ON CONFLICT DO NOTHING` takes no lock on an existing
  row, so without it two senders could both read `Pending` and both publish.

**The one gap, chosen on purpose (at-least-once):** exactly-once is impossible
here, because the side effect lives in ntfy, outside the transaction. Suppose
ntfy accepted the push but the response was lost (a timeout or a reset). The
publish throws, the transaction rolls back, and the row stays `Pending`, so the
next attempt sends again and the phone buzzes twice. I chose a possible
duplicate over a possible missed alert, the same "when in doubt, warn" rule as
D3 and D16.

**Rejected:**
- **At-most-once:** commit a `Claimed` row *before* sending; a retry that finds
  it stays silent. That fully repays D20, but a crash or lost request after the
  claim means **no alert that night**, which is the failure the project exists
  to prevent.
- **At-most-once plus releasing definite failures** (DNS errors, refused
  connections, non-2xx). Only a timeout or reset would then stay silent. It's
  the most nuanced option and needs the most code and tests, yet it still drops
  the alert in the one case that is genuinely ambiguous.
- **Deduplicating in memory, or checking first and then sending.** Two
  processes don't share memory, and a check-then-send has a race window.

**Details that matter:**
- **A failed send is recorded, not lost.** After the rollback, a separate short
  transaction upserts the row as `Pending`, with `attempts + 1` and
  `last_error`. `last_error` has every URL scrubbed out, because the topic is
  the last path segment of the publish URL. That upsert only touches rows that
  are still `Pending`, so it can never turn a `Sent` row back. A `Pending` row
  never blocks a send.
- **A timeout counts as a failure, not a cancellation.** `HttpClient` reports
  its own timeout as a `TaskCanceledException`. Only cancellation of *our* token
  counts as "stop". A timed-out POST is exactly the lost-response case, so it is
  recorded and retried.
- **After the push, nothing can be cancelled.** Once ntfy has accepted, marking
  `Sent` and committing ignore cancellation. Aborting there would only turn a
  stop request into a duplicate.
- **`IsSentAsync` is an optimization, not enforcement.** The nightly pass calls
  it first so an already-sent night skips the NWS fetch. Enforcement is
  `SendOnceAsync`, which re-checks under the lock.
- **The key comes from D2.** A 23:00Z run and a 05:00Z retry (1 AM EDT) both
  resolve to the same `NightDate`, which is why the retry is a no-op.

**What would break it:**
- An `idle_in_transaction_session_timeout` (server or pooler) shorter than the
  ntfy timeout (30 s). That would kill the transaction while the lock is held
  across the HTTP call. The row would stay `Pending`, so the next run resends.
  That's still at-least-once, but now as a failed run. *(Corrected by [D32](#d32-review-exit-codes-docs-and-two-overstated-claims): an earlier draft blamed the pooler
  itself, but PgBouncer in transaction mode keeps one server connection for the
  whole transaction.)*
- **A second duplicate window:** if the commit itself fails *after* a 2xx, the
  row stays `Pending` and the next run resends. It's the same at-least-once
  case, and it's accepted with it.
- Renaming the unique index (D26).

**Tests:** `tests/Worker.Tests/Alerts/`, against real Postgres:
- `First_send_publishes_once_and_records_Sent`, and
  `Second_send_for_the_same_key_publishes_nothing`.
- `Retry_after_local_midnight_resolves_to_the_same_night_and_publishes_nothing`:
  the D2 tie-in, using the real `NightWindow.Resolve`.
- `Eight_simultaneous_senders_publish_exactly_once`: 8 contexts released by one
  barrier, a 200 ms publish, 5 rounds, run for a brand-new night *and* for a
  night with an earlier `Pending` row. Exactly 1 publish each round.
- `Lost_response_is_resent_this_is_the_accepted_duplicate`: 2 publishes, by
  design. The test exists so the gap is visible, not hidden.
- `Http_timeout_is_a_recorded_failure_not_a_cancellation`.
- `Crash_mid_send_leaves_no_Sent_row_and_the_next_run_publishes`: one variant
  disposes the connection, the other uses `pg_terminate_backend`, which is
  closer to a process dying.
- `Earlier_Pending_row_does_not_block_a_send`, and
  `Different_night_or_different_location_sends_independently`.
- `Cancellation_throws_and_leaves_the_row_unchanged`.
- `Failure_text_never_carries_a_url`.

**Mutation check:**
- **Removing `FOR UPDATE`** failed only the earlier-`Pending` race, with 8
  publishes instead of 1. The brand-new-night race still passed, because the
  waiting insert serializes it. A single race test would have missed this
  mutation, which is why there are two.
- **Removing the `ON CONFLICT` insert** failed 10 of 17 tests.
- **A realistic "check first, insert if missing" rewrite** failed the
  brand-new-night race. The losers got a 23505 error instead of `AlreadySent`.

#### D26. Rules and coordinates are data; their invariants live in the schema

**Decision:** locations, their alert rules and the alert history move into
Postgres (`src/Persistence`, EF Core + Npgsql, snake_case names). Every rule
that used to be an env-var check is now a **database constraint**, so any
writer is held to it, whether that's the API, a `psql` session or a future job:
- `alert_rules.probability_threshold` CHECK 0–100 (was D14's startup check).
- `alert_rules` CHECK `window_start > window_end`, so the window must cross
  midnight (was D5's).
- `locations.latitude numeric(6,4)` CHECK ±90, `longitude numeric(7,4)` CHECK
  ±180. Four decimal places is the NWS limit, so the column can't *store* a
  fifth. Postgres **rounds** one on write rather than rejecting it, so rejection
  is the code's job (below). *(Corrected by [D32](#d32-review-exit-codes-docs-and-two-overstated-claims).)*
- `alert_history` UNIQUE `(location_id, night_date)`, named
  `ux_alert_history_location_night`. This is the idempotency key from the
  brief; how sends use it is D25.
- `night_date` is a Postgres `date` (`DateOnly`), with no time and no zone. It's
  D2's local "night the window starts on," and storing it as a timestamp would
  invite exactly the UTC-date bug D2 guards against.

The same rules are checked first in code by `AlertRuleValidation`, so a caller
gets a message naming the field rather than a constraint violation. The
database is the backstop, not the only line.

**Coordinates:**
- A fifth decimal place is **rejected, not rounded**, by `AlertRuleValidation`
  (the API's path). Rounding silently moves the point by up to ~5 m, which makes
  it a different location. A raw `psql` insert would be rounded by the column
  instead, which is the one rule here that only the code enforces.
- Errors name the field but never the value (D22). The exception isn't even
  given the value as `actualValue`, because it would print it.
- Coordinates are immutable once stored: to move, create a new location. The
  gridpoint cached on the row (D27) then can't go stale through an edit.

**Deletes:** removing a location cascades to its rule but is **restricted** by
its alert history. What was sent is never erased as a side effect.

**Rejected:**
- Validating only in C#. Each new writer would need to remember the rules.
- `EnsureCreated` instead of migrations. The tests would then run against EF's
  idea of the schema instead of the one production gets (see the fixture below).
- Storing `night_date` as `timestamptz`.

**What would break it:** a raw-SQL writer that sidesteps EF is fine, since the
constraints still hold. What breaks it is a migration that drops or renames
the unique index. The ledger (D25) depends on its name.

**Tests:**
- `AlertRuleValidationTests`: D5, D14 and D22's cases, moved from
  `AppConfigTests`. Every coordinate rejection asserts the digits are absent
  from the message.
- `SchemaSmokeTests` (in both test projects) run against **real Postgres 17**
  in Testcontainers. A second `alert_history` row for the same location and
  night, inserted with raw SQL that bypasses all app code, fails with SQLSTATE
  23505 on `ux_alert_history_location_night`. A threshold of 101 fails with
  23514. The point is that the *database* refuses.
- The fixture (`tests/Shared/PostgresFixture.cs`) applies the real migrations
  once to a template database and gives each test class its own copy
  (`CREATE DATABASE … TEMPLATE`). So every DB test also proves the migrations
  build the schema.

#### D27. The gridpoint and time zone are cached on the location row

**Decision:** `NwsClient` now has two steps. `ResolvePointAsync` calls
`/points` and returns an `NwsPoint`: the zone, the hourly URL and the grid URL.
`GetForecastAsync(NwsPoint)` fetches the forecast. `DbGridpointCache` stores the
point on the `locations` row (`time_zone_id`, `forecast_hourly_url`,
`forecast_grid_data_url`, `gridpoint_resolved_at`) and reuses it on every
later run. That repays the Block 1 debt of re-fetching `/points` every night.

**Invalidation:** the cache never expires on a timer. A **404** from the cached
hourly URL throws `NwsPointStaleException`. The pass then refreshes the point
once and retries the fetch once. If it is still stale, the location fails with
exit 2, so it can't loop. A 500 is a plain NWS error, not "stale," because an
outage must not churn the cache.

**Rejected:**
- **A time-to-live (TTL) cache.** It re-fetches a mapping that almost never
  changes, and it still serves a stale URL for up to a whole TTL after NWS
  moves it.
- **Caching in memory.** The job runs once and exits, so nothing would survive
  to the next night.

**Why the row is the right place:** the zone is half of what makes the
idempotency key (D2/D25), and D10 said it belongs to the location. Coordinates
are immutable (D26), so the cached point can't drift from them.

**Tests:**
- `DbGridpointCacheTests`:
  - the first `GetAsync` calls `/points` once and persists the columns;
  - a second `GetAsync`, with a fresh context, makes 0 `/points` calls;
  - `RefreshAsync` overwrites a stale URL;
  - an hourly 404 throws stale; a 500 does not.
- `NightlyPassTests`: `Stale_point_is_refreshed_exactly_once_and_the_fetch_retried_once`,
  and `Still_stale_after_one_refresh_fails_the_location_with_2_and_does_not_loop`.
- `NwsCoordinatesTests` formats with at most 4 decimals in any culture.

#### D28. Migrations are explicit; a run refuses an out-of-date schema

**Decision:** schema changes are applied only by `Worker --migrate`, run on
purpose from the dev box before a deploy. Every normal run first checks that
the database is reachable and that `GetPendingMigrationsAsync()` is empty. If
either check fails, it exits **6** ("database not ready") before touching
anything else.

**Rejected:** migrating on startup (`Database.Migrate()` in every run). It's
convenient, but:
- the nightly job would need DDL rights it otherwise never uses;
- a bad migration would run unattended at 23:00 and cost that night's alert;
- two overlapping starts would race to migrate.

**Why exit 6 and not a crash:** "the schema is behind" is an operator mistake,
not a bug. A distinct code makes the Failed execution self-explanatory, and
Block 6's heartbeat will see it.

**What would break it:** deploying an image without running `--migrate` first.
That's caught on the first run (exit 6), not silently ignored.
`deploy/azure/README.md` puts the migrate step before `deploy.sh`.

**Tests:**
- `MigratorTests`: an empty DB gets every migration, a re-run is a no-op, and
  an unreachable DB exits 6 without printing the password.
- `NightlyPassTests`: `Pending_migrations_return_6_before_anything_else_is_touched`,
  `Database_with_no_schema_at_all_returns_6`, and
  `Unreachable_database_returns_6_without_echoing_the_connection_string`.

#### D29. Production Postgres is Neon's free tier, on the direct endpoint

**Decision:** the production database is a Neon project: Postgres 17, AWS
`us-east-1` (nearest to Azure `eastus`), free tier. The job reaches it through
one Container Apps secret, `db-conn`, mapped to `ConnectionStrings__Precip`.
The connection string uses Neon's **direct** host (not the `-pooler` one), with
`SSL Mode=VerifyFull` and `Channel Binding=Require`.

**Rejected:**
- **Azure Database for PostgreSQL Flexible Server (Burstable B1ms).** It's the
  "enterprise" answer and lives in the same subscription and region. But it
  costs roughly $13–16 a month for a database that is touched for a few seconds
  a night, unless the 12-month free grant applies. It would also add a server,
  firewall rules and an admin password to `deploy.sh`.
- **Postgres in a container next to the job.** A Container Apps Job has no
  durable disk, so the data would vanish with the replica.
- **SQLite on Azure Files.** It loses the row locking that D25 depends on, and
  file locking over SMB is not something to trust.

**Why it fits:** the workload is one short transaction a night. Neon's free
tier scales to zero when idle, and the first connect wakes it in about a
second, well inside Npgsql's 15 s connect timeout. Nothing else in the code
knows it's Neon: swapping providers is a new connection string and a
`--migrate`.

**Connection details that matter:**
- **Direct, not pooled.** D25 holds a row lock across the ntfy call, and
  `--migrate` uses session-level features. There's one job and a handful of
  connections a night, so a pooler would add nothing but a layer to reason
  about.
- **`VerifyFull`, not `Require`.** `Require` encrypts but accepts any
  certificate. `VerifyFull` also checks the CA chain and the hostname. The
  chiseled runtime image has the CA bundle (the NWS calls over HTTPS already
  depend on it).
- **Neon gives a URL; Npgsql wants `key=value`.** The deploy README spells out
  the conversion.
- **GSSAPI is disabled in code** (`PrecipDbContext.Configure`). Npgsql 9+ tries
  Kerberos session encryption first. The chiseled image has no
  `libgssapi_krb5`, so the first Azure run printed "cannot load library" to
  stderr before falling back to password auth over TLS. Harmless, but it was
  an error line on every run.

**What would break it:**
- Neon changing its free tier or suspending an inactive project. That shows up
  as exit 6 on the next run, and the job's Failed execution is the signal
  (Block 6's heartbeat).
- Pointing a *local* Worker at Neon. It would mark nights `Sent` from a dev box
  (D32). Locally, only `--migrate` and the admin API ever use `.env.neon`.

**Proof (2026-10-30, first deploy with Block 4):**
- `--migrate` from the dev box applied `InitialSchema` to Neon.
- The location was added through the local admin API (201).
- `deploy.sh` with image `786dd1b9d199`. Afterwards, `az containerapp job show`
  lists:
  - secrets `db-conn` and `ntfy-topic` only (the lat/lon secrets are gone);
  - args `--once`;
  - three env vars, two of them `secretRef`s.
- **Idempotency in production:** with the rule's threshold at 0, execution
  `job-precip-nightly-qkxvwuk` sent one notification (`Sent`, attempts 1). The
  next start, `job-precip-nightly-hp5qhjv`, logged
  `Location 1 (Home): already sent for night 2026-10-30.` and published
  nothing. The phone got exactly one alert. The threshold was then set back to
  50.

#### D30. The admin API is local-only, behind an API key

**Decision:** `src/Api` is a minimal API for managing locations and their
rules, and for reading alert history:
- `POST /locations`, `GET /locations[/{id}]`, `PUT /locations/{id}/rule`;
- `DELETE /locations/{id}`, which is a soft deactivate (history is kept);
- `GET /locations/{id}/alerts`.

In this block it runs only on the dev box, pointed at whichever database
(compose or Neon). It is **not deployed**.

**It still requires a key,** because it serves coordinates, which pin a home:
- Every route except `/health` sits behind an endpoint filter that checks
  `X-Api-Key`.
- The comparison is `CryptographicOperations.FixedTimeEquals` over SHA-256
  hashes. Hashing first makes the lengths equal, so the comparison's timing
  leaks nothing.
- A missing key or one under 32 characters stops startup.

**Rejected:**
- **Deploying it with external ingress.** That's a public endpoint holding home
  coordinates, which means key rotation, rate limits and an attack surface,
  all for an admin tool used a few times a year.
- **Internal ingress only.** It would be unreachable without a VNet or a
  jumpbox, so it would exist mostly for show.
- **No auth "because it's local."** It runs against the production database,
  and the key costs one filter.

Deploying it publicly is debt for Block 7.

**Details that matter:**
- **Auth runs before the body is read.** Handlers read the body themselves
  instead of using parameter binding. ASP.NET Core binds before endpoint
  filters run, so a malformed body from a caller with no key would otherwise
  get a 400 that reveals the shape of the API, instead of a 401.
- **Unexpected errors return a bare 500.** The development exception page would
  print the request headers, including the key.
- **Errors never echo coordinates.** A 400 names the field and never echoes a
  coordinate (D22). Database constraint errors (23505 and 23514) map to a 409
  or 400 with fixed text, never the Postgres message.
- **Coordinates can't be edited (D26).** No endpoint changes them.

**Tests:** `ApiTests` (WebApplicationFactory against real Postgres), 40 tests:
- `Missing_key_is_401_with_no_body` and
  `Writes_without_a_key_are_401_even_when_the_body_is_garbage`;
- `Five_decimal_latitude_is_400_and_the_response_never_echoes_it`, and
  `Create_then_get_round_trips_coordinates_exactly`;
- `Duplicate_name_is_409` and `No_endpoint_changes_coordinates`;
- `Alerts_default_to_50_and_cap_at_500`;
- startup refused for a missing, empty or 31-character key.

#### D31. The Worker runs on the generic host: `--once` in Azure, a loop locally

**Decision:** `Program.cs` builds a `Host.CreateApplicationBuilder` host, and
one `NightlyPass` does the work in both modes:
- **`--once`** (the deployed Container Apps Job): run one pass, return its exit
  code, stop.
- **No flag** (local): a `BackgroundService` runs a pass at start, then every
  `Worker:Interval` (default 1 h). It uses a `PeriodicTimer` on the injected
  `TimeProvider`, so tests drive it with a fake clock.
- **`--self-check`** is still the very first statement, before config or the
  host, because CI runs it in an image with nothing set (D18).

**Why hourly is now fine:** D19 rejected an hourly schedule because every extra
run was another chance at a duplicate alert. D25 removes that objection. The
first run that sees precipitation alerts; every later run that night is a
no-op. Production stays on the 23:00 UTC cron, because one run a night is all
it needs.

**Rejected:**
- **Keeping top-level statements and constructing a `DbContext` by hand.**
  That's a smaller diff, but every dependency would be wired by hand twice
  (app and tests). It also isn't how a `BackgroundService` is built, which is
  what this design note exists to show.
- **Deferring the long-running mode.** Both modes share the pass, so the loop
  costs one small class.

**Many locations:**
- Each active location runs inside its own try/catch, in id order.
- The run exits with the **first failing location's code** (2 NWS, 3 ntfy, 4
  incomplete), and every failure is logged.
- **No active locations is exit 1**, never a quiet 0. Silence must not mean
  "unconfigured" (problem 3 from the brief).
- Azure's one retry (D20) re-runs every location. That's safe: sent nights are
  no-ops, and dry ones are just re-evaluated.

**Logging (D22, extended):**
- Locations are identified by id and name only.
- The framework's `System.Net.Http.HttpClient` logger is switched off. At
  Information level it logs every request's full URL, and that URL contains
  the coordinates (`/points/{lat},{lon}`) and the ntfy topic. The integration
  agent found this; removing the filter makes two `WorkerServicesTests` fail.
- EF Core's per-command SQL log is raised to Warning. It never carried
  parameter values (sensitive-data logging stays off), but the SQL text was
  most of a run's output. The local end-to-end run caught that. Failed
  commands still log.

**Tests:**
- `NightlyPassTests` (25):
  - every exit code;
  - `Failing_first_location_does_not_stop_the_second_and_its_code_is_returned`;
  - `Already_sent_night_makes_no_forecast_call_and_counts_as_ok`;
  - `Dry_run_never_touches_the_ledger_and_prints_the_message`;
  - `D22_no_log_line_contains_the_coordinates_or_the_topic`, a full pass over
    six locations taking different paths, with a real `NtfyPublisher`.
- `PassHostedServiceTests`: `--once` stops with the pass's code, and the loop
  runs again after one fake interval and survives a crashing pass.
- `WorkerServicesTests`: the URLs never appear in the logs, and the headers are
  sent.

### Block 4: independent review

After D25–D31 and a local end-to-end run (one real notification, then
"already sent" on the second `--once`), a separate reviewer that wrote none
of the code checked the branch for correctness. It found **no high-severity
defects**.

What it confirmed, with evidence:
- **The D25 locking argument** under READ COMMITTED, plus the `FOR UPDATE`
  mutation result.
- **No API auth bypass.**
- **`deploy.sh` still fails closed**, and it removes the old lat/lon secrets
  only after nothing references them.
- **D2's night key is stable** across a minute-by-minute sweep of both DST
  nights (2026 fall-back, 2027 spring-forward) in 8 US zones and 5 windows:
  864,000 checks, 0 violations.

#### D32. Review: exit codes, docs, and two overstated claims

**Fixed:**
- **A database error during a send was reported as an ntfy failure.** If the
  ledger's own SQL failed (claim, lock, or the commit after a 2xx), the run
  exited 3 and logged "publish failed." It now exits **6** and says it was the
  ledger's database. If that commit failed after a 2xx, the push did go out,
  and the next run resends (D25's at-least-once case). Test:
  `Ledger_database_failure_during_send_returns_6_not_3`.
- **Logs weren't scrubbed the way `last_error` was.** Logged exception messages
  went out raw. Today's .NET messages carry no URL, but the logs are the more
  widely readable copy. One `Redact` helper now scrubs both. It also catches
  IP and `localhost` hosts (`10.0.0.5:80/topic`), which the first version
  missed.
- **The dev database listened on every interface.** `docker-compose.yml`
  published `5432:5432`, which binds `0.0.0.0` and `[::]`, with a committed
  password, and its comment said "localhost." It's now `127.0.0.1:5432:5432`.
  A local location row is still a real home.
- **`deploy.sh` could print "Deployed." after a failed check** (from Block 3).
  If `registry list` failed, the stale-credential cleanup was skipped silently
  and the script still exited 0. It now stops, like every other failed check
  (D24). Stub runs: a failing list exits 1, and a normal update still removes
  the old secrets.
- **`ApiSettings.ToString()`** would have printed the API key and the
  connection string (a record's generated `ToString`). It's redacted now;
  nothing logged it yet.
- **Test gap:** the D25 retry test now also runs across both DST nights. A
  fall-back retry at 1:30 AM EST and a spring-forward retry at 3:30 AM EDT
  each key on the night the window started.

**Docs corrected in place** (marked there):
- **D20 said "exit 1–5".** Exit 6 exists now.
- **D20's "about 90 s" network budget is per location.** Many slow locations
  could reach the 300 s replica timeout. Not fixed: there is one location
  today, and Block 5's Polly timeouts are the place to bound it.
- **D25 blamed Neon's pooler** for killing the held-lock transaction. PgBouncer
  in transaction mode keeps one server connection for the whole transaction.
  The real risk is an `idle_in_transaction_session_timeout` shorter than 30 s.
- **D26 said the `numeric(6,4)` column rejects a fifth decimal place.** Postgres
  *rounds* it on write (`40.71285` → `40.7129`). Rejection is the code's job
  (`AlertRuleValidation`), the one rule in D26 a raw `psql` insert can bypass.

**Noted, not changed:**
- **A local Worker pointed at the production database would mark nights
  `Sent`,** and production would then skip them. The deploy README runs only
  `--migrate` and the API against Neon, never a send.
- **Exit 6 has two causes.** It now covers "not ready" at startup *and* a
  database failure mid-send. Both mean "look at the database."

### Block 5: resilience + second provider

#### D33. Every forecast GET runs through one Polly pipeline: 10 s attempt, 3 tries, 30 s total, a breaker per provider

**Decision:** `AddGetResilience` (Polly v8 via `Microsoft.Extensions.Http.Resilience`) wraps the NWS and Open-Meteo clients. Strategies, outermost first:
1. **Total timeout, 30 s.** A hard cap per call, whatever happens inside.
2. **Retry: 3 attempts in total**, exponential backoff with jitter (1 s base). It retries 5xx, 408, 429, connection failures and an attempt timeout. It **never retries a 404 or any other 4xx**: D27's stale-point refresh has to see the 404 at once, and a retried 400 would just fail three times.
3. **Circuit breaker, one per provider.** It opens when at least half of at least 4 calls in a 30 s window fail, and stays open for 30 s. While open, calls fail instantly with `BrokenCircuitException` and nothing reaches the network.
4. **Attempt timeout, 10 s.** One slow response can't eat the whole budget.

`HttpClient.Timeout` equals the 30 s total *(corrected by [D38](#d38-review-response-bodies-had-no-timeout-a-hard-backstop-a-breaker-for-the-gridpoint): the first version set it to infinite, which left reading the response body unbounded)*. Polly's total timeout bounds the retries up to the response headers; the client timeout bounds the whole call, body included. All numbers live in `ResilienceSettings`, bound from config (`Resilience:Nws`, `Resilience:OpenMeteo`), so tests run the same pipeline with millisecond timings.

**Why the breaker, in a once-a-night job:** in `--once` mode its state dies with the process, so it does **not** remember last night's outage. What it does: within one pass, once NWS has failed enough to trip it, every later location skips NWS instantly and goes straight to Open-Meteo (D36), instead of spending 30 s each re-discovering that NWS is down. The hourly local loop (D31) keeps it across passes. The gridpoint fetch has a breaker of its own *([D38](#d38-review-response-bodies-had-no-timeout-a-hard-backstop-a-breaker-for-the-gridpoint))*, so a failing secondary endpoint can't trip the one that decides the alert. Persisting breaker state in Postgres was rejected as machinery for one run a night.

**Rejected:**
- **`AddStandardResilienceHandler` defaults.** Close to these numbers, but defaults aren't decisions, and it brings hedging and breaker settings nobody chose.
- **A tighter 5 s / 2 tries.** NWS is sometimes several seconds slow while healthy; that would turn slow into failed.

**Behavior worth knowing:** when retries run out on a status code (say, three 500s), the pipeline returns the last response rather than throwing. `NwsClient` turns it into an exception, as before. The breaker counts the attempts of every call: three failed attempts on one call plus one on the next trip it, so the second call's retry gets `BrokenCircuitException` immediately.

**Tests:** `GetResilienceTests`, against WireMock.Net (a real HTTP server in-process, so the real handler chain runs):
- `Two_503s_then_200_succeeds_after_exactly_3_requests`
- `A_404_is_returned_after_exactly_1_request`, `A_400_is_not_retried`
- `A_response_slower_than_the_attempt_timeout_is_retried`
- `The_total_timeout_cuts_retries_short_when_it_is_shorter_than_the_attempts`
- `Open_breaker_rejects_with_0_requests_and_a_successful_probe_closes_it`

Each test asserts how many requests actually reached the server. The suite passed three consecutive full runs before commit.

**Logging:** the `Polly` log category is set to Warning. Polly logs every successful attempt at Information, which would have doubled each run's log volume; retries, timeouts and breaker transitions are Warning and stay. The local end-to-end run caught this.

#### D34. ntfy is retried only when the request provably never arrived

**Decision:** `AddNtfyResilience` retries the publish POST **only** for failures where ntfy cannot have processed it:
- **Never sent:** an `HttpRequestException` whose `HttpRequestError` is `NameResolutionError`, `ConnectionError` or `SecureConnectionError`. In .NET 10's `SocketsHttpHandler` these are raised only while setting up the connection (DNS, TCP connect, TLS handshake), before any byte of the request is written. The XML doc cites the exact source lines.
- **Not processed:** HTTP 429 and 503. RFC 9110 defines both as "didn't handle this request."

**Everything else is sent exactly once:**
- an attempt timeout, or any cancellation: the request may have been delivered;
- a connection dropped after sending (`ResponseEnded`), or a garbled response;
- 500, 502, 504: the server may have processed it before failing.

**Why:** D25 already accepts one duplicate case, a lost response after ntfy delivered. A generic retry would add new ones: a slow-but-delivered POST timing out and being sent again. This predicate adds **no** duplicate window beyond D25's, while still riding out the failures that are truly safe to repeat (a DNS blip, a refused connection, rate limiting).

**The one assumption, stated:** a 503 means "not processed." A misbehaving proxy that returned 503 after forwarding would produce exactly D25's accepted duplicate, nothing worse.

**`Retry-After` is ignored.** On a 429/503 ntfy may ask for a delay. Honoring it would hold D25's row lock for as long as ntfy says, and the 30 s total timeout would cut a long wait short anyway. The backoff stays the agreed one.

**No breaker:** there is one POST per alert, so there's nothing to protect.

**Rejected:**
- **Retry all transient failures.** Simplest and most resilient, but a timed-out POST that was delivered gets sent twice, a duplicate the user never chose.
- **No ntfy retry**, leaving it to Azure's one job retry (D20). The narrowest window, but a DNS blip would then cost the whole job a re-run.

**Tests:** `NtfyResilienceTests`:
- `Two_503s_then_200_retries_and_succeeds_in_3_requests`
- `A_status_the_origin_may_have_processed_is_sent_exactly_once` (500, 502, 504)
- `A_response_slower_than_the_attempt_timeout_is_sent_exactly_once_and_throws`
- `A_connection_dropped_after_the_request_was_read_is_sent_exactly_once`. WireMock can't drop a connection mid-exchange, so a raw socket reads the request and closes.
- `A_refused_connection_is_retried_up_to_MaxAttempts_then_throws` and `An_unresolvable_host_is_retried_up_to_MaxAttempts_then_throws`
- `Publisher_retries_a_503_and_sends_the_same_body_and_headers_twice`

#### D35. Open-Meteo maps onto the same types, and its hours end at their timestamp

**Decision:** `OpenMeteoClient` asks `/v1/forecast` for hourly `precipitation_probability`, `precipitation`, `snowfall`, `weather_code` and `temperature_2m`, with `timeformat=unixtime`, `timezone=auto` and `temperature_unit=fahrenheit`. `OpenMeteoMapper` turns the response into the same `ProviderForecast` NWS produces, so the decision engine can't tell them apart.

**The trap: preceding-hour values.** Open-Meteo documents precipitation, its probability and snowfall as covering **the hour before** the timestamp. A value stamped 01:00 describes 00:00–01:00. So each entry becomes an hour starting at *T − 1h*. Reading *T* as the start would shift every hour, and the overnight window with it, one hour late. The test `Value_at_T_describes_the_hour_ending_at_T` pins this.

**Other mapping rules:**
- **Unix time** (`timeformat=unixtime`): every instant is unambiguous, so the DST rules of D3 and the offset rule of D14 hold with no local-time parsing at all.
- **Units fail loudly, as in D7:** precipitation must be `mm` and snowfall `cm`. Snowfall is converted cm → mm of depth (×10), the unit NWS's `snowfallAmount` uses.
- **Ice:** Open-Meteo has no ice-accumulation series. Hours whose weather code is freezing drizzle or freezing rain (WMO 56, 57, 66, 67) put their precipitation in the ice series.
- **Short text** comes from the WMO weather-code table, worded so D11's whole-word keywords still work ("Light freezing rain" reads as ice, "Light snow" as snow, "Fog" never as ice).
- **Temperature is display-only.** By D16's rule an input that doesn't decide the alert must never block it, so a missing series, a null or a non-°F unit just omits the temperature from the message. It never fails the forecast or prints a fake 0. The integration review caught that the first version printed "0".

**Rejected:** treating *T* as the hour's start (wrong by an hour); failing the forecast on a temperature problem (D16); mixing NWS hours with Open-Meteo amounts (D36).

**Tests:** `OpenMeteoMapperTests` / `OpenMeteoClientTests`, including a captured Open-Meteo payload for a fixed public point (`tools/capture-open-meteo.sh`):
- `Fall_back_night_has_twelve_in_range_hours_and_no_gaps`: on the 2026-10-31 fall-back night, the real capture gives exactly the 12 hours D3 says that night has.
- `Value_at_T_describes_the_hour_ending_at_T`
- `Wrong_units_throw_and_name_the_unit`, `Snowfall_centimetres_become_millimetres_of_depth`
- `Freezing_rain_or_drizzle_hour_lands_in_ice`, `Weather_code_text_and_resulting_kind`
- `Temperature_maps_only_when_fahrenheit`

#### D36. Open-Meteo is the fallback; a fallback night keeps the same key and says where it came from

**Decision:** NWS stays primary. `NightlyPass` falls back to Open-Meteo when the NWS path fails for **any reason except our own cancellation**: an HTTP error after retries, an open breaker, a timeout, bad JSON, a time-zone id the OS doesn't know, or a point that is still stale after D27's one refresh. Exit **2** now means *both* providers failed.

**What does *not* trigger fallback:** a gridpoint-only failure. The hourly forecast alone decides the alert (D11), so NWS hours with "amounts unavailable" are still a good NWS answer.

**Never mix providers within a night.** Hours and amounts both come from whichever provider answered. NWS probabilities with Open-Meteo amounts would be two models' opinions stitched together, and the message couldn't say which number came from where.

**The night key must not change.** The idempotency key is `(location, night_date)`, and `night_date` depends on the zone (D2). The zone is the location's cached `time_zone_id` (D27), whichever provider answered. Only when NWS never resolved one does it come from Open-Meteo's `timezone=auto`. Both are IANA ids, so an NWS run and a fallback run for the same instant produce the same key, and D25 still prevents a second alert.

**Provider recorded:**
- `alert_history.provider` (migration `AddAlertProvider`) says which provider's forecast was sent.
- A fallback alert's body ends with `Source: Open-Meteo (NWS unavailable)`. A different model can disagree with what the reader saw earlier, and they should know why. NWS alerts are unchanged.

**Rejected:**
- **Open-Meteo per location.** Unlocks non-US locations but doesn't help the one that exists.
- **Primary plus fallback per location.** The most capable, and the most to test and explain.

**Tests:** `NightlyPassFallbackTests`:
- `Nws_http_failure_falls_back_and_the_alert_names_open_meteo`
- `Open_circuit_goes_straight_to_the_fallback_without_refreshing_the_point`
- `Still_stale_after_the_one_refresh_falls_back_and_exits_0`
- `Both_providers_failing_exits_2_and_the_log_names_both`
- `Gridpoint_only_failure_is_not_a_fallback_trigger`
- `Cached_zone_wins_over_the_fallbacks_and_the_night_key_matches_an_nws_run`
- `Our_own_cancellation_is_not_a_provider_failure_and_propagates`

The D22 log guard now includes a fallback location whose errors quote a URL carrying the coordinates and topic. Nothing leaks.

**Drills:** `Nws:BaseUrl` and `OpenMeteo:BaseUrl` are configurable (absolute http(s) URLs, else startup fails with exit 1), so an outage can be forced without code changes. One catch, found in the local end-to-end run: `Nws:BaseUrl` only redirects the `/points` lookup. The hourly URL cached on the location row (D27) is absolute, so a real drill also clears `forecast_hourly_url`. Done that way, locally on 2026-10-30, NWS was refused, Open-Meteo answered with the same night key (cached zone, night `2026-10-30`), one notification arrived with the Source line, and `alert_history.provider` read `Open-Meteo`.

#### D37. A pass deadline that actually fits inside Azure's timeout: 100 s, exit 7

*(Revised by [D38](#d38-review-response-bodies-had-no-timeout-a-hard-backstop-a-breaker-for-the-gridpoint): the arithmetic below left out database time, so a hard 270 s backstop now makes the bound real. The 100 s soft deadline still decides whether new locations start.)*

**Decision:** before starting each location, `NightlyPass` checks how long the pass has run. Past **100 s**, it starts no more locations, logs each skipped one by id and name, and exits **7**, unless an earlier location already failed, whose code wins. Skipped locations are a failure, never a quiet 0: silence must not mean "not checked."

**Why 100 s and not more:** the deadline only stops *new* locations. One location's worst case is six calls, each capped at 30 s by D33/D34: points, hourly and grid together, a stale refresh, the retried fetch, the Open-Meteo fallback, and ntfy. That's 180 s. A location started just before the deadline can run that long, so deadline + 180 s must stay under Azure's 300 s replica timeout: 100 + 180 = 280 s. A typical location takes 1–2 s, so 100 s still covers dozens.

**Rejected:**
- **240 s**, the first draft's number. It looked like headroom, but 240 + 180 = 420 s; Azure would kill the replica mid-location.
- **240 s plus a hard cancel at 280 s.** It uses time better, but it cancels in-flight work, including possibly a send, and adds moving parts for a single-location job.
- **No deadline.** D32 noted the per-location budget could reach the replica timeout with several locations, and then Azure, not the app, decides which location is cut off.

**What would break it:** nothing silently any more ([D38](#d38-review-response-bodies-had-no-timeout-a-hard-backstop-a-breaker-for-the-gridpoint)). The soft deadline is now a best effort to finish cleanly, and the hard backstop is the guarantee. Startup refuses a hard deadline over 280 s or a soft one not below it.

**Tests:**
- `Deadline_passing_during_location_1_means_location_2_is_not_started_and_the_exit_is_7`
- `An_earlier_failure_keeps_its_code_when_the_deadline_then_passes`
- `Deadline_is_measured_from_the_start_and_comes_from_the_options`
- `Database_time_before_the_first_location_counts_against_the_deadline`
- The backstop and validation tests in D38.

A malformed `Worker:PassDeadline` or `Resilience:*` value fails startup with exit 1, like every other config error.

### Block 5: independent review

After D33–D37 and a local end-to-end run with NWS forced down, a separate reviewer that wrote none of the code checked the branch. It confirmed, with experiments:
- **ntfy retries can't duplicate (D34).** The classification holds against .NET 10's source, and the handler's own internal retry never applies to a POST with a body.
- **Open-Meteo's mapping matches its documentation (D35).**
- **A fallback night keeps the NWS night key (D36).**
- **Polly's warnings leak nothing (D22).**
- **The WireMock tests are deterministic enough:** 7 of 7 runs passed, including three suites running at once, twice.

It found one high-severity defect and two medium ones.

#### D38. Review: response bodies had no timeout; a hard backstop; a breaker for the gridpoint

**1. High: nothing bounded reading a response body.**
- **The bug:** Polly's timeouts end when the response *headers* arrive, and D33 had set `HttpClient.Timeout` to infinite. A server that sent headers and then stalled mid-body hung the run until Azure killed the replica, losing the exit code and the log D37 exists to produce. It was a regression: Block 4's default 100 s client timeout had bounded it.
- **The fix:** `HttpClient.Timeout` now equals the pipeline's 30 s total, and every body is read buffered (`GetAsync`, then `ReadFromJsonAsync`), so the read happens inside that clock. A client-timeout expiry is not our cancellation, so the pass falls back (D36) and the ledger records it as a failure (D25).
- **Tests:** `StalledBodyTests` covers `/points`, hourly, Open-Meteo and ntfy. A raw socket sends headers and half a body, then stalls; each call throws near the total timeout, and ntfy is sent exactly once. A stalled *gridpoint* degrades to "no amounts" (D11). With the timeout set back to infinite, the tests hang until their own 15 s limit fails them.

**2. Medium: one NWS breaker covered `/points`, hourly and gridpoint.**
- **The bug:** repeated gridpoint 500s, with hourly healthy, opened it. The next location's healthy hourly call was then rejected and fell back to Open-Meteo. That broke D36's "a gridpoint-only failure is not a fallback trigger." Nothing caught it, because that test used fakes.
- **The fix:** the gridpoint fetch goes through its own named client, `nws-grid`, with its own pipeline and breaker. It has the same headers and settings.
- **Tests:**
  - `Gridpoint_500s_open_only_the_grid_breaker_and_hourly_keeps_succeeding`, through the real pipelines.
  - `With_one_shared_breaker_the_same_gridpoint_failures_would_break_hourly`: the contrast, so the test proves the bug is real.

**3. Medium: D37's bound wasn't real.**
- **The bug:** its 100 + 180 = 280 s left out database time (about 10 statements per location, each allowed 30 s by Npgsql, plus a possible wait on D25's row lock). It also started the clock after the migrations check.
- **The fix:** a **hard backstop**. Every pass, in `--once` and in the loop, runs under a token cancelled at `Worker:HardDeadline` (default **270 s**, from the start of the pass, on the injected clock). If it fires, in-flight work is cancelled and the run exits **7** with an error log.
- **A send interrupted by the backstop** is D25's at-least-once case: the transaction rolls back, the row stays `Pending` or absent, and the next run resends. That is the only guarantee that holds by construction, because a sum can't bound a database waiting on a lock. The soft deadline's clock now starts at the very top of the pass.
- **Startup checks:** both deadlines must be positive, soft below hard, and hard at most 280 s; otherwise exit 1.
- **Rejected:**
  - **Tightening database timeouts and redoing the sum** (about a 40 s deadline). It's still arithmetic that a future change can break silently.
  - **Documenting the assumption.** D37's title would then be false.
- **Tests:**
  - `Once_a_pass_still_running_at_the_hard_deadline_is_cancelled_and_the_host_stops_with_7`: at 270 s, not at 269.
  - `Loop_the_backstop_cancels_one_pass_and_the_next_interval_still_runs`
  - `Once_stopping_the_host_mid_pass_is_not_reported_as_a_deadline`
  - `A_hard_deadline_over_280_seconds_fails_at_startup_without_echoing_it`, `A_soft_deadline_not_before_the_hard_one_fails_at_startup`
  - `Database_time_before_the_first_location_counts_against_the_deadline`

**Low, also fixed:**
- **Invalid settings now fail at startup.** Values that parse but are invalid (`MaxAttempts=0`, a failure ratio over 1, a zero timeout, an attempt timeout longer than the total) used to fail only when the pass started; now startup refuses them, naming the key (`A_resilience_value_that_parses_but_is_invalid_fails_at_startup_naming_the_key`). Polly's own lower limits (breaks of at least 0.5 s, timeouts of at least 10 ms) are not checked at startup.
- **A real °F capture.** The Open-Meteo fixture predated the temperature request, so a new capture proves the live `"°F"` unit string (`Captured_fahrenheit_payload_labels_every_hour_F`).
- **Two small corrections.** A comment cited D33 for the fallback; it is D36. A test name claimed the *total* timeout fired when three attempt timeouts did.

**Noted, not changed:**
- **A location NWS has never resolved takes Open-Meteo's zone, and that zone isn't saved.** Once NWS resolves it, its zone could differ, and so could the night key. That can't happen on the 23:00 UTC schedule, and the different keys belong to windows that don't overlap, so no night gets two alerts.
- **Two timeouts now race.** With the client timeout and Polly's total both at 30 s, an exhausted call fails with either `TimeoutRejectedException` or a client timeout. Every caller treats both the same.

**In production (2026-11-01):**
- `--migrate` applied `AddAlertProvider` to Neon before the deploy. The Block 4 image kept working against the new column.
- `deploy.sh` deployed image `fdc63261b157` (`--once`, cron 23:00 UTC, 300 s timeout, one retry; secrets `db-conn` and `ntfy-topic`).
- A manual start at the real threshold (`job-precip-nightly-s0crjtz`) took the NWS path, evaluated the fall-back night's 12-hour window, found no hits and sent nothing. No GSSAPI or Polly noise appeared in its logs.
- The fallback itself was proven locally (D36). Forcing an NWS outage in production would mean editing the job's config for a drill, which isn't worth the risk for a one-location job.

### Block 6: observability

#### D39. The heartbeat is an outside watcher: healthchecks.io, pinged at start and with the exit code

**Problem:** no notification means either "dry night" or "the job died three weeks ago." Nothing inside the job can tell those apart, because a job that never starts can't report anything.

**Decision:** a dead-man's switch on healthchecks.io (free tier).
- Every `--once` run sends `POST {ping url}/start` before the pass, and `POST {ping url}/{exit code}` after it. The body is one safe line, such as `exit 2: both forecast providers failed`.
- The check is scheduled `0 23 * * *` UTC with a 1 hour grace period. healthchecks.io alerts when:
  - a run reports a non-zero code (at once);
  - a run starts and never finishes (after the grace period: the replica was killed);
  - no run arrives at all (after the grace period: the cron was deleted, the image doesn't pull, the subscription is disabled, the job is broken before the host starts).
- Alerts go to **email and a second ntfy topic**. If ntfy itself is down, the alerts topic can't report that, so email is the channel that doesn't share the failure.
- The ping URL (`PRECIP_HEARTBEAT_URL`) is a secret: anyone holding it can report "healthy". It's a job secret like the topic, `https` only, never logged, and `AppConfig.ToString` redacts it. `deploy.sh` refuses to deploy without it.
- Only `--once` without `--dry-run` pings. The local loop and dry runs never do, so a dev run with production's `.env` can't fake a healthy night.

**Rejected:**
- **An Azure Monitor alert rule** on job executions. No new vendor and no code, but the watcher lives in the same Azure subscription and resource group as the thing it watches, so it misses exactly the "Azure itself broke" cases. It also costs money and is configured with fiddly CLI.
- **A nightly "all clear" push.** No infrastructure, but it makes me the watcher: a missing message has to be *noticed*, and a nightly ping trains me to ignore the app.

**What would break it:** healthchecks.io being down. That fails loud (a false "down" alert), not silent. A run that dies before the host starts (a config error, exit 1) sends no ping, so it's caught by the grace period, not instantly.

**Tests:**
- `HealthchecksHeartbeatTests`:
  - `Start_posts_to_slash_start`, `Finish_zero_posts_to_slash_0_with_an_ok_body`, `Finish_two_posts_to_slash_2_and_the_body_names_the_failure`
  - `A_failure_never_logs_the_secret_uuid_or_the_url`
  - `No_url_gives_the_no_op`, `Dry_run_gives_the_no_op_even_with_a_url`, `The_real_heartbeat_wins_over_the_pass_defaults`
- `OncePassHeartbeatTests`: `Start_comes_before_the_pass_and_finish_after_it_with_the_passs_code`, `A_throwing_pass_finishes_with_1`, `The_hard_deadline_finishes_with_7`.
- `AppConfigTests`: `Heartbeat_url_that_is_not_absolute_https_is_rejected_without_echoing_it`, `ToString_redacts_the_heartbeat_url`.

#### D40. A heartbeat or telemetry failure never changes the outcome, and the 300 s budget holds by construction

**Decision:**
- **The heartbeat can't hurt the run.** Each ping gets 2 attempts of 2 s, at most 4 s, retrying network errors, timeouts and 5xx but not 4xx. `IHeartbeat` promises never to throw, and `OncePassService` enforces that anyway: a ping that throws is logged and dropped. A broken start ping still runs the pass; a broken finish ping still leaves the pass's exit code and stops the host normally.
- **The flush can't hold the exit.** Disposing the host is when a `--once` run flushes its telemetry. Each OTLP export has a 5 s timeout, but there are three providers (traces, metrics, logs), and the metrics provider exports again on shutdown. Against a collector that never answers, disposal was **measured at 10 s**. So `Program` no longer uses `host.RunAsync()`, which disposes with no limit. `BoundedShutdown` disposes on a pool thread and abandons it after **5 s**. Unsent telemetry is lost; the exit code never is.
- **The budget:** start ping ≤4 s + hard deadline 270 s (≤280 s, D38) + finish ping ≤4 s + flush ≤5 s = 283 s by default, at most 293 s, inside Azure's 300 s replica timeout. Every term is enforced in code, not by arithmetic about how long things usually take. That's D38's lesson applied again.

**Azure's retry shows up honestly.** A failed first attempt pings `/<code>`, which alerts at once. If D20's one retry then succeeds, it pings `/0` and healthchecks.io sends "back up". Two messages, both true.

**Rejected:**
- **Trusting the 5 s exporter timeout.** That was the first plan's arithmetic, and the measurement disproved it.
- **Making the heartbeat part of the pass** (a failed ping fails the run). Then a healthchecks.io outage would turn into failed runs and Azure retries, and could even cost an alert.

**Tests:**
- `BoundedShutdownTests.A_collector_that_never_answers_cannot_hold_the_exit_past_the_budget`: the measured case, with real providers and a WireMock collector that never responds.
- `BoundedShutdownTests.A_disposal_that_blocks_is_abandoned_at_the_budget_and_says_so`, `A_quick_disposal_completes_and_says_nothing`
- `HealthchecksHeartbeatTests`: `A_hanging_server_is_given_up_on_within_the_budget`, `Always_500_returns_after_two_attempts_with_one_warning`, `A_404_is_not_retried`, `A_cancelled_caller_token_returns_without_throwing`
- `OncePassHeartbeatTests`: `A_faulting_start_still_runs_the_pass`, `A_faulting_finish_does_not_change_the_exit_code_or_stop_the_host_abnormally`, `A_slow_heartbeat_does_not_change_the_exit_code`

**Locally (2026-11-01):** with `OTEL_EXPORTER_OTLP_ENDPOINT` pointed at a closed port, `--once --dry-run` printed `Shutdown: telemetry flush did not finish within 5 s; exiting without it.` and exited 0, 10 s wall clock including startup and the pass.

#### D41. Telemetry speaks OTLP only; the backend is configuration

**Decision:** the code emits through `System.Diagnostics` (`ActivitySource`, `Meter`, `ILogger`) and exports through the OpenTelemetry SDK's **OTLP** exporters, configured by the standard `OTEL_EXPORTER_OTLP_*` variables.
- **Locally:** the .NET Aspire dashboard in `docker compose` (UI on `localhost:18888`, OTLP on `localhost:4317`), bound to 127.0.0.1 only.
- **In production:** Grafana Cloud's free tier: Tempo for traces, Mimir for metrics, Loki for logs. The endpoint is plain config; the `Authorization` header is a job secret.
- **Export is on only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.** Tests, CI and a bare `dotnet run` export nothing and print no connection errors. The SDK still runs (spans exist, the redactor runs, tests can attach an in-memory exporter); only the exporter is missing.
- The OTLP settings are read from the host's configuration in one place, not left to each exporter's own environment reading, so the same configuration decides in production and in tests. A malformed endpoint, protocol or timeout fails startup with exit 1, naming the key and never the value.
- `service.version` is the deployed image's git sha (`OTEL_RESOURCE_ATTRIBUTES`, set by `deploy.sh`), so a trace maps to a commit, as D18's image tags do.

**Rejected:**
- **The Azure Monitor exporter.** It keeps everything in one resource group, but ties the code to a vendor's exporter, and swapping backends would mean a code change.
- **Honeycomb.** The best trace explorer, but built around events; metrics fit less naturally.
- **Local only.** The cheapest option, but it leaves no production trace to show anyone.

**What would break it:** a Grafana Cloud outage or a revoked token loses telemetry for those nights. The heartbeat (D39) is independent of it, so the nights still aren't silent.

**The Api isn't instrumented.** It runs only on the dev box (D30); the Worker is the thing that runs unattended.

**Tests:** `WorkerTelemetryTests`:
- `Endpoint_set_flushes_spans_to_v1_traces_when_the_provider_is_disposed`
- `No_endpoint_means_no_export_even_if_the_sdk_could_find_one`
- `Malformed_otlp_setting_fails_startup_naming_the_key`

**Locally (2026-11-01):** `--once --dry-run` against the Aspire dashboard produced one trace: `precip.pass` → Npgsql spans for the location query → `precip.location` (outcome `dry`, provider `NWS`) → `precip.fetch` → two HTTP spans, `https://api.weather.gov/gridpoints/{redacted}/{redacted}/forecast/hourly` and `.../gridpoints/{redacted}/{redacted}` → `precip.evaluate`. No coordinate appeared in the exported spans or logs.

#### D42. Telemetry is redacted by allowlist before any exporter sees it, and no trace context leaves the process

**Problem:** standard HTTP instrumentation records each request's full URL. Ours carry secrets (D22): `/points/{lat},{lon}`, `/gridpoints/{office}/{x},{y}` (the grid cell pins a location to about 2.5 km), Open-Meteo's `?latitude=…&longitude=…`, ntfy's `/{topic}`, and the heartbeat's `/{uuid}`. Npgsql's spans and metrics carry the connection string (password removed, but user, host and database still in it).

**Decision:** keep the standard instrumentation (it's what shows Polly's retries as separate HTTP spans) and put a `TelemetryRedactor` processor in front of every exporter.
- **An allowlist per source, failing closed.** For each span source (`System.Net.Http`, `Npgsql`, `Precip.Worker`) only the listed attributes survive. Any other source loses every attribute. A library upgrade that adds a new attribute drops it until someone allowlists it on purpose.
- **URLs keep their shape, not their values.** A path segment survives only if it's a known literal (`points`, `gridpoints`, `forecast`, `hourly`, `v1`, `start`) or a 1–3 digit exit code; everything else becomes `{redacted}`, and the query string is dropped. The same rewrite applies to URLs inside status descriptions and exception messages.
- **Exception events keep type and message, never the stack trace**, as `Redact.Describe` does for logs. .NET has no public way to edit an event after it's added, so the redactor rewrites them as they're added, by hooking `AddException`. In `OnEnd`, a span whose events still carry anything it can't vouch for (a stack trace, an unredacted message, a tag off the list) is **dropped entirely**. Dropping a span is the fail-closed choice. One residue: the `exception.stacktrace` key is still exported, with an empty value.
- **Order is the guarantee.** OpenTelemetry runs processors in registration order, and an exporter is itself a processor. The redactor is registered first, so every exporter, including one a test adds later, only sees redacted spans.
- **Metrics get tag allowlists too**, because they bypass span processors. Npgsql's `db.client.connection.pool.name` (the connection string) is dropped, and our own meter may never use the location id or name as a tag.
- **Logs rely on the existing D22 log hygiene.** HttpClient's URL-logging category is off, our errors go through `Redact.Describe`, and nothing logs an exception object. The OTLP log exporter sends what the console log already sends to Log Analytics.
- **The time zone is logged at Debug only.** The local smoke run showed Block 2's `Time zone: <zone>` line, at Information, being exported to the new backend. The zone isn't a D22 secret, but it hints at where the location is, and CLAUDE.md keeps it out of the repo for that reason. Production logs at Information, so the line no longer leaves the box. Nothing is lost: the zone is cached on the location row (D27). The one exception, kept on purpose: the rare warning when Open-Meteo's zone disagrees with the cached one (D36) names both, because that warning is useless without them.
- **No `traceparent` header goes to NWS, Open-Meteo, ntfy or healthchecks.io.** The process installs .NET's no-output propagator. Third parties have no use for our trace ids.

**Rejected:**
- **Hand-made spans only, with no HTTP instrumentation.** Nothing could leak unless we wrote it, but Polly's per-attempt retries would vanish from traces unless rebuilt by hand.
- **A deny-list** ("remove `url.full` and `url.path`"). It is correct only until a library adds an attribute nobody listed.
- **Scrubbing without a test.** No defence against regressions.

**Tests:**
- `TelemetryRedactorTests`:
  - `RedactUrl_keeps_the_shape_and_drops_every_secret` (18 real-shaped URLs), `RedactUrlsIn_rewrites_every_url_in_a_longer_message`
  - `Http_span_keeps_only_the_allowlist_and_a_redacted_url`, `Npgsql_span_drops_the_data_source_and_connection_details`, `Precip_span_keeps_precip_keys_only`, `Unknown_source_loses_every_tag`
  - `Exception_event_keeps_type_and_redacted_message_and_no_stack_trace`
  - `Exception_event_added_by_hand_with_a_stack_trace_drops_the_span`, `Event_with_a_tag_off_the_allowlist_drops_the_span`, `Link_with_tags_drops_the_span`
- `WorkerTelemetryTests.Exporter_added_later_only_sees_the_redacted_http_span`: a real HttpClient request to `/points/40.7128,-74.006`, captured by an exporter registered *after* the redactor.
- `NightlyPassTests.The_time_zone_is_logged_only_at_debug`
- A whole-pass leak test over all three signals follows in Wave 2.

#### D43. What's measured, and why metric tags never name a location

**Spans** (source `Precip.Worker`), one trace per pass:
- `precip.pass`: location count and exit code. Error status on any non-zero code, and on cancellation.
- `precip.location`, one per location: id, name, night date, provider, outcome (`dry`, `alerted`, `already_sent`, `failed`, `skipped_deadline`) and exit code. Deadline-skipped locations still get a span, so "not checked" is visible (D37).
- `precip.fetch`, once per provider tried; HTTP spans nest under it, so a retry or a fallback is visible as structure. The NWS one spans the point lookup and the forecast. On an already-sent night it holds only the point lookup and the ledger check, which is what that night actually did.
- `precip.evaluate` (hits, peak, verdict) and `precip.notify` (Sent or AlreadySent).

**Metrics** (meter `Precip.Worker`): `precip.passes` by exit code; `precip.pass.duration`; `precip.locations` by outcome and provider; `precip.provider.fallbacks`; `precip.alerts.sent` by provider. Plus .NET's HttpClient metrics, Polly's and Npgsql's, under the D42 tag allowlists.

**Why no location in metric tags:** each tag value multiplies the number of time series, and a location id or name on every series would also ship location identity to the metrics backend for no analytical gain. Spans may carry id and name, because the logs already do (D22).

**An honest limit:** a once-a-night job makes sparse metrics. Each run is a new process, so every counter starts at zero and is exported once on exit. Metrics answer "how many fallbacks this month"; the trace and the heartbeat answer "what happened last night."

**Tests:** `NightlyPassTelemetryTests.Pass_emits_the_expected_spans_and_metrics`, one row each for a dry night, an alert, already sent, NWS failing and the fallback answering, both failing, a deadline skip, the database down, and a dry run. Each row asserts the span tree, outcomes, exit codes, statuses and the metric measurements. It also asserts that no metric tag key mentions "location", and that no span carries a coordinate, a URL or the database password. `Cancellation_marks_the_pass_span_cancelled_and_rethrows`.
