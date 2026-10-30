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
| ~~Console app only — no container, no schedule, runs from a dev machine.~~ | ✅ Block 3 ([D18](#d18-runtime-image-chiseled--extra-proved-by---self-check), [D19](#d19-the-nightly-run-is-2300-utc)) |
| Image build, push and deploy are manual (`docker push`, `deploy/azure/deploy.sh`). CI only tests and builds. *(New in Block 3.)* | Block 7 |
| A lost ntfy response plus the job's one retry can send the same alert twice ([D20](#d20-one-retry-and-the-duplicate-it-can-cause)). *(New in Block 3.)* | Block 4 |
| Actions are pinned to major tags, not commit SHAs. Low risk while CI holds no secrets ([D24](#d24-review-deploysh-fails-closed)). *(New in Block 3.)* | Block 7 |

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
non-zero exit makes Azure retry once. That means 1–5 from the app, but also a
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
30 s HTTP timeouts cap the run's network time at about 90 s.
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
- Neon's pooled (PgBouncer) endpoint, or an `idle_in_transaction_session_timeout`
  shorter than the ntfy timeout (30 s). Either would kill the transaction while
  the lock is held across the HTTP call. Production uses the direct endpoint
  (D29).
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
  ±180. Four decimal places is the NWS limit, so the column type makes it
  structural.
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
- A fifth decimal place is **rejected, not rounded**. Rounding silently moves
  the point by up to ~5 m, which makes it a different location.
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
