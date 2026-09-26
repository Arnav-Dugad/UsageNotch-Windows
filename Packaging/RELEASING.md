# Portable release publishing

Repository: https://github.com/Arnav-Dugad/UsageNotch-Windows

The portable updater verifies ECDSA P-256 signatures over the exact UTF-8 `update.json` bytes, then checks the executable's signed size and SHA-256. The public key is embedded in the EXE. The private signing key is a non-exportable, per-user Windows CNG key named `UsageNotch.ReleaseSigning.v1`; it is not in Git or GitHub. It was initialized on the original publishing PC. Do not send private keys in chat or commit them.

1. Increase the three-part version in `UsageNotch.Windows.csproj` (the assembly/file fourth component must be zero).
2. Run diagnostics: `dotnet run --project Diagnostics/UsageNotch.Diagnostics.csproj -c Release -- --check`.
3. Quit the current local app, then run `build.ps1`. Keep a copy of the previous release before replacing it.
4. Run `Packaging/Sign-Update.ps1` under the Windows account that owns the CNG signing key. It signs the executable in `Release/Latest` and writes `Release/update.json` and `Release/update.sig`.
5. Commit and push the source. Create tag `vX.Y.Z` on that commit and a draft GitHub release with the same tag.
6. Attach `Release/Latest/UsageNotch.exe`, `Release/UsageNotch-win-x64.zip`, `Release/update.json`, and `Release/update.sig` to that draft. Verify all assets before publishing it as the latest stable release.
7. Test **Check now** in the installed app. Older installations verify the signature and download the exact versioned executable URL from the manifest. They install on next launch or **Restart to update**.

Do not edit or re-sign a published version: issue a new version. Source-only builds can use the checked-in public key without signing access. The `Initialize-UpdateKey.ps1` script refuses to overwrite a different existing public key. Losing this Windows key requires a deliberate trust-root migration/manual reinstall; there is no silent key rotation or unsigned fallback.

The executable is not Authenticode signed. Manifest signing authenticates app updates but does not establish Windows SmartScreen reputation. MSIX/code-signing certificate setup is a separate workflow documented in `README.md` in this directory.

## Install/recovery behavior

Downloads are bounded, cancelled after five minutes, and staged under `%LOCALAPPDATA%/UsageNotch/Updates`. HTTPS redirects are restricted to GitHub's release hosts. No archive is extracted and no remote script runs. A copy of the currently trusted app waits for the parent to exit, re-verifies the manifest, copies the candidate next to the destination, verifies that copy, and atomically replaces the old executable. Versions at or below the installed version are rejected. A backup named `UsageNotch.exe.previous` is retained. Failure to launch rolls back automatically; a later runtime failure may require restoring the backup manually. Settings and history are never moved or deleted by the updater.

Install in a directory your Windows account can write. Protected locations do not trigger elevation; installation fails safely and the existing app remains available. Update signatures protect against tampered remote assets; they do not protect against malware already running with access to the user's account or the signing key.
