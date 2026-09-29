# UsageNotch for Windows

A native Windows 11 edge-pinned usage monitor inspired by the open-source **Codenotch** macOS design. It shows how much of your Claude, Codex, Gemini and Cursor limits you have left, and when they reset.

## Download

**[Download page with setup guide → arnav-dugad.github.io/UsageNotch-Windows](https://arnav-dugad.github.io/UsageNotch-Windows/)**

| Get | File | Link |
| --- | --- | --- |
| **UsageNotch for Windows** (Windows 10/11, x64) | `UsageNotch.exe`, or the smaller `UsageNotch-*-win-x64.zip` | [Latest release](https://github.com/Arnav-Dugad/UsageNotch-Windows/releases/latest) |
| **UsageNotch for Android** (Android 9+) | `UsageNotch-*.apk` | [Android releases](https://github.com/Arnav-Dugad/UsageNotch-Android/releases/latest) |

Phone sharing is built into UsageNotch for Windows 2.3 and later. The separate UsageNotch Link download is only for 2.2 and older.

No installer or .NET runtime is needed. Download `UsageNotch.exe` and double-click it. The app isn't code-signed, so if Windows SmartScreen appears, choose **More info → Run anyway**. Installed copies update themselves from GitHub Releases after verifying a signed update manifest.

## Your usage on your phone

1. Open **Settings → Phone** and turn on **Share usage with paired phones**. Allow Private networks if Windows Firewall asks.
2. In UsageNotch for Android, tap **Scan QR code** and point the phone at the code. No app yet? Scan it with the phone's camera: the page that opens offers the download, then pairs in one tap.

The phone shows the dock's rings, labels, account names (when the dock shows them), countdowns and 24-hour charts, plus home-screen widgets in any size and reset alerts. Phones connect over HTTPS pinned to this PC's own certificate with a random 256-bit key. Only percentages, limits, reset times and recent readings are shared; your AI sign-ins never leave Windows. **Revoke all paired phones** invalidates every code at once.

The phone also gets history (30 days of daily usage, a 90-day calendar, your streak and busiest hours) and the dock's pace forecast for each limit, computed on this PC from the history it already keeps.

Optional **internet sync** brings readings to your phone on mobile data and keeps the latest one after this PC shuts down. Choose **Turn on internet sync**, click **Generate token** on the GitHub page that opens, and copy the token: UsageNotch takes it from the clipboard, clears the clipboard and stores it with Windows DPAPI. Paired phones switch over by themselves over the pinned Wi-Fi link, with no new code. Each reading is encrypted here (AES-256-GCM) and stored in a secret gist in your own GitHub account; only your paired phone has the key. Coming from the standalone UsageNotch Link? Choose **Switch to built-in**: your phone stays paired. See the [Android guide](https://github.com/Arnav-Dugad/UsageNotch-Android#pair-in-one-scan).

## For development

**Start here:** double-click **UsageNotch (Latest)**, or open `Release/Latest/UsageNotch.exe`. Current release: **2.5.0**. Older releases are kept in `Release/Archive`, not mixed with the current app. See [quick start](HOW-TO-USE.md).

2.2 simplifies Stats and adds customizable dock text, animated sample previews and clean AM/PM clocks. Reset times appear in expanded cards by default; collapsed-dock reset labels are optional. Claude and Codex remain visible together, with unlimited local history and explainable estimates. Download the latest app from [GitHub Releases](https://github.com/Arnav-Dugad/UsageNotch-Windows/releases/latest).

**Dock** settings control provider names, percentages, secondary percentages, the “left” suffix, window labels, saved-reading badges and reset-time placement. Custom dock labels are available for Claude, Codex, Gemini and Cursor. Compact mode hides all dock text. Sample previews animate visual changes and respect reduced-motion preferences; edits also preview on the real dock until Save or Cancel.

**Time & Stats** offers 12-hour AM/PM (default), 24-hour time, optional clock seconds, and a detailed Stats default. The main Stats cards show usage, reset time, a short estimate and the chart. **Explore history** reveals overview navigation, sessions, patterns and events. Existing observations, credentials and other preferences are preserved.

Account names use Claude Code's local profile or Codex's documented [`account/read` response](https://learn.chatgpt.com/docs/app-server); the app does not decode Codex auth files. Multiple simultaneous accounts are not enabled because isolated provider login lifecycles have not been verified.

## What this build supports

- **Claude subscription / Claude Code**: reads `~/.claude/.credentials.json` in memory and calls Anthropic's usage endpoint for 5-hour, weekly, model-specific windows, plus extra-usage credits when returned.
- **Codex / ChatGPT subscription**: discovers Codex from a CLI install or the VS Code/Cursor extension, launches `codex app-server --stdio`, and requests `account/rateLimits/read`; it does not open or save Codex auth files.
- **Gemini / Code Assist**: reuses Gemini CLI's Google sign-in and reads per-model quota where Google still permits this client. It does not create billable model requests. If Google returns `UNSUPPORTED_CLIENT`, the card shows Unavailable and Google's Antigravity migration guidance instead of fake stats or repeated sign-in advice.
- **Cursor subscription**: opens `%APPDATA%\Cursor\User\globalStorage\state.vscdb` read-only and uses Cursor's signed-in session against `https://cursor.com/api/usage-summary`.
- **OpenAI API**: uses the official organization Costs API with an **Admin API key** and shows month-to-date spend. Optional budget -> percentage ring.
- **Anthropic API**: uses the official organization Cost Report with an **Admin API credential** and shows month-to-date spend. Optional budget -> percentage ring.

## Security choices

- No browser-cookie scraping.
- No telemetry.
- No token logging.
- Claude/Cursor/Codex/Gemini account tokens are never copied into UsageNotch settings.
- Gemini refreshes use OAuth configuration read from the installed official Gemini CLI, without embedding client values in this repository. Bundled and unbundled npm installs are supported; unfamiliar layouts fail with installation guidance. [Upstream OAuth implementation](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/code_assist/oauth2.ts).
- API Admin keys are encrypted by Windows DPAPI (`CurrentUser`) before being written to `%LOCALAPPDATA%\UsageNotch\settings.json`.
- Provider failures show as unavailable/error; the app does not invent percentages.
- History has no automatic expiry, separated by provider, account fingerprint and limit window. Names and credentials are excluded. Providers without account identity start a new history series on each launch. Existing readings are preserved; readings previously deleted by older releases cannot be recovered.
- The updater checks an embedded ECDSA P-256 public key and the signed executable size/SHA-256 hash, restricts download hosts to GitHub, rejects older versions, and keeps the previous executable. Release signatures are separate from Windows Authenticode; the portable EXE is not Authenticode signed.

## Stats and usage-pace forecasts

Open **Stats & Settings** from the dock or tray. Claude and Codex appear together with separate window labels and percentages. Choose a limit within either card and 24-hour, 7-day, 30-day or all-history range. Charts always show percentage **used**, independently of the dock's used/remaining preference. Gaps and resets are not joined. Drag a chart or its overview to inspect an interval, hover for exact observations, use Ctrl+wheel to zoom, or choose Reset period. All-history navigation samples first/last/min/max readings in SQLite; raw readings remain intact. Select at most 32 days to load exact readings.

Dashed projections and scenario shading begin after the last observation. The shading spans the 10th–90th percentile observed interval rates, not a probability or confidence interval. Confidence disclosures show sample counts, coverage of the three-hour lookback and normalized rate variability. **Limited history** means less than 90 minutes or six rate samples; otherwise a coefficient of variation above 0.75 is **Highly variable usage**, with lower variability labelled **Consistent pace**. These are descriptive quality labels, not calibrated accuracy guarantees.

Observed work sessions are consecutive observation spans separated by gaps over 20 minutes, not keyboard/activity tracking. Their consumption sums positive continuous-period deltas; peak pace is an observed interval rate and can reflect provider rounding. Resets and missing intervals are excluded. Personal patterns use the last 28 days in local time and show coverage hours/date counts. They need at least seven observed dates, with at least three dates per hour or two per weekday and one covered hour per cell. Insufficient coverage stays unknown. Insights link to their chart intervals. Events distinguish confirmed resets from unconfirmed window changes and unavailable readings from provider-wide outages.

The window supports resizing, minimize/maximize, F11 full screen and remembered dimensions. A provider logo connects the hover inspector to its dashboard card; reduced-motion preferences skip this animation. Windows 11 build 22621+ uses the supported system backdrop API: Mica for the workspace, transient Acrylic for the updater window, and solid fallbacks. The layered WPF dock popup intentionally retains its solid fallback. [Microsoft backdrop roles](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type).

Forecasts are estimates, never provider guarantees. They need at least three live observations spanning ten minutes within one limit period, with no gap over twenty minutes and a latest observation no older than fifteen minutes. The rate is the observed increase in percentage points per hour over up to three hours. A constant-pace extrapolation estimates usage at the reported reset, or when the limit would be reached first. Flat readings say **No increase observed**. Unknown reset times, stale/error readings and insufficient history suppress the forecast. Usage decreases, changed reset timestamps and long gaps restart learning. Rolling limits, account changes not reported by a provider, and changes in future activity can invalidate estimates.

Countdowns show days and `HH:mm:ss`. Reset clocks use clean local AM/PM by default; 24-hour time and seconds are configurable. Exact timestamps with seconds and UTC offsets remain available in Stats tooltips. Optional collapsed-dock labels show the first two reported reset clocks; expanded cards show reset clocks by default. An expired countdown says **awaiting provider confirmation** until a fresh reading confirms the reset.

## Automatic updates and publishing

Automatic checks/downloads are enabled by default, run shortly after launch and every six hours, and can be disabled in **Updates**. Verified updates install on the next launch; **Restart to update** installs immediately after saving visible settings. The app never needs a GitHub token. Network or verification failures leave the current app in place. Manual recovery: quit the app and restore `UsageNotch.exe.previous` beside the executable.

See [release publishing and signing](Packaging/RELEASING.md). The existing [MSIX preparation](Packaging/README.md) remains a separate distribution option.

## Build on Windows

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
cd UsageNotch-Windows
.\build.ps1
```

The script publishes a self-contained, single-file x64 app here:

`Release\Latest\UsageNotch.exe`

It also creates a smaller download package at `Release\UsageNotch-win-x64.zip`.

To run it, double-click `UsageNotch.exe`. No separate .NET installation is needed on the destination PC.

You can also open settings directly with `UsageNotch.exe --settings`.

For Gemini, install the official CLI once with `npm.cmd install -g @google/gemini-cli`. In UsageNotch Settings, choose **Connect Gemini with Google**, then choose **Sign in with Google** using your Google AI Pro account. After sign-in, use **Refresh now**.

The connect button opens a dedicated terminal with Cloud credential, project, and API-key overrides cleared only for that process. It does not delete credentials, change Windows environment variables, enable APIs, or enable billing. An existing `GOOGLE_APPLICATION_CREDENTIALS` setting can otherwise cause Gemini CLI to use a Cloud credential instead of your personal account.

If Google still returns **SERVICE_DISABLED**, this is a Google account/project access issue, not a missing UI setting. Do not enable billing or attempt to modify an unfamiliar project just for this monitor. Workspace/enterprise users can explicitly opt into their configured Cloud project in Settings.

In the local verification for this release, clearing the conflicting Cloud credential override allowed personal Google sign-in to complete, but Google's account-discovery response then returned **UNSUPPORTED_CLIENT** and directed this account to **https://antigravity.google**. This is not fixed by reauthentication. Antigravity installation/account integration is a separate next step; this build does not claim to monitor it.

Gemini reports CLI / Code Assist model quota, not the Gemini website's chat or image limits. Codex reports Codex quota, not every ChatGPT chat model.

## Notes / limitations

- Claude's subscription endpoint and Cursor's personal usage endpoint are internal endpoints used by their own clients; they may change. UsageNotch treats them as provider adapters so they can be updated independently.
- OpenAI and Anthropic expose official API **cost/usage** reporting, but not every account exposes a supported endpoint for remaining prepaid API cash balance. This build shows official month-to-date spend, and optionally computes spend-vs-budget if you enter a budget.
- Anthropic's Admin Usage & Cost API is not available to every individual Console account; if your account cannot create an Admin API credential, that provider will show `needsAuth`/unsupported rather than use browser-session scraping.
- Codex `app-server` and Gemini Code Assist's quota schema are evolving interfaces. The provider adapters isolate future protocol updates.

## UI

Version 1.6 makes the dock itself the interaction: one outline that morphs, magnifies and
follows the pointer, and a settings window that edits the running dock rather than a copy of it.

**One outline, continuously morphed.** The dock is drawn from a single parametric path.
Free-floating it is a capsule; clinging to a screen edge it is a teardrop. Both shapes are
described by the same five curve segments, so every value in between is a real shape rather
than a cross-fade — you can watch it stretch out of the edge as you drag it away, and pour
back into the edge as you bring it home.

**Magnetic edges.** Drag the dock anywhere. Within about 150 px of a screen edge it starts
reaching for it: the outline morphs toward the teardrop and the dock eases ahead of your
pointer, in proportion to how close you are. Release inside that pull and it snaps home;
release outside it and it stays a free capsule wherever you dropped it.

**Drag to set the vertical position.** While the dock is on an edge, dragging up and down
slides it along that edge and stores the new position — no slider needed. The Appearance
slider and the drag stay in sync in both directions.

**Dock magnification.** Hovering a provider lifts it and leans it out of the dock, and its
neighbours follow at a smaller amplitude, so the whole column reacts to the pointer.

**Rings.** Each dial is layered: a recessed disc, a theme-aware track, a progress sweep with
a true angular gradient and a lit head, and a glow that grows with usage. Above 90% the glow
breathes; when a reading actually moves the head blooms briefly so a refresh is felt rather
than guessed at. Loading is a comet-tail spinner, unknown values are a dotted ring, and
sign-in/error states get a dotted ring plus a badge — a broken provider never looks like a
healthy 0%.

**Detail card.** Hovering a ring opens a card with the provider, a live/saved/error status
pill, a hero block showing the headline percentage, how much is left and when it resets, then
one row per limit window with a gradient meter, exact percentage, reported amounts and a
reset countdown. Meters stagger in.

**Live settings.** Every control in the settings window edits the running dock the moment you
touch it — size, spacing, position, opacity, colour, mode, compact, glass, providers,
hotkeys, alerts. **Save changes** keeps them; **Cancel**, Escape or closing the window puts
everything back exactly as it was. The settings window is modeless, so you can keep dragging
and hovering the dock while you tune it.

Other things in 1.6:

- Double-click bare dock surface to open settings; right-click for the full menu.
- Launching UsageNotch again reveals the running dock instead of doing nothing, and
  `UsageNotch.exe --settings` opens settings on the running dock.
- Multi-monitor aware: the dock docks to the edge of whichever screen you left it on.
- **Compact dock** — smaller rings, no labels, a narrower notch.
- **Hide until hover** — the dock tucks off-screen after a configurable delay and glides back
  when the pointer touches the screen edge.
- **Click-through** — make the dock ignore the mouse entirely; turn it back off from the tray.
- **Global hotkeys** — chords to show/hide the dock and to refresh every provider.
- **Threshold alerts** — Windows notifications at a warning level and an urgent level, plus an
  optional note when a limit resets. Each window fires once per level per reset period, and
  the state survives restarts.
- **Start with Windows** — a per-user run entry, written only when you ask for it.
- **Rebuilt settings** — sidebar navigation across Display, Appearance, Behaviour, Alerts,
  Providers and API budgets, with a live ring preview and a readout beside every slider.

Settings include:

- Classic session, weekly, most-used, or Orbit dual-ring mode.
- Orbit: outer ring = 5-hour; inner ring = 7-day for Claude and Codex. Gemini uses two
  distinct model buckets with numeric labels.
- Dock size, provider spacing, vertical position, left/right edge, opacity, glass sheen and
  compact mode.
- Percentage labels, provider names, monochrome/brand-colour logos, and optional dashboard
  button.
- Reduced motion, hover delay, refresh interval, topmost mode, tray visibility, and provider
  toggles.

Claude 429 responses honour Retry-After, with an exponential 2-30 minute fallback cooldown.
The last good reading from the current process stays visible with a saved/not-live label and
its original timestamp. Changing appearance does not reset Claude's cooldown. Step-by-step
usage and Gemini guidance are in [HOW-TO-USE.md](HOW-TO-USE.md), also included in the release
ZIP.

The visual QA artifacts in `Diagnostics/Artifacts` use explicitly labelled sample data.
Production never displays those fixtures. The diagnostics harness supports `--check`
(window selection, alert thresholds, hotkey chords, theme contrast, the dock outline at every
blend step, and that every settings control edits the dock live and cancels cleanly),
`--preview` (WPF renders of the dock, card, compact mode, error/auth/loading states, the
capsule-to-teardrop strip and settings), and `--gemini` (token-safe live provider check).

The visual dimensions follow Codenotch's public MIT-licensed design specification: black edge
pill, 44-50 px provider rings, used-percentage labels, state colours, and hover detail card.
This implementation is new C#/WPF code; no macOS implementation code is required at runtime.

## Attribution

UI concept/design inspired by: https://github.com/vinzdg/codenotch (MIT License).

Provider marks: Simple Icons v14 (OpenAI, Claude, Google Gemini) and Lobe Icons (Cursor), obtained from their published SVG sources. Marks belong to their respective owners; UsageNotch is an independent app and is not endorsed by those providers.
Sources: https://cdn.jsdelivr.net/npm/simple-icons@14/icons/ and https://github.com/lobehub/lobe-icons/blob/master/packages/static-svg/icons/cursor.svg
The corresponding notice is embedded in the EXE and available through Settings → Icon credits.
