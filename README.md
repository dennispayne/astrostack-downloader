<div align="center">
  <img src="assets/wgfetch.svg" width="220" alt="wgfetch verified-download logo">
  <h1>wgfetch</h1>
  <p><strong>Resolve fuzzy application names to current vendor installers, download them byte-for-byte unmodified, and lay the result out as a local winget source.</strong></p>
  <p>
    <a href="https://github.com/dennispayne/astrostack-downloader/actions/workflows/build.yml"><img alt="build" src="https://github.com/dennispayne/astrostack-downloader/actions/workflows/build.yml/badge.svg"></a>
    <a href="https://github.com/dennispayne/astrostack-downloader/actions/workflows/codeql.yml"><img alt="CodeQL" src="https://github.com/dennispayne/astrostack-downloader/actions/workflows/codeql.yml/badge.svg"></a>
    <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-6366f1?style=flat-square">
    <img alt="NativeAOT" src="https://img.shields.io/badge/NativeAOT-win--x64-2563eb?style=flat-square">
    <img alt="License MIT" src="https://img.shields.io/badge/license-MIT-0891b2?style=flat-square">
  </p>
</div>

`wgfetch` is a .NET 10 NativeAOT command-line tool aimed at astrophotography software — N.I.N.A.,
PHD2, ASCOM Platform, SharpCap, ASTAP, Stellarium, Sequence Generator Pro — most of which is absent
from `winget-pkgs` or present but badly stale. It is not a winget mirror: winget is one upstream
among several and frequently the worst one.

**`wgfetch` installs nothing and repacks nothing.** It downloads vendor installers exactly as
published, verifies every byte through a mechanical [verification gate](docs/VERIFICATION-AND-PRIVACY.md)
that no model output can bypass, records what it did, and writes a directory another tool can
consume passively.

The authoritative specification is [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md). Contributors and
agents must read [`AGENTS.md`](AGENTS.md) first.

## Quick start

```powershell
wgfetch prereqs install --include-llm            # once: fetch and verify the pinned ONNX models
wgfetch add nina phd2 astap ascom-platform       # build the target list; downloads nothing
wgfetch fetch --winget-repo .\source             # acquire and emit the source tree
wgfetch status --winget-repo .\source            # acquired / stale / blocked
```

## Learn more

| Topic | Doc |
| --- | --- |
| Full CLI walkthrough, output formats, `targets.yaml`, exit codes, licensing | [`docs/USAGE.md`](docs/USAGE.md) |
| The verification gate, network destinations, search providers, privacy | [`docs/VERIFICATION-AND-PRIVACY.md`](docs/VERIFICATION-AND-PRIVACY.md) |
| [astrostack-dsc](https://github.com/dennispayne/astrostack-dsc) field ownership and interop | [`docs/ASTROSTACK-DSC.md`](docs/ASTROSTACK-DSC.md) |
| Building, testing tiers, mutation testing, CI workflows | [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) |
| Full specification | [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md) |

## Contributing and support

| I want to | Read |
| --- | --- |
| Contribute a change | [`CONTRIBUTING.md`](CONTRIBUTING.md) |
| Know how we behave here | [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) |
| Ask a question or report a bug | [`SUPPORT.md`](SUPPORT.md) |
| Report a vulnerability privately | [`SECURITY.md`](SECURITY.md) |

`wgfetch` itself is MIT licensed (see [`LICENSE`](LICENSE)); fetched installers are not — see
[`docs/USAGE.md`](docs/USAGE.md#licensing--read-this-before-redistributing-anything) before
redistributing anything it produces.
