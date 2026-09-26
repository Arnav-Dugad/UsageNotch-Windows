# UsageNotch 1.8.0 verification — 8 September 2026

Completed:

- Production and diagnostics projects compile.
- 1,728 WPF-rendered layout combinations pass: used/remaining, sleek/classic, left/right/top, pinned/floating, compact/full, scale, spacing, labels and dual usage.
- Used/remaining converters preserve original used values for thresholds, keep unknown readings unknown, and support account-label hiding.
- Motion timeline checks confirm transitions capture their current start value and omit animations in reduced-motion mode. These are not frame-rate or end-to-end mouse-interaction measurements.
- Alert tests cover stale suppression, outage deduplication, reset-time corrections, genuine resets, quiet hours across midnight, snooze/resume, account-label separation, startup/restart suppression and sample preview routing.
- Existing provider parsing, encrypted Claude cache, cooldown, settings, colour contrast, hotkey and geometry tests pass.
- Live read-only Claude and Codex checks both returned OK and non-empty account labels. Labels/tokens were not printed. No model messages were sent.
- Inspected remaining-mode, top-edge, white-theme, alert-card and alert-settings renders. Screenshots use explicitly labeled sample data.
- MSIX/App Installer templates validate with dummy identity and HTTPS URL. The signed-build script requires a matching certificate, verifies Windows trust and emits update metadata only after verification. It does not upload, install, or enroll certificates.

Not completed:

- Computer Use failed to connect to its native pipe, so hands-on desktop testing and actual frame-rate verification were not possible.
- No signed installer was generated: publisher signing identity, update hosting, and Windows SDK packaging tools are not configured. Real installation/update/uninstall and portable-to-MSIX settings migration need testing after that setup. The downloadable EXE remains portable and unsigned, with no active updater.
- Simultaneous multi-account monitoring remains disabled. The app follows the active provider login.
- NuGet vulnerability metadata could not be fetched by the sandboxed build. Build warnings are NU1900, not compiler errors.

Release 1.7.0 is preserved in Release/Archive before the Latest files are updated.
