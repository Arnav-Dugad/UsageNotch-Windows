# Verification 2.4.0

Checked on September 29, 2026 on the development PC (Windows 11, x64) with a Samsung Galaxy S23+ (Android 16) over USB and Wi-Fi.

- **Existing checks:** all 18 `--check` diagnostics pass.
- **Phone link checks (`--phone-check`, 35 checks):** the 2.3 checks, plus daily consumption counts only rises between continuous readings (gaps and resets are not joined), days without readings stay empty rather than zero, the weekday × hour map and observed-hour counts, a 3-day streak, the forecast's limit time when a fast pace reaches the limit before the reset, local snapshots saying internet sync is off, and handing the sync address and key only to paired phones over the local link. Uploaded snapshots include history but never the sync key. Fixtures written by these checks are decoded by the Android 1.3 unit tests.
- **Signed update:** `--verify-release` passes for the staged `UsageNotch.exe` against the production public key.
- **Live test with the phone:** the staged 2.4.0 build took over the phone port from 2.3.0 with the same pairing. The S23+ running Android 1.3.0 refreshed over Wi-Fi and its History tab showed this PC's real history: 4 of 7 days recorded, a 3-day streak, the busiest day and the busiest hours. Hours with no readings were outlined, not shown as quiet. 2.3.0 was then restored so it can update itself.

Not verified here: the two-click internet sync with a real GitHub account (it needs the owner's sign-in; the token pattern, clipboard clearing and gist creation are covered by code review and the 2.3 gist checks), Windows Firewall prompts on other PCs, and networks other than this PC's Wi-Fi.
