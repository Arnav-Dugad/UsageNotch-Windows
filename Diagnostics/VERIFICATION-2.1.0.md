# UsageNotch 2.1.0 verification

The full Windows diagnostics suite passed under the publishing Windows account on September 27, 2026. The self-contained win-x64 publish completed successfully. A stale NuGet audit warning remained in the diagnostics restore cache; the release restore completed with online access and no warning.

- Unlimited retention across the former 30-day boundary, SQLite overview queries, identity separation, deduplication, reset/gap segmentation and exact countdown boundaries.
- Forecast coverage, consistent/variable/limited-history labels, scenario rate ordering, stale/error suppression, sparse pattern suppression and interval-linked insights.
- Signed release acceptance and rejection of wrong signers, tampered metadata/payloads, foreign URLs and oversized downloads.
- 1,728 rendered dock layouts with reset timestamps, including compact/full, scale, spacing, top/side docks and mixed provider states. Adding reset timestamps exposed a top-dock spacing regression; stretching the inner layout fixed it and the complete matrix passed.
- Settings-open hover regression, independent resizable dashboard, simultaneous Claude/Codex cards and exact local reset timestamps.
- Existing drag geometry, used/remaining modes, alert lifecycle, Claude authentication/cache/cooldown recovery, provider parsing, hotkeys and live settings/cancel behavior.

Native UI review used synthetic observations in a separate diagnostics process. Confirmed side-by-side provider cards, full-screen layout, reset-period zoom, drag-to-select chart intervals with overview position, and expandable forecast explanations. Early asynchronous window initialization exposed a dispatcher-affinity issue; history rendering now explicitly returns to the owning dispatcher. The review process was closed after testing.

Windows materials use the documented Windows 11 backdrop API, with a solid fallback for unsupported systems, high contrast, or layered windows. The layered dock popup keeps its solid material. Mixed-DPI connected-animation behavior and long-running performance over years of accumulated history have not been exhaustively tested. Quality labels and scenario bands are descriptive estimates, not calibrated probabilities. Observation sessions do not measure actual keyboard activity.
