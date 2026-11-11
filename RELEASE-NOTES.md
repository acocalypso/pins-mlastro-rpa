# 0.3.0-beta.2 — MLAstro RPA for PINS

Beta release for Linux ARM64 PINS.

Includes quiet logging and direct USB as the only USB transport. The separate USB worker and its client/source/tests have been removed. No environment override is required. Firmware communication-watchdog settings are never changed automatically. Enable/disable remains available over an active Wi-Fi connection; set it explicitly there before switching to USB. Manual Stop and firmware travel limits remain.

Compatibility baseline: PINS 3.3.0.1058 nightly / .NET 10; TPPA 2.2.8.0; ninaAPI 2.2.15.2; Touch-N-Stars 6.5.0-beta22-beta; RPA firmware 1.13.0. Other versions have not been validated. Do not install over different versions without review: the bundle replaces ninaAPI and the web interface, not just the RPA DLL. Installer checks do not establish version compatibility.

Download the linux-arm64 ZIP, verify SHA256SUMS, extract, and read USER-GUIDE.md before installation. Stop imaging and alignment first. Use --fresh-install on first installation. Supply your actual PINS/plugin/web paths; do not copy example paths blindly. Installation creates a rollback backup. The service restarts; hardware is left disconnected.


Android APK included: **Touch-N-Stars MLAstro Test** (Android 10+). This is the compatible 0.2.0 interface APK; beta.2 USB changes run on the Pi.

## Installation


## Requirements

64-bit Raspberry Pi OS with PINS and Python 3.9+. This bundle targets PINS 3.3.0.1058 nightly (.NET 10), TPPA 2.2.8.0, ninaAPI 2.2.15.2 and Touch-N-Stars 6.5.0-beta22-beta. Check versions before proceeding: the installer replaces ninaAPI and the Touch-N-Stars web interface as well as adding the RPA plugin. It does not update PINS or TPPA.

## Install on the Pi

1. Stop imaging, live view, focusing and polar alignment; ensure the mount is not slewing. Leave PINS running for the installer's status checks. Make a separate backup of your installation.
2. Download `pins-mlastro-rpa-0.3.0-beta.2-linux-arm64.zip` and `SHA256SUMS-0.3.0-beta.2.txt` from this release into the same directory on the Pi.
3. Open a terminal there and run:

```sh
sha256sum --ignore-missing -c SHA256SUMS-0.3.0-beta.2.txt
unzip pins-mlastro-rpa-0.3.0-beta.2-linux-arm64.zip -d mlastro-rpa-beta2
cd mlastro-rpa-beta2
```

The ZIP must show `OK`. The following paths match a standard installation under user `pi`; adjust them if your installation uses different locations. Confirm the ninaAPI DLL and Touch-N-Stars app directory exist before continuing.

```sh
python3 install_bundle.py --check --fresh-install \
  --pins-home /home/pi/pins \
  --plugin-root /home/pi/.local/share/NINA/Plugins/3.0.0 \
  --nina-dll /home/pi/.local/share/NINA/Plugins/3.0.0/ninaAPI/ninaAPI.dll \
  --web-root /home/pi/.local/share/NINA/Plugins/3.0.0/Touch-N-Stars/app \
  --service pins
```

After that succeeds, run the same command with `sudo python3` and replace `--check` with `--install`. For an upgrade, omit `--fresh-install` from both commands. The installer restarts PINS and prints a backup directory: keep it for rollback. See USER-GUIDE.md for recovery instructions.

4. Reopen the Pi's Touch-N-Stars page (usually `http://YOUR_PI_IP:5000`) and refresh it. Open **MLAstro RPA**, choose USB or Wi-Fi and connect. USB uses direct serial communication with no separate worker.

## Android

Download `Touch-N-Stars-MLAstro-0.2.0.apk` on your Android phone and open it. Allow installation from that browser/file manager if Android asks. It installs as **Touch-N-Stars MLAstro Test**, separately from the official app. Android 10 or newer is required. Configure it with your Pi's address. If upgrading our test app, install over the existing copy.

The APK is the existing compatible interface build; its Android version label is `6.4.0-mlastro-test`. The USB changes in beta.2 are on the Pi, not in the APK. The APK does not install the Pi components. Browser use needs no APK.

## Firmware watchdog

The plugin preserves the device's communication-watchdog setting. To enable or disable it, connect the plugin to the RPA over **Wi-Fi**, open the watchdog control, apply your choice and verify its reported state. You can then return to USB. Disabling it removes the automatic heartbeat-loss stop; manual Stop and firmware travel limits remain. Changing it over USB is not supported.

## First use

Choose a small manual step and confirm the direction with the mount attended. For automated alignment, configure TPPA's camera/exposure/start position, connect and unpark the mount, choose a tolerance and press **Start automated alignment**. Allow room for the mount's TPPA reference sweep. Report issues with the plugin's diagnostic export rather than raw profiles or logs.


Source and component licence notices are included. Do not submit raw logs or profiles with bug reports; use the diagnostic export.
