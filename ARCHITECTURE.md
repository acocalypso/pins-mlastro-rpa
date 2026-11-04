# Architecture and behavior

The MEF composition root is `MLAstroPlugin`; it owns the serial/Wi-Fi services, loopback control API, correction runner and single-session TPPA selection lifecycle. Its assembly GUID differs from the upstream Windows plugin. Do not run two controller owners against the same device.

Touch-N-Stars calls ninaAPI's fixed `/v2/api/mlastro/*` routes. The relay calls `127.0.0.1:1891`; the local service rejects browser Origin headers. This is a local-development integration using the surrounding application's access controls, not a newly designed independent authentication system.

The upstream MLAstro broker correction engine is retained. TPPA owns reference captures, capture-window grants, solving, settling, tolerance verification and session completion. The RPA runner performs corrections only while explicitly enabled and selected. A later ordinary TPPA run must not inherit RPA automation. TPPA's ManualMode concerns its reference-movement procedure; resetting the correction controller does not force that flag.

USB discovery reads sysfs and stable serial aliases without opening devices. Connect tries compatible candidates sequentially, checks MLAstro firmware acknowledgement plus fresh telemetry, and starts periodic monitoring after confirmation. Stop invalidates pending attempts. Failed unconfirmed probes are closed without a firmware Disconnect command. A bounded foreground close wait prevents slow driver cleanup from indefinitely owning the shared connection gate; background OS cleanup may still leave that particular port unavailable temporarily.

Manual relative moves use the official firmware arrow/relative command path. No fixed direction-reversal allowance is added by the host. Firmware backlash/limits remain active. Step completion requires telemetry evidence, not merely successful command transmission.

Hold-to-jog uses a per-press lease, renewed by the browser every 200 ms. The Pi checks for expiry every 100 ms and attempts release after 900 ms without renewal. Old leases cannot revive or stop newer movement. USB held presses are refreshed; Wi-Fi uses native move/stopMove. Transport failures and Pi termination remain material limitations; firmware-side Wi-Fi timeout protection is unverified.

Device-setting writes require an explicit Apply, idle confirmed hardware, no jog/manual correction or automation, and matching readback. They do not claim persistence across reboot. The interface exposes speed and backlash, not the entire upstream calibration, motor/limit, homing or saved-position feature set.
