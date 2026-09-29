# UsageNotch 2.4.0 — quick start

## Start the updated app

1. Right-click your current dock and choose **Quit UsageNotch**. Closing a popup does not quit the app.
2. Double-click **UsageNotch (Latest)** in the project folder, or open `Release/Latest/UsageNotch.exe`. No .NET installation is needed.
3. Keep this EXE wherever you prefer. Settings stay in your Windows account, so your colours, position and providers carry over.

If you ever lose the dock, just run `UsageNotch.exe` again — a second launch brings the running dock back into view instead of doing nothing.

The tray tooltip and Settings header identify the version. Old builds are in `Release/Archive` for recovery; do not launch those for everyday use. The ZIP `Release/UsageNotch-win-x64.zip` always contains the most recently built release. If Windows startup was already enabled, opening the new release updates its startup path automatically.

## Your usage on your phone

Open **Stats & Settings → Phone** (or right-click the tray icon → **Pair a phone…**) and turn on **Share usage with paired phones**. In UsageNotch for Android, tap **Scan QR code** and point the phone at the code. Allow Private networks if Windows Firewall asks. The phone and PC need the same Wi-Fi or a private VPN.

- **Network**: pick the address your phone uses, usually Wi-Fi. WSL/Hyper-V adapters are listed last because phones can't reach them.
- **Copy pairing code** / **Save pairing file**: alternatives to scanning. Send them only to your own phone.
- **Internet sync** (new in 2.4: two clicks): choose **Turn on internet sync**. GitHub opens with a token already set up for gists only; click **Generate token**, then the copy icon. UsageNotch picks the token up from the clipboard, clears the clipboard and turns sync on. GitHub tokens expire after the time you choose on that page; when one does, UsageNotch says so and you turn sync on again. Your phone switches over by itself the next time it's on the same Wi-Fi; no new code to scan. Readings are encrypted before upload; turn it off to delete the gist. **Paste a token instead** still works.
- **History and pace on the phone** (new in 2.4): the phone's History tab shows 30 days of daily usage, your streak and busiest hours, and each limit shows the dock's pace forecast. These come from the history UsageNotch already keeps on this PC.
- **Switch to built-in**: appears when the standalone UsageNotch Link is still running. It closes Link, removes Link from Windows startup and takes over with the same pairing, so your phone keeps working.
- **Revoke all paired phones** disconnects every phone and changes the keys.

Sharing resumes automatically when UsageNotch starts if it was on before. Actions on this page apply immediately; they aren't part of Save/Cancel.

## Dock text, clocks and simpler Stats in 2.2

The collapsed dock hides reset times by default. Hover over a provider to see them in its expanded card. Open **Stats & Settings → Dock** to choose which text appears, enable collapsed-dock reset clocks, hide expanded reset details, or give providers custom dock names. Compact mode hides all dock text.

Open **Time & Stats** for **1:45 PM** or **13:45**, optional clock seconds, and the default level of Stats detail. Countdown timers keep second-by-second precision. The graphic previews use sample data and animate setting changes; reduced motion disables the transitions. Changes preview live, **Save changes** keeps them, and **Cancel** restores your previous preferences.

Stats starts with a simple usage/reset/forecast summary and chart. Expand **Explore history** for the overview, reset-period zoom, sessions, patterns and events. Enable detailed Stats if you prefer those tools open by default.

## Stats, forecasts and updates

Open **Stats & Settings** from the dock/tray menu or run `UsageNotch.exe --settings`. Claude and Codex are visible together, with independent limit buttons and time ranges. Charts show **usage used**, even when the dock shows remaining usage. History stays local with no automatic expiry. There is no backfill; data already deleted by older releases cannot be recovered. Providers without a reported account identity begin a new series each launch.

Hover charts for exact readings; drag a chart or its overview to select an interval. Ctrl+wheel zooms. Reset period focuses the current period. Click a confidence label to read forecast assumptions. Dashed lines and shaded pace scenarios are estimates. Expand sessions, personal patterns or events for their evidence and coverage. Missing data never counts as inactivity.

Resize the window by its edges, use the minimize/maximize buttons, or press F11 for full screen (Escape restores it). Dimensions are remembered. Dock hover remains available while this window is open. Expand **Usage inspector** in a provider hover card for its recent sparkline and estimate; **Explore history** opens that provider's card.

**Usage-pace forecast · estimate** uses at least three observations across ten minutes in the same limit period. It shows percentage points per hour and an estimated time to the limit or percentage at reset. It assumes activity continues at the same pace; it is not a provider promise. Resets, gaps, stale data and insufficient history pause forecasting. Exact reset countdowns update every second, with the provider timestamp shown in your local timezone. An elapsed countdown awaits provider confirmation instead of claiming the quota has reset.

The **Updates** page controls automatic checks/downloads. A verified newer version installs on the next launch, or choose **Restart to update**. That button saves visible settings first. Signature, hash and version checks fail safely. The previous executable stays beside the app as `UsageNotch.exe.previous` for manual recovery.

## Moving the dock

- **Drag it anywhere.** Grab any icon or bare dock surface and move it. Away from the edges it becomes a free-floating capsule.
- **Bring it near a screen edge** and it starts reaching for that edge: the outline stretches into the teardrop and the dock eases ahead of your pointer the closer you get. Let go inside that pull and it snaps home; let go further out and it stays a capsule where you dropped it.
- **Drag up and down while it is on an edge** to set its vertical position. The Appearance slider follows along, and vice versa.
- To place it exactly, use Settings → Appearance → *Position along screen edge*, or right-click → **Snap to left / right / top edge**.
- At the **top**, the dock becomes horizontal and its cards open below it. Drag left/right to reposition it. Top and side positions are remembered separately.
- Turn the magnet off entirely with Settings → Appearance → *Snap to the screen edge when dropped nearby*.
- **Anticipate drag direction** (Appearance) makes the shape begin reaching toward the edge you are moving toward, using a short, bounded movement prediction. It relaxes if you stop or turn away. Prediction never enlarges the snap zone: your release position decides whether it docks. Icons stay upright; the horizontal/vertical layout changes on release. Reduced motion disables prediction.

## Opening settings

- **Double-click bare dock surface** (anywhere that is not a provider ring).
- Or right-click the dock, or the tray icon, and choose **Stats & Settings…**.
- Or run `UsageNotch.exe --settings`.

The settings window is modeless and **live**: every control changes the running dock the moment you touch it, so you can see exactly what you are choosing while you drag and hover the dock underneath. **Save changes** keeps them. **Cancel**, Escape, or closing the window puts everything back the way it was.

## Using the dock

- **Hover a ring** for the detail card: one row per limit window with its percentage and reset time. The duplicated headline summary has been removed.
- **Left-click a ring** to open that provider's dashboard in your browser.
- **Right-click** for Settings, Refresh now, Compact dock, Hide until hover, Click through, Keep above other windows, edge snapping and Quit.

## Things worth turning on

**Usage remaining** — Settings → Display → *Used or remaining*. Choose **Usage remaining** to show what you have left on both rings and in detail cards. Choose **Usage used** to return to the original view. Warning colours and alert thresholds still measure usage used; an unknown reading never becomes a fake 100% remaining.

**Sleek proportions** — Settings → Display. Version 1.9 reduces the standard sleek side dock from 88 to 76 pixels at 100% scale, with smaller 44-pixel rings and shorter shoulders. Switch it off if you prefer the larger layout. Compact mode is even smaller. Your saved spacing and scale are preserved.

**Account name** — Settings → Display → *Account name in detail cards*. Claude uses the saved Claude Code profile name/email; Codex uses its account response. Providers that do not supply a name display “Account name unavailable.” Switch this off before screen sharing if you prefer. This build follows one active login per provider; simultaneous multiple-account monitoring is not enabled.

**Compact dock** — Settings → Display, or the right-click menu. Smaller rings, no labels, a narrower notch. Good on a laptop screen.

**Hide until hover** — Settings → Behaviour. The dock tucks off-screen a moment after your pointer leaves and glides back when you touch the screen edge. Set the delay with the slider on the same page.

**Global hotkeys** — Settings → Behaviour → *Enable global hotkeys*. Click the field next to *Show / hide the dock*, press the combination you want (it needs at least one of Ctrl, Alt, Shift or Win), and do the same for *Refresh every provider*. Backspace clears a field; Escape leaves it alone. If Windows or another app already owns a chord, UsageNotch quietly skips it — pick a different one.

**Alerts** — Settings → Alerts. Choose warning and urgent levels in percent **used**. New floating cards show the provider, what happened, used/remaining amounts and reset time. They do not activate themselves or steal keyboard focus. Hover to keep a card visible; use **×** to dismiss or **Snooze 1 hour** to pause alerts. Uncheck *Use the new floating alert cards* to use Windows tray notifications instead. *Send a test notification* now shows a real, explicitly labeled sample card.

**Quiet hours** — Settings → Alerts. Choose a start/end hour using your computer's local time. Overnight schedules work; matching hours means all day. Warnings during quiet hours or snooze are recorded in **Recent alerts**, without popups or sound. The latest 40 alerts remain available for this running session; the list is cleared when the app exits. Threshold deduplication is saved separately and survives restarts. Quiet-hour warnings are not replayed as a burst later.

**Reliable warnings** — Only live usage can trigger a threshold alert. Saved readings and temporary connection failures do not trigger alerts or erase deduplication history. New accounts prime silently. Reset-time corrections alone do not generate another warning.

**Click through** — makes the dock purely decorative and lets clicks land on whatever is underneath. Turn it back off from the tray icon; the dock itself stops responding while it is on.

**Start with Windows** — Settings → Behaviour. Writes a single per-user entry; unchecking removes it.

## Claude occasionally returns 429

429 means the usage-check endpoint is rate-limiting requests. It does not establish that your chat allowance is exhausted.

1. Leave UsageNotch running. It now respects the server's Retry-After header. Without that header, it progressively waits 2–30 minutes after repeated 429 responses.
2. Do not repeatedly refresh or restart it. Manual refresh also respects the current cooldown.
3. Successfully fetched readings survive restarts in an encrypted, account-matched cache for up to 24 hours. During failures they stay visible with **Saved · not live** and their original timestamp. The retry cooldown also survives restarts.
4. Open the detail card to see the next permitted check time. The actual request occurs on the next scheduled poll after that time.
5. If you need to check immediately, open [Claude's usage page](https://claude.ai/settings/usage). This app cannot remove Anthropic's server-side rate limits.
6. Reconnect Claude only if the app specifically reports expired login, not for a 429.

Healthy Claude checks are limited to once every two minutes, even if you press Refresh faster. Claude Code manages your sign-in; this monitor does not renew or modify its credentials. If the dock says **Sign in**, open VS Code → Claude Code → sign in (or `/login`), then return to the dock and press **Refresh**. Signing into the Claude Windows chat app alone does not update Claude Code's login.

## Gemini: supported steps for your account

Your Google sign-in succeeded, but Google's discovery response returned **UNSUPPORTED_CLIENT** and directed this account to Antigravity. Repeating Gemini CLI sign-in does not restore this quota connection. This build does **not** read Antigravity quotas.

1. Open [Google's official download page](https://antigravity.google/download).
2. Choose **Windows**, then **Antigravity 2.0 — Download for x64** for your current x64 PC.
3. Open the downloaded installer, finish installation, and launch Antigravity.
4. Sign in with the same Google account that owns your **Google AI Pro** subscription.
5. Open Antigravity **Settings** to view baseline quota usage across models.
6. Set **AI Credit Overages** to **Never** if you do not want extra credits consumed after the included allowance.
7. In UsageNotch, you may hide the unavailable ring: right-click dock → **Settings** → **Providers** → uncheck **Gemini** → **Save**.

These steps let you view supported **Antigravity coding quotas**; they do not connect them to this dock or measure Gemini chat-app messages. Do not buy an API key or enable Cloud billing to repair the unsupported connection. If you use Antigravity CLI instead, its `/usage` command displays model quotas.

Official references: [Getting started](https://antigravity.google/docs/getting-started), [plans and quota settings](https://antigravity.google/docs/plans), [CLI usage command](https://antigravity.google/docs/cli/commands/usage).

## Installer and update status

This release is a **portable, unsigned EXE**. Automatic installation/update delivery is **not active**. A signed MSIX/App Installer build workflow is included in the project under `Packaging`, but requires a signing identity, HTTPS release host, and Windows SDK packaging tools. No certificate has been added to Windows trust, and the app does not download or run unverified updates.

For now, always open the **Latest** shortcut. Signed clean-machine installation, upgrade, uninstall and settings migration still need verification after signing/hosting setup. See `Packaging/README.md` in the project for the release workflow.
