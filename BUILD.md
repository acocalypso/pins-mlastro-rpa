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

Build the Linux ARM64 USB worker as a self-contained single executable:

```sh
dotnet publish src/Pins.MLAstro.UsbWorker -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/usb-worker
```

Install `Pins.MLAstro.UsbWorker` in `usb-worker/` beside the plugin DLL and make it executable. Keep only the executable there, not a tree of framework DLLs that a plugin loader could scan. `MLASTRO_USB_WORKER` overrides its path for testing or packaging. A missing worker is an explicit connection failure; the Linux plugin does not silently fall back to USB I/O inside PINS. The plugin preserves the device communication-watchdog setting. Its advanced control reads and verifies explicit changes over an active Wi-Fi connection; it never silently disables it.

The worker integration checks use private Linux pseudoterminals and never open physical equipment:

```sh
python3 tests/Pins.MLAstro.UsbWorkerChecks/worker_checks.py -- /absolute/path/to/Pins.MLAstro.UsbWorker
dotnet publish tests/Pins.MLAstro.UsbWorkerChecks/ClientHarness -c Release -r linux-arm64 --self-contained true -o artifacts/client-harness
python3 tests/Pins.MLAstro.UsbWorkerChecks/client_checks.py /absolute/path/to/ClientHarness /absolute/path/to/Pins.MLAstro.UsbWorker
```

The client checks pause only their disposable test process with SIGSTOP. Do not substitute the production PINS PID. Set `MLASTRO_USB_WORKER` when running full plugin checks if the worker is not installed beside their plugin DLL.

The standalone adversarial safety checks need only the .NET SDK and PowerShell. They extract the current plugin methods and run with fake collaborators, plus the actual bounded writer:

```powershell
./tests/Pins.MLAstro.SafetyChecks/run.ps1 -Dotnet dotnet
```

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

Base: `nitr57/ninaAPI`, commit `f3ab4f052f72df78d88f0d29645924129d083dcc`. From the upstream repository root, apply both patches and copy the portable build project beside the original:

```sh
git apply /path/to/export/integration/ninaAPI/mlastro-relay.patch
git apply /path/to/export/integration/ninaAPI/focuser-compatibility.patch
cp /path/to/export/integration/ninaAPI/ninaAPI.Pins.csproj ninaAPI/ninaAPI.Pins.csproj
dotnet build ninaAPI/ninaAPI.Pins.csproj -c Release -p:PinsLibraries=/absolute/path/to/pins
```

The second patch is required by the supplied PINS build project. The project produces `ninaAPI/bin/Release/net10.0/ninaAPI.dll`; it derives from the customized Linux .NET 10 project and maps source paths to a neutral prefix. It requires the target host's compatible PINS reference assemblies, which are not redistributed. Those dependencies and package restore are still required; neither the portable project nor `Pins.MLAstro.RelayCompile` is a standalone replacement for PINS. The portable project fixes the excluded System.Drawing.Common dependency to the resolved 10.0.14 version; package availability and transitive dependency resolution can still affect reproduction. Check that the rebuilt DLL matches the target installation before use.

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

The frontend's API address comes from the selected rig. Enable MLAstro RPA in PINS plugin management and open its navigation entry. Configure camera/mount and a positive TPPA tolerance before supervised alignment. The current native app can render these screens through a frontend bundle; the optional debug flavor is supplied in `integration/touch-n-stars/android-test-flavor.patch`. Signing keys and local SDK configuration are excluded.

Distribute frontend updates through the project's established release mechanism. Partial frontend copies may leave mismatched assets; preserve an installation backup and replace the entry document only after its referenced assets are present.

## Building an integration bundle

After building and testing a matched plugin, worker, full ninaAPI DLL and web build, assemble from explicit artifact paths. The output directory must not exist. The ninaAPI input must be the complete compatible DLL with relay, not the relay compile test. Inspect the output and its manifest before distribution; this process does not prove the supplied binaries correspond to their claimed versions. Distribute required corresponding source and license notices with the release. Do not include host DLLs, private build projects, signing material or keys.

```sh
python3 scripts/build_bundle.py --version 0.2.0-experimental --plugin /path/to/NINA.Plugins.MLAstroRPA.Linux.dll --worker /path/to/Pins.MLAstro.UsbWorker --nina-api /path/to/ninaAPI.dll --web-dir /path/to/built-web --output /path/to/new-bundle
```

Pass `--source-revision` with the 40-hex public corresponding-source commit when assembling a release. The bundle includes the guide, build instructions, upstream attribution, source notice and license texts. The manifest lists the source repository and revision, SHA-256 hashes, components and the intended compatibility baseline; it does not verify the binaries or establish that these artifacts were tested. Optional repeated `--component-version NAME=VERSION` arguments record unverified supplier claims. Hashes are not signatures. See USER-GUIDE.md for check, installation and recovery. Native Android APKs are separate and are not bundled.


For artifacts intended for sharing, build with .NET `PathMap` mapping the absolute source workspace to a neutral prefix, for example `-p:PathMap=/absolute/source/workspace=/_` (use the actual platform path and quote the argument where needed). This also applies to the separately built full ninaAPI project. Inspect the resulting binaries and frontend assets for private paths and secrets before release; source mapping and simple string searches do not prove that they contain no private information. Do not include PDBs or other private build outputs in the integration bundle.

## Optional Android test flavor

The distributed experimental APK uses a separate application ID and label so it can coexist with the standard app. After applying the frontend integration to the pinned checkout, apply `integration/touch-n-stars/android-test-flavor.patch`. It adds only the debug flavor and an application-ID-relative FileProvider authority. Use the upstream Android SDK/JDK requirements, build the native web assets, sync Capacitor Android, then run `assembleDebug`. Use your own automatically generated debug signing key; no keys are supplied. The generated APK is a separate artifact from the Pi integration bundle.

The bundle includes build/source/license notices and a source revision. Reproduce its code from that source revision and the pinned upstream checkouts, not from unrelated latest branches. Version declarations identify intended compatibility and are not a claim that an arbitrary collection of files passed hardware tests.
