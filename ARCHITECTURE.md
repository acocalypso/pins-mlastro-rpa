> Current beta: USB is in-process only. References below to the isolated USB worker describe earlier builds and are historical, not current release validation.

# Architecture and behavior

Version 0.2.0 exposes fixed relative steps and finite TPPA corrections. Continuous jogging and browser jog leases were removed; old routes return 410. The Linux worker rejects continuous-mode configuration and admits relative arrows only after relative mode is selected. No jog-specific one-second/900-ms motion deadlines remain.

The MEF root MLAstroPlugin owns connection, correction runner, profile settings, loopback API, diagnostics and one-session TPPA selection. Loading leaves hardware disconnected and automation off. Its assembly GUID differs from the Windows plugin. Competing controller owners must not drive the same hardware.

The inherited BridgeEngine plans angles; firmware converts them to steps and applies device backlash/limits. Defaults are maximum correction 120 arcminutes, fraction 0.75, settling 3 seconds and first-correction pause on. Axis mode, host altitude overshoot and software direction settings retain upstream defaults and are editable. Host overshoot and firmware backlash/overshoot are separate. ALIGN command shape, completion contract and 90-second completion timeout remain inherited.

Linux USB has a child process as its sole serial-port owner. Private JSON-line pipes carry session, generation, expiry and monotonic telemetry timestamps. Polling every 200 ms is independent of PINS camera processing. Identity, finite syntax, fresh readiness, completion and settling are checked. Uncertain commands are not replayed. A 15-second telemetry gap during a finite move faults and attempts Stop; parent scheduling gaps get 30 seconds. Parent death or pipe EOF attempts Stop/release. Write uncertainty retires the link. Reconnection waits for old work and verifies fresh READY.

USB/Wi-Fi allow 15 seconds of silence. A brief idle reference-sweep gap does not abort TPPA. A correction waits at dispatch up to 15 seconds for fresh telemetry before sending once. Stop invalidates queued admissions and cancels management work without queuing behind it. Fault cleanup uses ControllerFault and still runs if Stop fails.

CommunicationWatchdog reads firmware state or sends only its specific setting on short-lived WebSockets. It never claims control, moves, reboots or changes other device settings. The parent requires connection to that RPA over Wi-Fi; writes require fresh READY and idle state. Disabling requires explicit acknowledgement. ACK and a fresh read must agree. Unknown never means disabled. The persistent firmware setting is never changed automatically at load or connect.

Touch-N-Stars uses fixed ninaAPI routes to loopback port 1891; the local service rejects browser Origin headers. Existing surrounding application access controls apply. API v2 gates new management and diagnostics controls. The frontend displays authoritative transport/address, explains blocked arrows, verifies tolerance writes, and offers explicit reconnect. The Android test app embeds the same frontend.

A matched integration bundle contains plugin, worker, full compatible ninaAPI and normal web build, plus hashes, idle checks and backup/rollback. New relay routes are verified after install. Temporary-installation tests do not establish a clean third-party Pi installation. Prior user-reported sky alignment predates these release improvements.

Diagnostics use allowlisted enums, numeric versions/durations, Pi power flags and bounded timestamped events, excluding network identifiers, hardware coordinates, credentials and raw logs. Bounded local vcgencmd checks are read-only. Production builds map source paths to neutral paths to avoid exposing developer paths in compiled logging call sites.
