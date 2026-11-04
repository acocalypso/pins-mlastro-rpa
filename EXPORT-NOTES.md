# Export notes

This repository was initialized from the reviewed clean source export. It is not an upstream fork; no private development history, credentials or installed device profiles were imported. Upstream pull requests and release packages have not been created.

The source includes the latest prepared backend, relay and frontend changes. The test Android app used the latest frontend; the installed Pi browser bundle may lag the source because interrupted network transfers prevented its final update. The source should be built and reviewed as a whole rather than inferred from the development installation.

Excluded: the abandoned standalone panel, development APK/app-signing changes, downloaded repositories, installed/vendor DLLs, generated registries/bundles, logs, personal validation history, private installation addresses, local account paths, screenshots and credentials.

Patch and source copies are alternative integration methods. Use the complete Touch-N-Stars patch once, or copy the plugin and apply the existing-file change manually. See BUILD.md.

MANIFEST.sha256 records every exported file except itself and EXPORT-CHECKS.md. It can be checked with `sha256sum -c MANIFEST.sha256` from this directory. The original source-export ZIP is a pre-publication snapshot. Temporary compile outputs are kept outside this repository.
