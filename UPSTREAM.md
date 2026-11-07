# Source baselines, attribution and licenses

| Component | Upstream | Base |
|---|---|---|
| MLAstro Windows plugin | https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA | da3817f26b15ebee6b772dab7ce48644e7eebf4f |
| PINS | https://github.com/nitr57/pins | 11320114a8b922dc88b46409b0067f5bc133655b |
| PINS TPPA fork | https://github.com/nitr57/nina.plugin.polaralignment | 12dfa7366e9fdf69e5115c666c38e17b6613f616 |
| ninaAPI fork | https://github.com/nitr57/ninaAPI | f3ab4f052f72df78d88f0d29645924129d083dcc |
| Touch-N-Stars | https://github.com/Touch-N-Stars/Touch-N-Stars | 73e378a66783d474c91cf971c4ab91d5e446b26e; tag 6.5.0-beta22-beta |

`src/Pins.MLAstro.Plugin/Upstream` contains copied/modified MLAstro source under MPL-2.0; original authors retain copyright. The backend and ninaAPI integration in this export use MPL-2.0, supplied as root LICENSE. Touch-N-Stars integration files and its patch use GPL-3.0, supplied in integration/touch-n-stars/LICENSE. Preserve these notices when separating contributions into upstream reviews.

Linux changes include removal of desktop/WMI/style dependencies, guarded Windows registry code, headless dispatch, stricter readiness, explicit connection lifecycle, serial discovery/identity checks and bounded cleanup. The headless composition root/control API and normal frontend plugin are integration additions. The upstream correction engine and TPPA bridge contract are reused; no new polar-alignment algorithm is claimed.

System.IO.Ports and Newtonsoft.Json are Microsoft/MIT and MIT dependencies respectively; dependency packages are restored by their projects and are not included as binaries here. Installed PINS/TPPA assemblies are required for host compatibility and tests but are not redistributed. The diagnostic projects are separate from the movement-capable plugin.

Export-only changes replace local library paths with PinsLibraries configuration and test runtime paths with PINS_HOME/TPPA_ASSEMBLY environment variables. No device profiles, account-specific configuration, build caches, private keys or Git history are included.

Version 0.2.0 also includes a portable `integration/ninaAPI/ninaAPI.Pins.csproj` for rebuilding the full relay against installed PINS assemblies, and an optional Android debug-flavor patch corresponding to the test APK. No signing keys or host assemblies are included. The self-contained worker's runtime notices are in `licenses/` and are included in binary integration bundles.
