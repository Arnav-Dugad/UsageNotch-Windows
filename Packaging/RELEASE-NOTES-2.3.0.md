UsageNotch 2.3 puts your usage on your phone with one scan.

**Phone sharing, built in**
- Open **Settings → Phone**, turn on **Share usage with paired phones**, and scan the QR code with UsageNotch for Android 1.2. There's no separate Link app to run anymore.
- No app on the phone yet? Scan the code with the phone's camera. The page that opens offers the download, then pairs in one tap.
- The phone gets exactly what the dock shows: limit names, percentages, reset times, 24-hour history, status, dashboard links, and account names when the dock shows them.
- Optional **internet sync**: readings are encrypted on this PC and stored in a secret gist in your own GitHub account, so your phone stays current on mobile data and keeps the latest reading after this PC shuts down.
- Coming from UsageNotch Link? Choose **Switch to built-in**. Your phone stays paired with the same key and certificate, and Link is removed from Windows startup.
- The network list now offers your Wi-Fi first, not WSL/Hyper-V adapters that phones can't reach.
- Tray menu: **Pair a phone…** opens the Phone page directly.

Security: phones connect over HTTPS pinned to this PC's own certificate, with a random 256-bit key. The server answers one read-only route with rate limits. Your AI sign-ins never leave Windows. **Revoke all paired phones** changes every key at once.

Installed copies update automatically: the new version installs on next launch, or choose **Restart to update** in Settings → Updates. Your settings, history and existing phone pairing are kept. The download is larger than before (about 190 MB) because it now includes the secure web server that phones connect to.
