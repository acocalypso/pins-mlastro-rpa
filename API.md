# Control API

Local service: `http://127.0.0.1:1891`. ninaAPI relay prefix: `/v2/api/mlastro`. JSON request bodies are limited to 4096 bytes. State conflicts return 409, invalid inputs 400. No connection or automation starts on plugin load.

| Method | Path | Purpose / body |
|---|---|---|
| GET | /status | Connection, active transport/address, device/firmware state, manual/jog and automation/session status |
| GET | /usb-devices | Metadata list and suggested unambiguous selection; no ports opened |
| POST | /connect | `{ "transport":"usb", "address":"auto" }`, an explicit Linux serial path, or `{ "transport":"wifi", "address":"mlastrorpa.local" }` |
| POST | /disconnect | `{}`; disables automation and releases connection |
| POST | /move | `{ "axis":"az", "direction":1, "arcseconds":5 }`; integer 1–120 arcseconds; axis az/alt; direction -1/+1 |
| POST | /jog/start | axis and direction; returns leaseId |
| POST | /jog/renew | leaseId; expires without browser renewal |
| POST | /jog/stop | leaseId; stale leases cannot stop newer ones |
| POST | /stop | `{}`; stop/cancel and disarm |
| GET/POST | /device-settings | speedLevel 1–5, backlashEnabled, integer azSteps/altSteps 0–10000000; explicit apply and fresh readback |
| GET/POST | /settings | factor 0.1–1, maxStepArcMin 0.1–10, settleSeconds 1–60, pauseBeforeFirstCorrection |
| POST | /discover | Capability-only TPPA exchange |
| POST | /select-controller | Select RPA for the next automated run |
| POST | /automation | enabled boolean; arming requires confirmed READY hardware |

The frontend orchestrates preflight, controller selection, arming and TPPA websocket start. The automation endpoint alone does not launch TPPA. Pending starts expire; completion/cancellation returns TPPA's correction selection to None. Connect has a 45-second request deadline for multiple USB candidates; relay timeout is 50 seconds and frontend connect timeout 55 seconds. Other local operations retain a 10-second request deadline.

`pauseBeforeFirstCorrection` pauses before RPA correction, after TPPA's reference sweep; it does not prevent the initial mount movements. Full sky acceptance remains pending.

The status response includes `motionInhibited`, `motionState` (`Ready`, `Stopping`, or `Faulted`) and `motionFault`. A failed axis release, uncertain transport write, failed STOP or missing post-STOP READY confirmation latches a fault. Manual movement, jog and automation remain blocked even if an old telemetry snapshot says READY. Disconnect remains available; a failed physical stop is still reported as a conflict after the connection closes.

Recovery requires an explicit new connection, completed old writes/cancellation/cleanup, and fresh explicit READY telemetry across the settling interval. Reconnection never resumes movement. A newer Stop or failure invalidates an in-progress recovery. STOP attempts axis release and the general stop independently and disarms automation/releases TPPA ownership even when either attempt fails.
