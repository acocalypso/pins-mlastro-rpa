# Control API v2

Plugin 0.2.0. Local service: `http://127.0.0.1:1891`; ninaAPI relay prefix: `/v2/api/mlastro`. Bodies are limited to 4096 bytes. State conflicts return 409 and invalid inputs 400. Loading never connects hardware or arms automation. API v2 reports `continuousJogSupported: false`.

| Method | Path | Purpose |
|---|---|---|
| GET | /status | Connection, authoritative transport/address, firmware, readiness, faultCode, recoveryAction and automation state |
| GET | /usb-devices | Metadata and stable paths; no ports opened |
| POST | /connect | USB address auto or Linux serial path, or Wi-Fi hostname/IP |
| POST | /disconnect | Attempt Stop, disarm and close; closes even if Stop remains unconfirmed |
| POST | /reconnect | Reconnect remembered target while idle; never resumes or replays movement |
| POST | /move | Axis az/alt, direction -1/+1, integer arcseconds 1–120; UI offers 5/10/30/60/120 |
| POST | /stop | Stop/cancel and disarm |
| GET/POST | /device-settings | Firmware speed 1–5, backlash enabled, integer AZ/ALT backlash steps; apply/readback |
| GET/POST | /settings | Correction settings below |
| GET | /watchdog | Device state: supported, enabled, code; unknown is null |
| POST | /watchdog | enabled and confirmed booleans; confirmation required to disable; ACK and fresh readback |
| GET | /diagnostics | Allowlisted report and bounded local Pi power checks; no settings changed |
| POST | /discover | Capability-only TPPA exchange |
| POST | /select-controller | Select RPA for the next automated run |
| POST | /automation | Arm/disarm; does not itself start TPPA |
| POST | /jog/start, /jog/renew, /jog/stop | Retired: 410, no movement, including legacy relay routes |

Correction settings require factor 0.05–1, maxStepArcMin at least 1 (default 120, no added upper ceiling), settleSeconds 0–60 and pauseBeforeFirstCorrection. Optional fields preserve profile values when omitted: axisMode Both/Auto, overshootEnabled, overshootUpEnabled, overshootDownEnabled, finite nonnegative overshootUpArcMin/overshootDownArcMin, autoChangeDirection, reverseAzimuth and reverseAltitude. Overshoot directions are exclusive. Manual reversal changes require automatic direction changes off. Settings cannot change during an RPA automated session.

Watchdog control requires an active Wi-Fi connection to the same RPA; writes also require fresh READY and no RPA operation. USB users switch to Wi-Fi to configure this persistent firmware setting, then may switch back. A saved Wi-Fi address alone cannot authorize changing a USB-connected device. GET returns wifi_required outside Wi-Fi. Unsupported, unavailable and unverified states are distinct from a verified enabled/disabled state. Stop cancels management requests. This firmware heartbeat ESTOP is separate from manual Stop and host timeouts.

The frontend performs preflight, selection, arming and TPPA websocket start. Save reads TPPA tolerance back before claiming success. Pause before first correction applies after the mount's reference sweep. Completion/cancellation restores ordinary TPPA behavior. Pending starts expire after 20 seconds. Connect/reconnect have a 45-second local deadline, relay 50 and frontend 55 seconds. Other local mutations have a 10-second deadline.

motionInhibited, motionState and motionFault distinguish a latched fault from cached device READY. Fault codes include firmware_watchdog, telemetry_timeout, write_unconfirmed, stop_unconfirmed and connection_lost. Reconnect requires no tracked movement/session and fresh READY if the old link is connected. Otherwise Disconnect attempts Stop and closes the old link; Connect performs identity and fresh-READY recovery. Recovery never resumes movement.

Diagnostics allow only numeric versions, transport/readiness/fault categories, observed telemetry timing, watchdog known state, Pi power flags and up to 50 timestamped event codes. Addresses, SSIDs, credentials, serials, usernames, paths, sky coordinates, images, profiles, arbitrary exceptions and raw logs are excluded. Timestamps reveal usage times: review before sharing. Timing is observed by the plugin, not a lossless USB bus trace.

Linux USB has an isolated worker polling every 200 ms. USB and Wi-Fi allow 15 seconds of link silence. New moves require fresh READY within one second; a correction at dispatch waits up to 15 seconds for a fresh sample before sending once. Finite movements allow 15 seconds without telemetry and 30 seconds of parent scheduling delay. Continuous mode is rejected. Parent death/IPC loss attempts Stop/release. Native write timeout is 250 ms with a 300 ms bounded wait; uncertainty faults the link. Generation fences and expiry reject late motion. Firmware watchdog state changes only by explicit verified user request.

Fork note (acocalypso/pins-mlastro-rpa): `/stop` and `/disconnect` also accept an empty body. `/move` and arming through `/automation` wait at most 10 seconds for the control gate and then answer 504 without having dispatched anything; disarming is never abandoned.
