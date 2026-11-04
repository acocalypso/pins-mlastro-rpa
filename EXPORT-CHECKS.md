# Export checks

Checked 2026-11-04 before packaging:

- Source copied by an allowlist; no generated build output, Git history, credentials, installed device profiles, logs or binaries included.
- All exported text decoded as UTF-8; private installation markers and common private-key/token patterns checked with no matches. This is a targeted inspection, not a guarantee that a future modification is safe to publish.
- Exported plugin compiled successfully against matching host assemblies supplied outside the export.
- Exported plugin-check project compiled; its Linux platform analyzer warnings are expected when compiling on Windows. The full hardware-isolated ARM64 suite passed during development; it was not rerun for the export-only environment-path adaptation.
- Exported diagnostic CLI compiled; eight protocol checks passed. Exported ninaAPI relay compile check passed.
- All three patches applied to clean pinned baseline files. Plugin files reconstructed by the Touch-N-Stars patch match the separate exported source copies.
- Nine frontend tests and plugin lint passed in the pinned Touch-N-Stars checkout containing the exported plugin code.
- Archive manifest and archive entries checked. No actual equipment was contacted or moved while preparing this export.

Complete automated sky alignment remains unvalidated. Publishing this reviewed snapshot does not imply upstream acceptance.

## Pre-publication privacy review

An independent QA agent reviewed the 60-file source snapshot and Git metadata. No apparent private credentials, installation-specific addresses, personal paths, logs, profiles or binaries were found. An additional scan of all reachable Git blobs found no credential-pattern or high-entropy candidates and verified the manifest against committed content. Commit metadata uses the intended public GitHub account and its noreply address. Public upstream attribution, synthetic test addresses and documented device defaults remain intentionally present. These checks reduce disclosure risk; they are not a guarantee against unknown secret formats or future changes.
