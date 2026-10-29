# PrecipitationNotification

A project for sending notifications when it is predicted to rain or snow, particularly overnight.

## How this was built

Built with an AI pair (Claude Code); commits it wrote carry a `Co-Authored-By`
trailer. Design decisions are mine. Every decision is recorded here along with
the alternative I rejected, and nothing in the decision engine, time handling,
idempotency or tests was committed until I could explain it back without notes.

## Running (Block 1)

```
cp .env.example .env   # then fill in real values
dotnet run --project src/Worker
```

Reads the NWS hourly forecast for the configured coordinates, checks tonight's
overnight window against a fixed probability threshold, and publishes one ntfy
message if anything is over the line.

## Block 1 — deliberate debt

Block 1 is a walking skeleton. The shortcuts below are intentional; each is
repaid in a later block. This list is the running record — the design-doc
write-up lands in Block 8.

| Shortcut | Repaid in |
|---|---|
| Overnight window is always *today 20:00 → tomorrow 07:00*, regardless of when the job runs. | Block 2 |
| Window edges compared as wall-clock time — the fall-back DST night double-counts the repeated 01:00–02:00 hour; the spring-forward night is missing an hour. | Block 2 |
| `PrecipDecision` is throwaway naive code, not a tested pure function. No captured-payload tests. | Block 2 |
| NWS gridpoint URL is re-fetched every run instead of cached. | Block 2 |
| Coordinates, topic, and threshold come from `.env` with no schema or validation. | Block 4 |
| No retry, backoff, circuit breaker, or timeout tuning on the HTTP calls. | Block 5 |
| Single weather provider, no abstraction seam exercised. | Block 5 |
| No persistence and no idempotency — every run that clears the threshold sends a notification. | Block 4 |
| No tracing or metrics. | Block 6 |
| Silence is ambiguous: "no precip" and "the job is dead" look identical. No heartbeat. | Block 6 |
| Console app only — no container, no schedule, runs from a dev machine. | Blocks 3, 7 |
