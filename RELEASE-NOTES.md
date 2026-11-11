# 0.3.0-beta.1 — Pi installation preview

Experimental matched integration bundle for Linux ARM64 PINS. Clean Pi 4 installation is pending; this is not a stable release.

Includes quiet logging and opt-in direct USB (no worker process). The worker remains the default. Direct USB was connected successfully on the development Pi; sustained camera-load and recovery acceptance remain pending. Firmware communication-watchdog settings are never changed automatically. Disable and verify that watchdog over Wi-Fi before testing direct USB under load. Manual Stop and firmware travel limits remain.

Compatibility baseline: PINS 3.3.0.1058 nightly / .NET 10; TPPA 2.2.8.0; ninaAPI 2.2.15.2; Touch-N-Stars 6.5.0-beta22-beta; RPA firmware 1.13.0. Other versions have not been validated. Do not install over different versions without review: the bundle replaces ninaAPI and the web interface, not just the RPA DLL. Installer checks do not establish version compatibility.

Download the linux-arm64 ZIP, verify SHA256SUMS, extract, and read USER-GUIDE.md before installation. Stop imaging and alignment first. Use --fresh-install on first installation. Supply your actual PINS/plugin/web paths; do not copy example paths blindly. Installation creates a rollback backup. The service restarts; hardware is left disconnected.

Direct USB: after installation, use sudo systemctl edit pins and add:

    [Service]
    Environment=PINS_MLASTRO_USB_TRANSPORT=direct

Then restart PINS while idle. Removing this setting restores the worker transport. This setting does not disable the firmware watchdog.

No new APK is required for browser use. The previous experimental APK is not included in this release. Use the Pi web interface for the clean-install test.

Source and component licence notices are included. Do not submit raw logs or profiles with bug reports; use the diagnostic export.
