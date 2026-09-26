# UsageNotch 1.7.0 verification

Verified on 8 September 2026:

- Compiled production and diagnostic projects successfully.
- 432 rendered layout combinations: left/right/top, pinned/floating, full/compact, scale, spacing, labels and dual usage.
- Dock outline and hit-target clip resize together; provider centres remain aligned and inside the silhouette.
- Settings changes update live; top/side position values remain separate and snapshot restoration is checked.
- Claude adapter tests cover expired credentials, renewed login, rejected-token suppression, healthy polling throttle, delta/date Retry-After, genuine saved readings, encrypted restart cache, persistent cooldown, account isolation and corrupt cache recovery.
- Real Claude usage request with the user's renewed login succeeded: current session 0%, all models 0%. No model request was made.
- Existing quota parsing, alerts, hotkeys, colour contrast and morph geometry checks passed.
- Inspected WPF-rendered top, side and white dock previews with explicitly labelled sample data. These are not screenshots of a hands-on desktop session.
- Portable EXE version and ZIP contents checked; packaged EXE SHA-256 matches the standalone EXE.
- Six old release directories moved into Release/Archive, not deleted. User settings and credentials were preserved.

Limitations: the Computer plugin returned “Trusted RPC service is not configured: sky”, so interactive desktop testing could not be completed this turn. The sandboxed publish could not retrieve NuGet vulnerability metadata; compilation and packaging still succeeded. External provider rate limits and expired sign-ins cannot be eliminated by the dock.
