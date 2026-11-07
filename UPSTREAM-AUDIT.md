# MLAstro Windows/PINS settings and behavior audit

Audited 2026-11-06 against official Windows commit `da3817f26b15ebee6b772dab7ce48644e7eebf4f` (3.3.1.0). The remote HEAD was checked and still matched this commit. The PINS changes were reviewed independently by a read-only QA agent as well as the implementation agent. This is a source/settings comparison, not a claim of complete hardware equivalence.

## Follow-up in 0.2.0

The 2026-11-07 implementation removes continuous jogging and its short deadlines, exposes the previously missing correction controls, classifies fault-driven shutdown as ControllerFault, and waits briefly for fresh telemetry at correction dispatch. It adds verified Wi-Fi watchdog control, explicit reconnect, allowlisted diagnostics and experimental matched bundle tooling. The dated findings below describe the earlier audited snapshot; consult API.md, ARCHITECTURE.md and USER-GUIDE.md for current behavior. Firmware defaults are not changed automatically.

## Findings and corrections

1. **Correction cap drift was introduced by this integration.** Windows defaults to 120 arcminutes, permits values of at least 1 arcminute, and imposes no upper bound in its setter. The PINS API/UI had a 10-arcminute ceiling while the inherited settings default was 120, so Save failed before writing TPPA tolerance. A later repair set 0.5, but the unchanged planner floors the cap at 1, producing repeated 1-arcminute moves. The correction restores default 120, range 1 and above, and removes the artificial upper limit. Invalid legacy values below 1 fall back to 120; valid existing choices are preserved.
2. **Two other range restrictions were unnecessary.** Correction fraction now accepts 0.05–1 and settling accepts 0–60 seconds, matching Windows. Their defaults remain 0.75 and 3 seconds. Initial frontend placeholders now agree with backend defaults.
3. **Several upstream correction options remain unavailable for editing in the PINS UI.** Axis mode, host overshoot, automatic direction change and manual software reversals still run with the persisted profile values or upstream defaults. They are now returned by the read-only settings endpoint so they can be audited. Full UI parity remains work; these options have not been silently disabled or rewritten.
4. **The one-second active-session telemetry abort was our addition.** It was not in the Windows plugin and was too broad. A TPPA session includes mount slews/captures while the RPA is idle. That condition no longer aborts the session. A separate worker mistake applied jog timing to finite corrections; finite moves now get a 15-second telemetry grace and 30-second parent scheduling allowance.
5. **READY did not explain all disabled arrows.** The frontend unnecessarily required a successful settings read and blocked manual arrows during ordinary TPPA. Both dependencies were removed. Actual RPA automation/movement still blocks competing commands; remaining blocks are explained beside the arrows.
6. **Fault labeling still needs cleanup.** The shared general Stop path can report `UserStop` when entered after a transport fault. The recorded motion fault remains authoritative. This is a reporting defect, not evidence that the operator pressed Stop.

## Correction settings

Defaults below describe a new profile, unless stated otherwise. Persisted choices take precedence. The settings API returns values from the profile settings getters without connection credentials; the planner separately clamps correction fraction and overshoot magnitudes.

| Setting | Official Windows | PINS after this correction | Assessment |
|---|---|---|---|
| Maximum correction per move | 120 arcmin; minimum 1; no setter upper bound | Same | Restored parity; cap is per move, not the maximum initial error |
| Correction fraction | 0.75; allowed 0.05–1 | Same | Restored range parity |
| Post-correction settling | 3 s; allowed 0–60 | Same | Restored range parity |
| Pause before first correction | On | On | Same; exposed |
| Axis selection | Both; optional larger-error-only mode | Same inherited default; not editable in UI | Missing control |
| Host altitude overshoot | Enabled | Same inherited default; not editable in UI | Consequential missing control |
| Host overshoot direction | Down selected, Up off | Same inherited defaults | Missing control |
| Host overshoot amount | Down 30 arcmin; Up 5 arcmin | Same inherited defaults | Missing control |
| Automatic direction change | Off | Same inherited default; not editable in UI | Missing control |
| Software reverse AZ / ALT | Off / Off | Same inherited defaults; not editable in UI | Missing controls |
| TPPA finish tolerance | Owned by TPPA | Saved to TPPA and read back before claiming success | Separate from the move cap; zero is displayed honestly and cannot start RPA automation |

For ordinary corrections, the inherited planner requests `min(abs(error) × fraction, cap)`. With fraction 0.75 and cap 120, an error of 180 arcminutes requests at most 120 arcminutes. Smaller errors naturally produce smaller requests.

Host altitude overshoot is different: on its selected direction it requests the full altitude error plus overshoot, limited by the same cap. It is one command; a later measurement corrects the remainder. This upstream policy can therefore bypass the normal 0.75 fraction for that altitude move. It must not be confused with firmware backlash or the firmware's own two-leg altitude overshoot routine.

The `BridgeEngine` planning code, ALIGN command shape, completion-token contract and 90-second correction-completion timeout are unchanged from the Windows baseline. `HardwareAligner` does differ in freshness/debounce checks, awaited motion dispatch and explicit Stop-failure reporting. Normal direction checking, wrong-direction handling, holding an axis already inside tolerance and error-growth detection remain inherited behavior. There is no new hardcoded backlash allowance added to angular moves.

## Hardware controls and UI coverage

| Control | Windows | PINS |
|---|---|---|
| Serial / Wi-Fi | Supported | Supported; Linux-specific USB identification and stable paths added |
| Relative arrow steps | Supported | Supported, including 5 arcsec; firmware converts angle to motor steps |
| Hold-to-jog | Supported movement controls | Added browser lease/release handling; optional for TPPA, which uses finite moves |
| Speed, anti-backlash enabled, AZ/ALT backlash steps | Firmware settings | Read/apply/readback exposed; no startup replacement with host defaults |
| Firmware overshoot, hardware reversal, travel limits, administration | Richer official hardware UI | Not fully exposed; configure in the official device UI |

The audited installation reported firmware anti-backlash enabled with AZ 77917 steps and ALT 1000 steps. Those are device configuration, not PINS source defaults. Mechanical take-up may occur before a commanded angle produces visible mount movement; this audit does not validate the calibration values.

## Connection and lifecycle differences retained deliberately

| Behavior | Windows baseline | PINS |
|---|---|---|
| Serial ownership | In-process SerialPort | Dedicated Linux child process; same-process path retained for non-Linux builds |
| USB polling | Configurable in-process timer | Worker independently polls every 200 ms |
| Link silence | 5-second link watchdog; serial polling also counts failed replies | 15-second silence allowance for USB and Wi-Fi |
| Freshness before a new move | Less strict inherited readiness checks | Fresh READY telemetry within 1 second; old data cannot authorize a new move |
| Short gap during idle TPPA sweep/capture | No added one-second abort | Session is not aborted solely because it is active; fresh telemetry is required at correction dispatch |
| Finite move missing telemetry | No equivalent added one-second test | 15 seconds; completion timeout remains 90 seconds |
| Parent scheduling delay | No independent parent process | Finite move/idle: 30 seconds; continuous jog: 900 ms |
| Continuous jog telemetry | No equivalent worker rule | 1-second telemetry deadline; separate browser release lease |
| Native serial write budget | SerialPort WriteTimeout 1000 ms | Worker native write 250 ms, bounded async wait 300 ms; uncertainty faults the session |
| Reconnect/retry | Official controller flow | Explicit verified reconnect; no replay of uncertain movements |
| Stop failures | Upstream cleanup can depend on STOP success | Independent release, general STOP and cleanup attempts; failed confirmation remains visible |
| Automation at load | Broker setting defaults on | Defaults off and is reset off at plugin load |
| TPPA controller selection | Official UI selection | Start selects MLAstro for one run, then restores normal TPPA behavior |
| Unconfirmed start | Official flow | Pending selection/arming expires after 20 seconds |

The worker keeps USB polling independent of PINS camera processing. It also adds process/IPC code, resource use and failure modes. It has not been proven necessary on a correctly powered installation. Real network/device failures remain possible; longer timeouts cannot make a failed physical link healthy. A remaining race is that stale telemetry at correction dispatch currently makes `AlignAsync` return false and can fault the run, rather than waiting briefly for a fresh sample. That is an additional behavior difference to review, even though a successful run has now been reported.

The firmware communication watchdog is independent of these host checks. The plugin/worker do not toggle it automatically. It was disabled persistently on the audited device at the user's request; automatic firmware ESTOP on missing PC heartbeats is therefore unavailable on that device. Manual Stop and separately configured firmware travel limits were not changed. This is an installation choice, not a new default applied to everyone.

## Validation and remaining work

- The user reported successful automated polar alignment under the stars on 2026-11-06, after the finite-move timing corrections. This is user-observed acceptance of that run, not independent verification of final angular accuracy or a long-term reliability claim.
- Focused Linux checks cover default 120, legacy invalid-cap fallback, accepting a 240-arcminute cap, correction fraction 0.05, settling 0, and rejecting invalid/non-finite values. Backend compilation, frontend lint and frontend build pass.
- Earlier targeted PTY tests passed finite-correction and relative-step telemetry gaps, no command replay, and short continuous-jog liveness/telemetry loss. No movement is needed to test settings persistence.
- Full editing parity for the missing upstream settings, clearer fault reason labels and packaging/release hardening remain outstanding. The short USB write budget is another remaining sensitivity to investigate if write failures recur; it has not been silently altered in this correction-default change.
- A Raspberry Pi USB power change, network packet loss, firmware watchdog configuration and worker timing fixes all changed during investigation. Their individual contributions have not been isolated by a controlled comparison.

## Primary sources

- [Official settings defaults](https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA/blob/da3817f26b15ebee6b772dab7ce48644e7eebf4f/MLAstroRPA-navigation/Settings/PluginSettings.cs)
- [Official settings validation and UI controller](https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA/blob/da3817f26b15ebee6b772dab7ce48644e7eebf4f/MLAstroRPA-navigation/Plugin/MLAstroController.cs)
- [Official correction planner](https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA/blob/da3817f26b15ebee6b772dab7ce48644e7eebf4f/MLAstroRPA-broker/Broker/BridgeEngine.cs)
- [Official serial service](https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA/blob/da3817f26b15ebee6b772dab7ce48644e7eebf4f/MLAstroRPA-implement/Services/SerialConnectionService.cs)
- PINS implementation: [plugin/API](src/Pins.MLAstro.Plugin/MLAstroPlugin.cs), [settings](src/Pins.MLAstro.Plugin/Upstream/PluginSettings.cs), [serial service](src/Pins.MLAstro.Plugin/Upstream/SerialConnectionService.cs), [worker](src/Pins.MLAstro.UsbWorker/Program.cs), [frontend](integration/touch-n-stars/plugin/views/MlastroView.vue).
