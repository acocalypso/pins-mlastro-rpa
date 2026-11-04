# Validation snapshot — 2026-11-04

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
