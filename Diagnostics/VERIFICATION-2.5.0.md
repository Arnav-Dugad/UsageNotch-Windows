# Verification 2.5.0

Checked on September 29, 2026 on the development PC (Windows 11, x64) with a Samsung Galaxy S23+ (Android 16).

- **Existing checks:** all 18 `--check` diagnostics pass.
- **Phone link checks (`--phone-check`, 36 checks):** the 2.4 checks, plus the 90-day calendar: readings 60 days ago appear in it, while the 30-day days, weekday × hour map and observed hours are unchanged, so Android 1.3 reads exactly what it did before. Days without readings stay empty in both. Fixtures written by these checks are decoded by the Android 1.4 unit tests, including the calendar.
- **Signed update:** `--verify-release` passes for the staged `UsageNotch.exe` against the production public key.
- **Live test with the phone:** the staged 2.5.0 build took over the phone port from 2.4.0 with the same pairing. Android 1.4.0 on the S23+ refreshed over Wi-Fi and its History tab showed a 90-day calendar from this PC's real history, with the days this PC wasn't recording outlined as no data. 2.4.0 was then restored so it can update itself.

Not verified here: phones that only use internet sync (the calendar travels in the same encrypted snapshot), and PCs with months of recorded history (covered by the synthetic check above).
