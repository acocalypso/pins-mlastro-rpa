# Redistributed runtime notices

The self-contained Linux ARM64 worker includes .NET runtime code and System.IO.Ports. This build uses runtime package 10.0.14 and System.IO.Ports 10.0.10.

- `dotnet/LICENSE.txt` and `dotnet/THIRD-PARTY-NOTICES.txt` are copied from the runtime package.
- `system-io-ports/THIRD-PARTY-NOTICES.txt` is copied from the System.IO.Ports package, whose declared license is MIT and whose package copyright is Microsoft Corporation.

These notices supplement the integration's MPL-2.0 and frontend GPL-3.0 licenses. They do not change those licenses. Host PINS/TPPA assemblies are not redistributed. Rebuilders using different dependency versions should supply the notices for their actual artifacts.
