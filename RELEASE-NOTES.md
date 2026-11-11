# 0.3.0-beta.2 — Pi installation preview

Experimental matched integration bundle for Linux ARM64 PINS. Clean Pi 4 installation is pending; this is not a stable release.

Includes quiet logging and direct USB as the only USB transport. The separate USB worker and its client/source/tests have been removed. No environment override is required. This exact beta still needs clean Pi 4 installation, camera-load and reconnect testing. Firmware communication-watchdog settings are never changed automatically. Enable/disable remains available over an active Wi-Fi connection; set it explicitly there before switching to USB. Manual Stop and firmware travel limits remain.

Compatibility baseline: PINS 3.3.0.1058 nightly / .NET 10; TPPA 2.2.8.0; ninaAPI 2.2.15.2; Touch-N-Stars 6.5.0-beta22-beta; RPA firmware 1.13.0. Other versions have not been validated. Do not install over different versions without review: the bundle replaces ninaAPI and the web interface, not just the RPA DLL. Installer checks do not establish version compatibility.

Download the linux-arm64 ZIP, verify SHA256SUMS, extract, and read USER-GUIDE.md before installation. Stop imaging and alignment first. Use --fresh-install on first installation. Supply your actual PINS/plugin/web paths; do not copy example paths blindly. Installation creates a rollback backup. The service restarts; hardware is left disconnected.


No new APK is required for browser use. The previous experimental APK is not included in this release. Use the Pi web interface for the clean-install test.

Source and component licence notices are included. Do not submit raw logs or profiles with bug reports; use the diagnostic export.
