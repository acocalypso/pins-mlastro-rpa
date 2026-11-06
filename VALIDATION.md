# Validation snapshot — 2026-11-04

## Finite-move telemetry grace

Sky testing exposed two overly broad checks: an active TPPA reference sweep was treated as active RPA movement, and the USB worker applied a one-second jog telemetry deadline to firmware-controlled finite corrections. The parent now tolerates idle session gaps; Wi-Fi silence allows fifteen seconds. Bounded relative steps and alignment corrections allow fifteen seconds without telemetry, while continuous jogs retain short release/liveness deadlines. Firmware watchdog configuration is not changed by these binaries.

Targeted Linux PTY checks passed idle heartbeats, continuous-jog parent loss, continuous-jog telemetry loss, a three-second telemetry/parent pause during a finite correction with subsequent recovery, and relative-step classification with a three-second gap. Commands were not replayed. The plugin builds successfully. The prior idle-session regression and extracted Stop/writer safety checks passed. Complete sky-alignment acceptance remains pending; these tests do not establish that hardware or network dropouts are resolved.

## Earlier validation

| Area | Evidence | Remaining work |
|---|---|---|
| PINS plugin loading | Loaded on Linux ARM64 with installed host assemblies; disconnected and disarmed on load | Broader host/version coverage and packaging |
| TPPA bridge | Installed payload types checked; simulated grant/move/backlash/settle/new-measurement/completion cycle passed | Complete alignment under the stars |
| Manual controls | User reported successful small steps in each direction, lowest-speed jog/release and central Stop | Report did not specify transport; repeat full USB motion/Stop matrix explicitly |
| USB connection | After reboot, real RPA confirmed over USB with firmware 1.13.0, READY, fresh telemetry and speed/backlash settings | Replug/reconnect, exhaustive motion, real cancellation and device-loss testing |
| USB automatic discovery | Simulated candidate scan, busy/impostor rejection, sysfs link fixtures and delayed serial boot passed | More adapters, multiple devices, duplicate USB serial IDs and physical reset behavior |
| Wi-Fi | Earlier supervised manual use succeeded; simulated handshake/correction cycles passed | Latest real reconnect not accepted: Pi network instability and mDNS resolution failures interrupted validation |
| Single-run scope | Simulated completion disarms controller; later ordinary TPPA session cannot reuse it; Stop cleanup passed | Real sequential automated-then-normal TPPA run |
| Frontend / Android | Nine focused frontend tests, lint and builds passed; separate Android test app installed | Native background/network-loss gesture testing; standard upstream distribution |

Tests include a stalled transport, expired/stale leases, explicit release, concurrent/oversized manual rejection, Stop during USB identification and a serial device replying generic OK without RPA identity. The simulated serial firmware uses private Linux pseudoterminals; real serial ports are not opened by the test suite.

The prepared source has portable build-reference and test-runtime paths. The export-only adaptation compiled successfully for the plugin, plugin-check project and diagnostic CLI; protocol checks and the relay compile check also passed. It does not change the movement algorithm or protocol. Test/binary evidence from development does not establish unattended safety, firmware-side deadman behavior, complete USB movement validation or sky alignment accuracy.

USB and host network interruptions occurred during development. A Pi reboot restored real USB communication. No hardware problem, USB cable failure or network root cause has been conclusively established by these tests. Local deployment chronology and private logs are intentionally omitted from this export.


## Active connection display

The panel derives connected transport and address from backend status instead of its newly initialized form defaults. Existing servers reporting usbDevicePath remain supported; a connected USB device stays selected even before USB inventory finishes loading. Connected controls remain disabled. Disconnected draft choices remain editable. A checking state replaces default selections until initial status arrives. Four regression cases cover unloaded state, reopening USB with Wi-Fi defaults, displaying an authoritative Wi-Fi address and retaining disconnected choices. Thirteen frontend tests and plugin lint passed; backend and web/Android builds passed. Browser navigation away and back confirmed the connected USB selection and disabled controls on the real installation.

## Adversarial Stop fixes

The three priority-one Stop defects were addressed: axis release failure no longer skips general STOP or cleanup; synchronous write/cancellation initiation is bounded with unfinished work retained and fenced; failed or unconfirmed stops latch an inhibition that survives cached READY telemetry and observer cancellation. USB and Wi-Fi writes are tied to their captured transport session, and queued motion cannot cross a Stop or reconnect. Missing wireless status remains UNKNOWN.

`tests/Pins.MLAstro.SafetyChecks/run.ps1` extracts the actual plugin Stop and admission methods into fake collaborators. It checks independent stop attempts, cleanup, faulted manual/automation rejection, stale queued requests, recovery invalidation and blocked cancellation. The plugin integration checks additionally use isolated simulated serial and Wi-Fi firmware; they do not command real hardware. Sixteen frontend tests, targeted lint and the web build passed. Full physical loss-of-link, emergency-stop behavior and sky alignment remain acceptance work; these software checks cannot establish a physical stop when the link is unavailable.

The final standalone safety suite and Linux ARM64 plugin simulation passed, including simulated USB discovery/identification, manual controls, jog cancellation and TPPA correction/completion. The Android test build passed. The backend and web bundle were installed on an idle host; the loaded backend hash matched the tested artifact, and status confirmed disconnected hardware with automation disabled. No real movement was requested during this fix round. Physical USB instability remains unresolved.

## USB reply tolerance — 2026-11-05

The Linux ARM64 simulation passed new cases for a two-second idle reply gap, stale-telemetry movement/automation refusal, recovery without disconnection, a short setup gap, disconnection after prolonged silence, stale telemetry during active jog, and firmware heartbeat ESTOP inhibition followed by verified reconnection. The standalone writer checks passed a 400 ms query with the one-second status budget, the unchanged short default write deadline, and STOP waiting for that query before using its own short write budget. Prior Stop and correction-cycle checks also passed.

A Wi-Fi regression additionally passed with active jog, paused position telemetry and unrelated log/error-state frames continuing to arrive. Those unrelated frames did not prevent stale-telemetry inhibition and a general STOP; verified reconnection then cleared the fault.

This change reduces host-side sensitivity to short gaps and removes the synchronous write/reply wait from periodic polling. It does not change firmware watchdog settings or establish immunity to long host pauses during camera image handling. A firmware heartbeat ESTOP remains a fault requiring reconnection. Hardware validation with main-camera downloads remains necessary.

The extended USB-only profile uses a fifteen-second reply grace and a two-second status-write budget. Regression coverage includes a seven-second idle gap with fresh-telemetry recovery, eventual disconnection after fifteen seconds of silence, a 1.5-second query, and STOP queued behind that query with its original short I/O deadline. Movement readiness still expires after one second. Wi-Fi retains its five-second link watchdog. These host limits do not lengthen the device firmware's separate communication watchdog.

## Isolated USB worker

The worker's Linux PTY checks passed idle parent pauses, loss of parent liveness during movement, EOF/parent death, missing identity, expired/fenced requests, busy/stale readiness, firmware ESTOP, idle silence recovery/timeout, command ordering, timestamp preservation and output backpressure. A separate managed-client harness was paused with SIGSTOP for four seconds: USB queries continued in the child. Pausing that parent during a simulated move caused a physical STOP command and latched rejection of further movement. No real serial ports are used by those checks.

The full Linux ARM64 plugin simulation passed with the isolated worker, including USB identification, gap recovery, stale-motion Stop, reconnection, manual controls, TPPA correction/completion and Wi-Fi regressions. One supervised real camera connection/download then completed while USB remained connected; backend status confirmed the isolated worker and READY, and the firmware watchdog readback remained enabled. No real movement was requested. Repeated camera stress, physical movement/Stop validation and complete sky alignment remain acceptance work. A slow or failed driver can still prevent confirmed delivery of STOP, so worker retirement and the independent firmware watchdog remain necessary fallbacks.
