# UsageNotch 2.2.0 verification

The full diagnostics suite passed on September 27, 2026 under the publishing Windows account. The release publish restored dependencies successfully with no warnings. Diagnostics retained a cached NuGet audit connectivity warning, with no build errors.

- Existing settings without the new fields default to no reset text on the collapsed dock, reset details in expanded cards, 12-hour AM/PM clocks, and a simplified Stats view.
- Independent percentage, secondary percentage, suffix, window label, saved badge, reset placement and bounded custom-provider-label controls. Compact mode suppresses every collapsed-dock text field.
- Noon/midnight, 12/24-hour and optional-second formatting, plus settings cloning and restoration.
- New controls apply live; Cancel restores reset placement, clock format and custom labels along with the pre-existing settings.
- All 1,728 dock layout combinations passed, including optional reset/window labels, varying clock formats, compact/full layouts, top/side edges, scales, spacing, mixed provider states and contrast checks.
- Existing history/forecast, provider authentication/cache/cooldown, update signature/tampering, drag/motion, alert and hotkey checks passed.

The actual WPF control trees were rendered using synthetic observations. Visual review covered desktop and narrow Stats layouts, Dock and Time & Stats settings, the white dock theme and used/remaining variants. Reset times appear in expanded cards and are absent from the default collapsed dock. Stats keeps chart tools, sessions, patterns, events and sampling explanations under Explore history. Forecast projections are clipped at the chart ceiling without shifting their time axis.

Animated sample previews share the app's dock text rules, illustrate ring values, text/reset placement, colour, size, position, opacity and auto-hide behavior, and honor reduced-motion settings. They never poll providers or alter history. The native desktop inspection helper remained unavailable after its documented recovery sequence; live input and animation frame-rate measurements were not performed. Static WPF renders and automated behavior checks were used instead.

Publication and local installation completed on September 27, 2026:

- Release `v2.2.0` targets source commit `e17639888e394992dbed122cbcaae9bd723972e2`. The production public key verified the manifest signature and executable; all four uploaded GitHub asset digests matched their local files.
- The signed installer updated `Release/Latest/UsageNotch.exe` to 2.2.0.0. Its SHA-256 matched the release manifest, one installed app process was running, and the updater retained 2.1.0.0 as its rollback copy.
- The pre-update executable, settings and history were backed up locally under `Release/Archive/Before-2.2.0-20260927-143516`. Every one of the 5,110 pre-update history observations remained unchanged; no existing settings fields changed.
- The public production update check verified the published metadata and reported “You’re up to date.” The versioned ZIP and local latest ZIP alias were updated.
