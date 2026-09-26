# UsageNotch 2.0.1 verification

Validated on Windows x64, 26 September 2026.

- Built the self-contained release successfully. The source from before this task is preserved on the local-only `local-preserved-development` branch; original binaries are retained under `Release/Archive`.
- The existing full `--check` suite passed under the user's Windows account: 1,728 rendered dock layouts, 300 bounded drag shapes, hotkeys, alerts, encrypted Claude cache, provider mappings, theme contrast and settings save/cancel behavior. The sandbox could not exercise Windows DPAPI; the same check passed outside it.
- New `--history-check` tests passed for persistence, account separation, duplicate/stale/invalid sample rejection, retention, usage drops, real resets versus timestamp jitter, offline gaps, constant-pace projection, stale forecasts, flat usage, unknown resets and exact countdown boundaries.
- Update tests accepted a valid signature and rejected modified metadata, wrong signing keys, modified payloads, foreign URLs and oversized manifests. The final release signature and executable were verified against the app's embedded production public key.
- Installed Gemini CLI configuration discovery passed without logging or embedding OAuth values. GitHub initially rejected the original adapter's embedded public-client values. The original history was retained locally and the public history was recreated without those values; GitHub then accepted the push.
- Inspected rendered sample-data Stats and Settings pages, then the running app with real provider readings. Verified labelled estimates, empty/learning states, exact local reset time, live countdowns, chart labels and shared navigation.
- Published all four assets only after comparing GitHub-reported SHA-256 digests with local files.
- End-to-end delivery: the running 2.0.0 app fetched the public release, displayed **Downloading verified release 2.0.1**, then **Version 2.0.1 verified and ready**. Restarting the existing app invoked its startup installer, applied the GitHub download and retained the previous executable. Existing preference fields were unchanged through the initial local upgrade.

Release: https://github.com/Arnav-Dugad/UsageNotch-Windows/releases/tag/v2.0.1

Limits: portable EXE is not Authenticode signed; signed manifests authenticate updates separately. Clean-machine MSIX installation, arbitrary multi-monitor/DPI configurations and long-term forecast accuracy were not validated. History starts when collected and is not backfilled. The layout sample in `Diagnostics/Artifacts/Stats.png` is explicitly labelled sample data and is not used by the production app.
