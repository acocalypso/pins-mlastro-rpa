# MLAstro One RPA integration for PINS / Touch-N-Stars

Experimental integration 0.3.0-beta.1 for clean-install testing, 2026-11-11.

See [RELEASE-NOTES.md](RELEASE-NOTES.md) and GitHub Releases for the matched ARM64 beta bundle.

Repository: https://github.com/ydutertre/pins-mlastro-rpa
 This is an experimental Linux port and UI integration, not an official PINS, Touch-N-Stars or MLAstro release.

Connect an MLAstro One RPA to a Raspberry Pi over USB or Wi-Fi, use manual adjustment controls, and start a TPPA session with automatic polar corrections. A completed alignment under the stars was reported on 2026-11-06; the newer 0.2.0 management and packaging changes still require hardware acceptance.

## Contents

- `src/Pins.MLAstro.Plugin`: headless PINS plugin, firmware transports and TPPA broker integration.
- `src/Pins.MLAstro.Connection` and `src/Pins.MLAstro.Probe`: separate read-only connection diagnostics.
- `tests`: protocol, simulated firmware, serial pseudoterminal and relay compile checks.
- `integration/ninaAPI`: controller relay and reviewable patches.
- `integration/touch-n-stars`: normal frontend plugin and a complete patch against the pinned upstream version.
- [BUILD.md](BUILD.md): build, integration and installation instructions.
- [ARCHITECTURE.md](ARCHITECTURE.md), [API.md](API.md), [VALIDATION.md](VALIDATION.md), [UPSTREAM.md](UPSTREAM.md): behavior, limits and source baselines.

## Implemented behavior

- Wi-Fi or automatic USB identification; compatible USB ports are tried until firmware identity and fresh telemetry confirm an RPA. No motor movement is sent during identification.
- Manual arrow pad with bounded 5/10/30/60/120 arcsecond steps, speed levels 1–5, explicit firmware-backed backlash editing and Stop.
- Continuous jogging is removed; old requests are rejected. Limits and backlash compensation remain firmware responsibilities.
- Verified communication-watchdog control over Wi-Fi, explained faults/reconnect, allowlisted diagnostics, advanced correction settings, and an experimental matched bundle installer with rollback. See [USER-GUIDE.md](USER-GUIDE.md).
- One-button TPPA start, tolerance/correction settings, pause/resume and stop. RPA correction selection is scoped to one session; completion/cancellation resets TPPA to ordinary operation without automatic RPA corrections. Normal TPPA may still slew the mount for its reference measurements.
- Plugin loading leaves hardware disconnected and automation disabled. No separate Android native feature is required: the interface is a normal Touch-N-Stars frontend plugin.

## Status and limitations

Real USB firmware/telemetry/settings communication is confirmed. The user reported successful small manual moves and jog/release/Stop testing, and a completed automated alignment under the stars on 2026-11-06. Independent final-accuracy verification, repeated-run reliability, exhaustive USB movement tests and failure recovery remain pending. See [VALIDATION.md](VALIDATION.md) and the [Windows/PINS settings audit](UPSTREAM-AUDIT.md).

Controls send real movement commands when connected. A host cannot guarantee delivery of Stop after power or connection loss. Firmware watchdog state is preserved unless the user explicitly changes it; disabling it removes its heartbeat-triggered stop. Do not use this snapshot as an unattended alignment release.

This Git repository contains source and documentation only; compiled beta bundles are distributed separately through GitHub Releases. It contains no installed PINS assemblies, SSH keys, logs, device profiles, APKs, signing files or compiled binaries. 

## Review / upstreaming

The suggested review split is the PINS backend, ninaAPI relay and Touch-N-Stars plugin. TPPA bridge code is consumed as supplied by the PINS TPPA fork; this export does not modify TPPA. Licensing differs by component: backend/relay use MPL-2.0; Touch-N-Stars integration uses GPL-3.0 as its upstream project. See [UPSTREAM.md](UPSTREAM.md).
