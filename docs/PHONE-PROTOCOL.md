# UsageNotch phone protocol

Windows serves paired phones over HTTPS pinned to the PC's own certificate, authenticated with `Authorization: Bearer <256-bit key>`. Internet sync uploads the same JSON, AES-256-GCM sealed, to a secret gist. This page is the contract between UsageNotch for Windows (2.6+) and UsageNotch for Android (1.5+).

## Versions and negotiation

- `schema` stays `1`. Android 1.1–1.4 reject any other value, and every field below is optional, so old phones keep working.
- `protocol` is the negotiated version. Windows 2.6 sends `"protocol": 2` and a `features` list. Phones check `features` before they use a capability. They never infer one from the app version.
- A phone states what it understands with request headers: `X-UsageNotch-Protocol: 2` and `X-UsageNotch-Features: refresh,settings,trend,insights,tokens,incidents,accounts`. Windows sends the heavier fields (`trend`, `events`, `sessions`) only when the request asks for protocol 2, so 1.4 phones get the same payload size as before.
- Unknown fields are ignored on both sides. Unknown `features` are ignored.

Feature names in Windows 2.6:

| Feature | Meaning |
| --- | --- |
| `refresh` | `POST /v1/refresh` exists |
| `settings` | `shared` is present and `PUT /v1/settings` exists |
| `trend` | 7-day readings in `history[].trend` |
| `insights` | `unused`, `hits`, `reliability`, `events`, `sessions` in `history[]` |
| `tokens` | `tokens` at the root |
| `incidents` | `incidents` at the root |
| `accounts` | provider ids may carry an account suffix (`claude:work`); `kind` and `accountLabel` are set |
| `report` | `report` at the root: "This week" (Windows 2.8) |
| `alltime` | `allTime` in `history[]`: everything recorded for that window (Windows 2.8) |

## Endpoints

| Method and path | Body | Response |
| --- | --- | --- |
| `GET /v1/snapshot` | none | snapshot JSON |
| `POST /v1/refresh` | empty | Fresh snapshot JSON. Header `X-UsageNotch-Refresh`: `done`, `throttled` (another refresh within 20 s, or provider cooldowns; the snapshot is the latest one) or `timeout` (25 s passed; the snapshot has whatever finished) |
| `PUT /v1/settings` | `{"shared": {…}}`, at most 32 KB, `Content-Type: application/json` | `{"shared": {…merged}}`; 400 when invalid |

All requests are rate-limited together (30 a minute). Query strings are rejected.

## Snapshot additions (protocol 2)

```jsonc
{
  "schema": 1, "protocol": 2,
  "features": ["refresh","settings","trend","insights","tokens","incidents","accounts"],
  "generatedAt": 1730000000000,
  "shared": { /* see Shared settings */ },
  "incidents": [
    { "provider": "claude", "title": "Elevated errors on Claude Opus", "impact": "minor",     // none|minor|major|critical|maintenance
      "status": "investigating", "url": "https://status.claude.com/incidents/x", "updatedAt": 1730000000000 }
  ],
  "tokens": {
    "updatedAt": 1730000000000,
    "sources": [
      { "id": "claude", "label": "Claude Code",
        "today": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0, "total": 0, "cost": 1.23 },  // cost: API-price equivalent in USD, null when unknown
        "week":  { …same… },
        "days":  [ { "date": "2026-10-31", "total": 123, "cost": 0.4 } ],            // 30 local days, oldest first
        "projects": [ { "name": "UsageNotch-Windows", "total": 123, "cost": 0.4, "share": 0.38 } ],  // last 7 days, at most 8; folder name only, never a path
        "models":   [ { "name": "claude-opus-5-5", "total": 123, "cost": 0.4, "share": 0.9 } ] }    // last 7 days, at most 6
    ]
  },
  "providers": [
    { "id": "claude:work", "kind": "claude", "accountLabel": "Work",                      // accounts feature
      "history": [
        { "window": "five_hour", "label": "Current session", "days": [], "heat": [], "observed": [], "streak": 3, "calendar": [],
          "trend":  [ { "at": 1730000000000, "used": 0.42, "period": "p1" } ],             // 7 days, at most 400, oldest first
          "unused": [ { "end": 1730000000000, "peak": 0.54 } ],                            // completed periods, newest last, at most 12
          "hits":   [ { "at": 1730000000000, "reset": 1730003600000 } ],                   // limit reached (≥ 99.5%), last 30 days, at most 20
          "reliability": { "periods": 20, "fixed": 19, "moved": 1, "averageShiftMinutes": 4.0, "confirmed": 18, "changed": 2 },
          "events": [ { "at": 1730000000000, "kind": "Confirmed reset", "text": "…" } ],    // last 7 days, at most 30
          "sessions": [ { "start": 1730000000000, "end": 1730003600000, "used": 12.5, "peak": 30.0 } ] }  // percentage points; at most 6, newest first
      ] }
  ]
}
```

- Windows 2.7 adds, all optional:
  - `providers[].plan`: the subscription as the provider reports it ("Max 5×", "Plus", "Edu", "Edu Pro", "Business", "Pro"…).
  - `providers[].breakdown`: `[{ "name": "Claude Code", "percent": 100 }]`, where this week's usage went (Claude only, for now).
  - `history[].days[].coverage` and `calendar[].coverage`: 0..1 of that local day this PC observed. Daily `used` now includes usage that happened while the PC was off or asleep (counted on the day it was next read) and usage since a renewal, so totals are complete; coverage says how much of the day's timing is known.
  - `tokens.sources[].allDevices: true` marks totals that come from the provider for the whole account (Codex), not just this PC's transcripts, with optional `lifetime`, `peakDaily` and `longestStreak`. Costs are null there.
  - Codex Edu, Business and Enterprise seats report a `<limitId>-monthly` window ("Monthly limit") with `usedAmount`/`limitAmount` in `credits`. A `<limitId>-credits` extra holds a bought credit balance, and `codex-reset-credits` an available free limit reset.
- Windows 2.8 adds two features, both optional and ignored by older phones:
  - `report`: a root `report` object, "This week". The last 7 local days, ending today, against the 7 days before. Sentences are written on the PC so both apps say exactly the same thing.

    ```jsonc
    "report": {
      "from": "2026-11-03", "to": "2026-11-09", "generatedAt": 1730000000000,
      "headline": "A busier week than the last",          // or "A quieter week than the last", "A steady week", "Your week", "Your first week of history"
      "summary": "Nov 3 – Nov 9 · 6 active days",
      "providers": [ { "id": "claude", "name": "Claude", "window": "five_hour", "label": "Current session",
          "used": 4.2, "previous": 3.1,                     // in limits: 4.2 = four full limits and a fifth; null without readings
          "change": 0.35,                                   // (used - previous) / previous; null unless both weeks have 3+ days with readings and previous ≥ 0.05
          "activeDays": 6, "hits": 2, "busiestDay": "2026-11-04", "busiestUsed": 1.3, "streak": 12 } ],
      "insights": [ { "kind": "change", "provider": "claude", "text": "You used Claude 35% more than the week before." } ]   // at most 7, in this order: change, hits, busiest, hours, unused, streak, tokens
    }
    ```

    Project names appear in `insights[].text` only when the PC shares project names with phones.
  - `alltime`: `history[].allTime` is everything this PC has recorded for that window, with no time limit: `{ "since": 1700000000000, "used": 37.4, "activeDays": 120, "recordedDays": 131, "hits": 14, "bestDay": "2026-10-02", "bestUsed": 3.2 }`. `used` and `bestUsed` are in limits, counted the same way as the daily totals. `hits` counts limit periods that reached 99.5%.
- From 2.8, `unused`, `hits` and `reliability` count real limit periods: a period ends only when the limit renews (its reset passed and a later one was reported, or usage fell by more than 10 points). Earlier versions split a period at sampling gaps and 1-minute reset drifts. The `period` field on readings still marks continuous observation segments, for pace estimates.
- Phones keep their own permanent copy (Android 1.8): every snapshot's `calendar` days are saved on the phone and kept until you delete them, so History keeps working past 90 days and while the PC is off.
- Forecast ranges: phones show the pace band as a time range. With `limitAt` set and `highRate` > 0, the earliest likely time is `now + (1 - used) / (highRate / 100)` hours, and the latest is the same with `lowRate`. Show "limit around 4:10 PM (likely 3:40–4:50 PM)" when both come before the reset and are at least 10 minutes apart, "(as early as 3:40 PM)" when only the early one does, and nothing extra otherwise.
- `windows[].forecast` gains `lowRate` and `highRate`: the 10th and 90th percentile observed paces in percentage points per hour, which phones draw as the pace band.
- `unused[].peak` is the highest reading in a finished limit period, so unused capacity is `1 - peak`.
- `reliability.fixed` counts periods whose reported reset time never moved by more than 2 minutes. `moved` counts the others. `confirmed` and `changed` count "Confirmed reset" and "Window changed" events in the last 30 days.

## Shared settings

The two apps sync a last-writer-wins map. Each entry is `{"v": value, "at": epochMillis}`. A `null` value means "back to the default" and stays in the map as a tombstone. When merging, the larger `at` wins; on a tie the PC's copy wins. At most 200 entries.

| Key | Value |
| --- | --- |
| `budget:<providerId>\|<windowId>` | `{"limit": 1–100, "by": 0–1439 or null}`; `by` is minutes after local midnight |
| `accent:<providerId>` | `"#RRGGBB"` |
| `name:<providerId>` | dock name, at most 18 characters |
| `order` | `["claude", "codex", …]`, at most 16 provider ids |
| `alerts:thresholds` | `{"warn": 1–100, "critical": 1–100}` |
| `alerts:quiet` | `{"on": true, "start": 0–23, "end": 0–23}` |

The PC includes `shared` in every snapshot, including the relay copy. A phone pushes its changes with `PUT /v1/settings` whenever it reaches the PC on the local link. Edits made while it's on internet sync wait until then.

## Provider ids and accounts

`claude`, `codex`, `gemini`, `cursor`, `openai_api` and `anthropic_api` are the primary accounts. Extra accounts look like `claude:<slug>` or `codex:<slug>`, where `slug` matches `[a-z0-9-]{1,16}`. `kind` is the part before the colon. Phones choose logos, accents and session/weekly windows by `kind`.
