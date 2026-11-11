# MLAstro RPA: quick user guide

## Open and connect

Install the Pi bundle first (see INSTALL.md). In Touch-N-Stars, open **Settings → Plugins**, enable **MLAstro RPA**, then select its tab. Repeat this in each browser or app you use.

For USB, connect the RPA to the Pi, select USB and use automatic device detection. For Wi-Fi, connect the Pi and RPA to a reachable network, select Wi-Fi and enter the RPA hostname or address. Wait for **Connected · READY**. Connect only one controlling app at a time.

## Manual adjustments

Choose a bounded step size (5–120 arcseconds) and use the altitude/azimuth arrows. Each click commands real movement. The speed setting controls motion speed, not the selected angle. Direction changes may take longer while the RPA applies its configured backlash compensation. Avoid repeated clicks while a move is still completing. **STOP** requests a stop; it cannot reach the hardware through a broken connection.

This plugin has no continuous jog control. Backlash and travel limits are managed by the RPA firmware; the plugin preserves them.

## Automated polar alignment

1. Connect the imaging camera and mount in PINS, unpark the mount, and provide room for its reference sweep.
2. Set TPPA's camera, exposure and starting-position options as you normally would. Use a plate-solving setup that already works.
3. Connect the RPA and wait for READY. Choose the finish tolerance in arcminutes; save changed settings if the Save button is available.
4. Press **Start automated alignment**. This selects the RPA for the session and starts TPPA's three-point sweep. Watch TPPA for progress and any errors.
5. The plugin applies corrections between measurements. **Pause** pauses the operation; **Stop alignment** ends it. Keep the equipment attended for the initial run.

Advanced correction settings include the per-correction cap (default 120 arcminutes), correction factor, settling time, axes, altitude overshoot and direction settings. Leave these at their defaults initially. A cap is a maximum per correction, not a requested move or finish tolerance. After an RPA session ends, starting TPPA normally does not automatically enable RPA correction.

## What the communication watchdog does

The firmware watchdog expects regular messages from the controlling computer. If messages stop for its timeout, it stops/inhibits motion even if the motor was otherwise operating normally. A host pause, overloaded USB bus or lost connection can trigger it. This is separate from travel limits, manual Stop and the Raspberry Pi's own system watchdog.

Disabling it can prevent heartbeat-triggered stops during host pauses. It does **not** repair a loose cable, fix a power problem or make a lost connection reliable. It also removes the firmware's automatic response to lost host heartbeats.

### Enable or disable it

1. Stop alignment and wait for the RPA to be idle.
2. Disconnect USB in the plugin, then connect this RPA over **Wi-Fi**.
3. Expand **Advanced correction settings** and find **Firmware communication watchdog**.
4. Check **Current device state**, then select **Enabled** or **Disabled** under **Requested state**.
5. When disabling, tick the acknowledgement explaining that communication-loss protection is removed.
6. Press **Apply watchdog setting**. Wait until the reported **Current device state** confirms your choice. If the readback is unavailable, do not assume it changed.
7. You may now disconnect Wi-Fi and reconnect over USB. The device stores the setting; connecting does not automatically change it.

The current implementation requires Wi-Fi to read/change this setting. If the control reports unavailable or unsupported, check connectivity and firmware compatibility rather than repeatedly applying it.

### Important when using other apps with continuous jog

The watchdog setting belongs to the RPA, not this plugin. If you disable it here and later use another app's continuous jog (press-and-hold movement), losing the connection or missing the release/stop command can leave motion running. This plugin's bounded steps do not provide protection to another app. **Re-enable and verify the watchdog before using continuous jog in another app.** Keep a way to stop the device locally; a disconnected app cannot deliver Stop. Firmware travel limits remain separate and are not a substitute for a working stop path.

## USB or connection problems

- **Disconnects during camera downloads:** check the Pi power supply, USB hub power, cable and connectors. Try a short known-good cable and a direct Pi port. A powered hub can help isolate peripheral power demand.
- **Pi 5 USB current budget:** the high-current USB setting may help when the attached supply can support it. Do not assume this Pi 5 setting exists on a Pi 4. Increasing the budget does not increase the supply's actual capacity.
- **Heartbeat/ESTOP despite a physically connected cable:** review the firmware watchdog state over Wi-Fi. If you choose to disable it, read the continuous-jog warning above first.
- **Connected/READY but controls disabled:** check the displayed reason, whether TPPA owns the session, and whether a previous operation is still finishing. Stop the session if appropriate. Use the recovery prompt rather than sending repeated moves.
- **Disconnect or fault during alignment:** stop the alignment, check cables/power, reconnect and wait for fresh READY status. Inspect the mount before starting a new session; reconnection does not automatically resume alignment.
- **Wi-Fi hostname fails:** use the RPA's current IP address and check both devices' network access. Ensure another app is not simultaneously controlling it.
- **No tab:** enable MLAstro RPA under Settings → Plugins in this client. If it is absent from that list, check the installed frontend/APK version.

Use **Run connection check** and **Export diagnostics** when reporting a problem. Include the connection method, firmware and PINS versions and what was happening when it failed. Avoid sharing raw profiles, network credentials or unfiltered logs.
