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
dotnet run --project src/Worker              # evaluate tonight, notify if needed
dotnet run --project src/Worker -- --dry-run # print the message, don't send it
dotnet test

docker build -t precip .
docker run --rm precip --self-check                # image has real time-zone data? (exit 0/5)
docker run --rm --env-file .env precip --dry-run   # full pipeline in a UTC container
```

Deployed as an Azure Container Apps Job on a nightly cron: see
[deploy/azure/README.md](deploy/azure/README.md).

Fetches the NWS hourly forecast and raw gridpoint amounts for the configured
coordinates, works out the next unfinished overnight window (8 PM → 7 AM) in the
location's own time zone, and publishes one ntfy message if any hour in it is at
or above the probability threshold. The message says rain, snow or ice, and how
much.

## Debt ledger

Block 1 was a walking skeleton with deliberate shortcuts; each is repaid in a
later block. Repaid rows stay, struck through, so the history is visible.

| Shortcut | Repaid in |
|---|---|
| ~~Overnight window is always *today 20:00 → tomorrow 07:00*, regardless of when the job runs.~~ | ✅ Block 2 ([D2](#d2-which-night-a-run-evaluates)) |
| ~~On the fall-back night the notification prints two identical "1 AM" lines.~~ *(Originally logged as "double-counts the repeated 01:00–02:00 hour". Captured payloads showed that was wrong: both 1 AM hours are real, distinct hours, and that night's window is genuinely 12 hours long. See [D3](#d3-window-edges-are-utc-instants-dst-handled-at-the-edges-only).)* | ✅ Block 2 ([D8](#d8-the-notification-answers-cushions-or-shovel-and-stays-unambiguous-on-dst-nights)) |
| ~~`PrecipDecision` is throwaway naive code, not a tested pure function. No captured-payload tests.~~ | ✅ Block 2 ([D9](#d9-probability-decides-whether-to-alert-amounts-decide-how-loudly)) |
| NWS gridpoint URL is re-fetched every run instead of cached. *(Moved from Block 2: the cache belongs on the Location row, and a container has no persistent disk to cache on.)* | Block 4 |
| Spring-forward test payload is hand-built (`tests/Fixtures/nws/synthetic-dst-springforward`). *(New in Block 2.)* | Real capture ~2027-03-08 |
| Coordinates and topic come from `.env` with no schema; only the window and threshold are validated. | Block 4 |
| No retry, backoff, circuit breaker, or timeout tuning on the HTTP calls. | Block 5 |
| ~~Single weather provider, no abstraction seam.~~ Seam exists ([D7](#d7-nws-responses-map-onto-provider-neutral-types-surprises-fail-loudly)); second provider not yet plugged in. | Block 5 |
| No persistence and no idempotency — every run that clears the threshold sends a notification. | Block 4 |
| No tracing or metrics. | Block 6 |
| Silence is ambiguous: "no precip" and "the job is dead" look identical. No heartbeat. | Block 6 |
| Console app only — no container, no schedule, runs from a dev machine. | Blocks 3, 7 |

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
  `NwsForecast.Amounts` is null and carries a reason. The run logs a warning
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
UTC"). The Sydney leg *can't* catch this bug. It only shows once the host's
date falls behind the location's date after 7 AM local, and that needs a host
at least 7 hours west of the location. No inhabited place is 7 hours east of
Sydney. So the legs aren't redundant: one host zone can be blind to a bug that
another catches. Production's leg, UTC, is one that catches it.

**Limitation:** the matrix multiplies the existing tests; it doesn't add
cases. A host-zone bug in code with no far-east test rows would still get
through.

**Not enforced yet:** the repo is private on GitHub Free, so the check shows
pass/fail on each PR but can't be made required. A public repo can require
`test` and `image` before merge.

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

**What would break it:** a location where 23:00 UTC is already 8 PM or later,
which is anywhere at UTC−3 or further east (London gets it at 11 PM or
midnight). The evaluator still does the right thing there, because D9 only
looks at hours still ahead, but the alert arrives after the night has started.
Per-location schedules belong with Block 4's Locations table.

**Tests:** `NightWindowTests.Utc_cron_run_precedes_that_evenings_window_in_New_York`
runs at 23:00Z on six days: an ordinary summer day, an ordinary winter day, the
day before and the day of fall-back, and the day before and the day of spring
forward. Each row asserts:
- the night date is that same local date;
- the exact window instants;
- the run happens before the window starts.

