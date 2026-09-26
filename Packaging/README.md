# Signed installer and automatic updates

MSIX status: **build preparation only**. No Authenticode signing identity or Windows SDK packaging tools have been configured. The portable 2.0 app uses a separate signed-manifest updater through GitHub Releases; see [portable publishing](RELEASING.md). Its release manifests are signed, while the EXE itself is not Authenticode signed.

Two supported distribution choices:

- **Microsoft Store MSIX**: Microsoft signs the approved package and manages updates. Requires your publisher enrollment and app submission.
- **Your own HTTPS release host**: requires a publicly trusted code-signing certificate or configured signing service. This script supports an explicitly selected certificate in your Windows CurrentUser/My certificate store. Cloud signing needs its service-specific integration; it is not implemented here.

## Direct distribution build

1. Obtain the signing identity. Do not send private keys or passwords in chat. Install Windows SDK packaging tools on your build PC.
2. Choose a permanent package Identity, exact certificate Publisher subject, and HTTPS update directory. Keep identity/publisher the same for later updates.
3. Run `build.ps1` to create the matching portable release.
4. Run `Packaging/Build-SignedInstaller.ps1` with `-Identity`, `-Publisher`, `-PublisherDisplayName`, `-UpdateBaseUrl` and `-CertificateThumbprint`. Use `-ValidateOnly` first to check template substitution without signing or installing anything.
5. Upload the versioned `.msix` first, then `UsageNotch.appinstaller`, to your selected directory. Hosting should serve `.msix` as `application/msix` and `.appinstaller` as `application/appinstaller`.
6. Install through the hosted `.appinstaller`, not the portable EXE. Windows checks on launch (12-hour interval) and in the background. Updates do not block launching the app; a running dock may need to close before an update can apply.
7. For each release, increase the first three version components in the project and keep the fourth component zero. Publish the signed package first and update the descriptor last. Downgrades are disabled.

The script never enrolls a certificate into Windows trust, stores a private key, uploads files or kills the running dock. Manifest XML validation is not a substitute for a signed clean-machine install/update/uninstall test. MSIX can isolate app data from the portable version: test settings migration, provider credential access, tray behavior and startup registration before distributing publicly.

Official references: [Microsoft signing guidance](https://learn.microsoft.com/en-us/windows/msix/package/sign-app-package-using-signtool), [App Installer update descriptors](https://learn.microsoft.com/en-us/windows/msix/app-installer/how-to-create-appinstaller-file), [Microsoft Store and other signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options).
