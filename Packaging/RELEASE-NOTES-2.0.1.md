# UsageNotch 2.0.1

- A shared Stats & Settings window with dark usage-history charts and provider/window/time-range selectors.
- Local history for up to 30 days, with honest gaps, reset boundaries and no historical backfill.
- Precise reset countdowns updated each second and exact local timestamps.
- Clearly labelled usage-pace estimates, with learning, stale-data and reset states.
- Automatic release checks and downloads, ECDSA-signed metadata, SHA-256 executable verification, downgrade rejection, restart installation and a previous-executable backup.
- Existing dock placement, appearance, providers, alerts and live settings preserved.
- Gemini OAuth client configuration is discovered from the installed official CLI instead of being embedded in public source.

Download `UsageNotch-win-x64.zip` for the app and quick-start guide, or `UsageNotch.exe` alone. It is self-contained for Windows x64 and does not need a separate .NET installation. History begins after this version starts collecting successful readings.

Forecasts assume the observed pace continues; they are estimates, not provider guarantees. Release manifests are signed; the portable executable is not Authenticode signed. See the repository's publishing guide for signing and recovery details.

Validation: existing diagnostic suite including 1,728 rendered dock layouts; history/forecast/countdown boundary tests; signature and payload tampering rejection; rendered dashboard inspection; signed local upgrade with existing preferences preserved.
