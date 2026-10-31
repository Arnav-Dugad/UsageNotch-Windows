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
