# Microsoft Store publishing — simple guide

Checked against Microsoft's documentation on 9 September 2026. This is guidance, not a published Store listing. UsageNotch's portable EXE remains unsigned.

## Is it free?

- **Developer registration:** Microsoft's new onboarding flow is free for both individuals and companies. The previous $19 individual and $99 company charges are waived in that flow. Start at [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com), not an old Partner Center sign-up link. [Individual registration](https://learn.microsoft.com/en-us/windows/apps/publish/whats-new-individual-developer) · [Company registration](https://learn.microsoft.com/en-us/windows/apps/publish/whats-new-company-developer).
- **Signing and update delivery:** for a Store-distributed **MSIX** package, Microsoft signs the approved package and hosts/delivers updates. You do not need to buy a separate signing certificate for this route. The Store's MSI/EXE submission route is different: you must sign and host that installer and handle its updates yourself. [Distribution comparison](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path) · [Signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options).
- **If you charge money later:** Microsoft's commerce platform takes 15% for apps. Eligible non-game apps using their own commerce platform keep their revenue without a Microsoft revenue share; payment-processor fees and other business costs can still apply. [Store benefits and revenue share](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/why-distribute-through-store).

For a free personal dock, the Store/MSIX route is my recommended starting point: signed installation and managed updates, without maintaining a download server. It does not make the AI subscriptions free or remove provider limits.

## Your first steps

1. Open [the Store developer website](https://storedeveloper.microsoft.com) and choose **Get started for free**.
2. Choose **Individual** for a personal/hobby project, or **Company** when publishing for your business. Company registration requires business verification.
3. Sign in with your Microsoft account. Complete any ID/selfie verification directly on Microsoft's site; do not send identity documents or passwords in chat.
4. Finish setup and open Partner Center's **Apps & Games** area. Follow its app-creation flow and reserve the name you want, if available.
5. Share only the non-secret app identity/publisher values needed for packaging. We can then prepare a Store-specific MSIX and the listing materials.
6. Test installation, existing-settings migration, Claude/Codex credential access, tray behaviour, startup, updating and uninstalling the packaged app. This is still outstanding; testing the portable EXE does not establish MSIX compatibility.
7. Prepare accurate screenshots, a support contact, a privacy policy describing account/usage data handling, and the listing description. Review provider integration and logo permissions before public release.
8. Submit for Microsoft's certification. Approval is not automatic. Once approved and installed through the Store, future approved versions can be delivered by the Store, subject to users' update settings and device policy.

Microsoft's [publishing overview](https://learn.microsoft.com/en-us/windows/apps/publish/) links to submission, certification and update requirements.

## Not done yet

No developer account has been created, no agreement accepted, no ID collected, no listing published and no certificate purchased. The current direct-distribution signing script is not a substitute for Store-specific packaging and certification testing.
