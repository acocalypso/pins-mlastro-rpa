# Build and integrate

## Prerequisites

- .NET SDK 10; Linux ARM64 for running the complete Pi simulation checks.
- Installed PINS assemblies matching your target installation, plus its TPPA plugin DLL. These are deliberately not redistributed here.
- For frontend work: the Node/npm versions and lockfile required by the pinned Touch-N-Stars checkout.

Tested target: PINS application 3.3.0.1058 nightly, runtime .NET 10; loaded TPPA 2.2.8.0; ninaAPI 2.2.15.2; RPA firmware 1.13.0. Compatibility with other versions must be checked.

## Backend

Set `PinsLibraries` to a directory containing the deployed PINS application's DLLs. The default is `lib/pins` beneath this repository; it is ignored by Git. Do not commit those DLLs.

```sh
dotnet build src/Pins.MLAstro.Plugin -c Release -p:PinsLibraries=/absolute/path/to/pins
dotnet run --project tests/Pins.MLAstro.Checks
dotnet build tests/Pins.MLAstro.RelayCompile
```

The plugin references the host's assemblies without copying them. `System.IO.Ports` must be supplied by a compatible PINS runtime. Building does not install, connect to or move equipment.

For the isolated full plugin checks, publish for Linux ARM64, copy the published test directory to a Pi, then run there:

```sh
dotnet publish tests/Pins.MLAstro.PluginChecks -c Release -r linux-arm64 --self-contained true -o artifacts/plugin-checks -p:PinsLibraries=/absolute/path/to/pins
export PINS_HOME=/absolute/path/to/installed/pins
export TPPA_ASSEMBLY=/absolute/path/to/NINA.Plugins.PolarAlignment.dll
./artifacts/plugin-checks/Pins.MLAstro.PluginChecks
```

These checks use an in-memory profile, loopback firmware server and private kernel pseudoterminals. They read USB metadata but do not open real USB equipment. Ports 1892/1893 must be free. Do not launch multiple copies concurrently. `PINS_HOME` is a runtime dependency location; `PinsLibraries` is the build reference location.

For installation, stop PINS, back up its existing plugin files, and put the compiled `NINA.Plugins.MLAstroRPA.Linux.dll` in a new `MLAstroRPA.Linux` directory under the installation's versioned NINA plugin root. Restart PINS and verify disconnected/automation-disabled status. Resolve the host and plugin directories from your installation; they are not hardcoded to a particular user account here. Debian packaging is not included.

## ninaAPI

Base: `nitr57/ninaAPI`, commit `f3ab4f052f72df78d88f0d29645924129d083dcc`.

```sh
git apply /path/to/export/integration/ninaAPI/mlastro-relay.patch
```

Alternatively copy `integration/ninaAPI/MLAstro.cs` into `ninaAPI/WebService/V2/`. Build using that project's PINS-compatible configuration and dependencies. The small `focuser-compatibility.patch` is an optional existing build compatibility fix for the deployed host; review it separately. This export does not include the locally customized full ninaAPI build project or proprietary/host DLLs. `Pins.MLAstro.RelayCompile` is a route/source compile check, not a replacement ninaAPI application.

Back up the installed ninaAPI DLL before replacing it. The relay calls loopback port 1891, exposes only fixed MLAstro routes, and needs no reference to the MLAstro assembly.

## Touch-N-Stars

Base: `Touch-N-Stars/Touch-N-Stars`, tag `6.5.0-beta22-beta`, commit `73e378a66783d474c91cf971c4ab91d5e446b26e`.

```sh
git checkout 73e378a66783d474c91cf971c4ab91d5e446b26e
git apply /path/to/export/integration/touch-n-stars/mlastro-rpa.patch
npm ci
node scripts/generate-plugin-registry.js
node --import ./scripts/test-loader.mjs --test 'src/plugins/mlastro-rpa/__tests__/*.test.js'
npx eslint src/plugins/mlastro-rpa
npm run build
```

Use either the patch **or** copy the `plugin` directory to `src/plugins/mlastro-rpa` and apply the websocket send-result change; do not apply both. The patch includes the new plugin and changes `websocketTppa.sendMessage` to return the send result. The shared `src/utils/tppaStart.js` already exists at the pinned base.

The frontend's API address comes from the selected rig. Enable MLAstro RPA in PINS plugin management and open its navigation entry. Configure camera/mount and a positive TPPA tolerance before supervised alignment. The current native app can render these screens through a frontend bundle; local Android package-name/signing modifications used during development are deliberately excluded.

Distribute frontend updates through the project's established release mechanism. Partial frontend copies may leave mismatched assets; preserve an installation backup and replace the entry document only after its referenced assets are present.
