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
```

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
| Coordinates, topic, and threshold come from `.env` with no schema; only the window is validated. | Block 4 |
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
