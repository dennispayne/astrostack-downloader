# wgfetch

**Resolve fuzzy application names to current vendor installers, download them byte-for-byte
unmodified, and lay the result out as a local winget source.**

`wgfetch` is a .NET 10 NativeAOT command-line tool aimed at astrophotography software — N.I.N.A.,
PHD2, ASCOM Platform, SharpCap, ASTAP, Stellarium, Sequence Generator Pro — most of which is absent
from `winget-pkgs` or present but badly stale. It is not a winget mirror: winget is one upstream
among several and frequently the worst one.

**`wgfetch` installs nothing and repacks nothing.** It downloads vendor installers exactly as
published, records what it did, and writes a directory another tool can consume passively.

The authoritative specification is [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md). Contributors and
agents must read [`AGENTS.md`](AGENTS.md) first.

---

## First use

```powershell
wgfetch prereqs install --include-llm            # once: fetch and verify the pinned ONNX models
wgfetch add nina phd2 astap ascom-platform       # build the target list; downloads nothing
wgfetch fetch --winget-repo .\source --dry-run   # resolve + verify only; moves no bytes
wgfetch fetch --winget-repo .\source             # acquire and emit the source tree
wgfetch status --winget-repo .\source            # acquired / stale / blocked
wgfetch export --format astrostack-dsc --out .\patches
```

Steady state is `wgfetch fetch --winget-repo .\source`, which acquires new targets and refreshes
stale ones. A run a month later hits the cached auto-generated recipes and skips the language model
entirely for unchanged vendors.

Defaults (Windows):

| Purpose | Path |
| --- | --- |
| Models | `%LOCALAPPDATA%\wgfetch\models\{e5-small-v2,phi-3.5-mini-instruct-onnx}\` |
| Output source tree | `%LOCALAPPDATA%\wgfetch\source\` |
| Cache | `%LOCALAPPDATA%\wgfetch\cache\` |
| Config | `%LOCALAPPDATA%\wgfetch\config.json` |

Precedence is **CLI flag > environment variable > config file > built-in default**.

---

## The verification gate

A local language model participates in URL discovery, so it is treated as **unreliable and
adversarial by default**. It may *propose* candidate URLs and rank page content; it may never
*authorize* a download.

Every candidate — from a recipe, from GitHub, from winget, or from the model — passes the same
purely mechanical gate before a single byte is retained:

1. Host matches an **allowlisted vendor domain** for the resolved package.
2. **HTTPS only**; redirects are followed only within the allowlist, and a redirect off-allowlist is
   a hard failure.
3. A `HEAD` or ranged `GET` succeeds.
4. `Content-Type` indicates a binary payload, not `text/html`.
5. **Magic bytes** of the leading chunk match a known installer format (PE/MZ, MSI/OLE compound,
   ZIP/MSIX, CAB, 7z, Inno, NSIS).
6. Content length is plausible for an installer and inconsistent with an error or interstitial page.

Anything failing is rejected and logged with the exact check that rejected it. **A login page, error
page or interstitial is never written to disk and never hashed as an installer.** No model output
can move a URL past this gate.

Downloads are resumable but never spliced: a resume requires an `ETag`/`Last-Modified` validator and
a `206` response, a `200` answer to a ranged request restarts from zero, and the final path only ever
appears after full-length and SHA256 verification via an atomic rename.

---

## The three consumption paths

**winget cannot `source add` a bare folder of manifests.** A directory of YAML files is not a source.
`wgfetch` therefore emits all three shapes and lets you choose:

| Path | What `wgfetch` writes | What you need to consume it |
| --- | --- | --- |
| **Pre-indexed source** | `index.db` — SQLite in winget's pre-indexed schema, alongside `manifests/` and `installers/` | Package `index.db` into a `Microsoft.PreIndexed.Package` MSIX-signed bundle, or point a consumer that reads the schema directly at it. `winget source add` requires the signed package. |
| **REST source** | `rest/` — static JSON shaped to the winget REST source API | Serve the directory over HTTP(S) and `winget source add --type Microsoft.Rest --arg <url>` |
| **Direct manifests** | `manifests/<letter>/<Publisher>/<Package>/<Version>/*.yaml`, with `InstallerUrl` rewritten to the local path and `InstallerSha256` set to the locally computed hash | `winget install --manifest <dir>` per package, or validate with `winget validate` |

Plus `provenance.json`, which records for every artifact the friendly query, resolved ID, discovery
stage, every candidate URL with its verification result, the accepted URL, resolved version,
conflicting source versions, computed SHA256, any upstream-published hash, timestamp, resolution
tier, confidence and AI mode.

Rewriting a manifest is not repacking — the installer bytes are untouched.

---

## The wishlist lives in the repo

A winget manifest cannot be a stub, so unacquired targets never appear in `manifests/`, `index.db` or
`rest/`. Instead the output tree carries its own declared intent in a root-level `targets.yaml`,
which winget ignores:

```yaml
version: 1
targets:
  - name: nina
    id: AstroStack.NINA
    componentId: nina
    state: acquired            # listed | resolved | acquired | stale | blocked
    acquiredVersion: 3.2.0.9001
    availableVersion: 3.2.0.9001
    allowlist: [nighttime-imaging.eu, github.com]
```

The file is human-editable and round-trippable: unknown keys survive a rewrite untouched. A
populated-but-unacquired repo is valid and expected — it is never treated as corruption.

---

## P0 / P1 boundary

**P0 (implemented):** everything publicly downloadable without authentication, purchase or licence
acceptance, plus *detection* of everything that is not.

When a package cannot be fetched anonymously — Microsoft Store or Entra-licensed packages, paid or
account-gated vendors, an HTTP 401/403, or a response whose content-type, size or magic bytes
indicate a login or interstitial page — `wgfetch` skips it, reports
`requires authentication (P1, unsupported)`, exits with a dedicated code and **writes nothing**.

**P1 (seams only, deliberately not implemented):** authenticated or licensed acquisition, vendor
logins, purchased-product portals and Store license files. There is a pluggable auth-provider seam
and a per-package `requiresAuth` flag, but no credential storage, no interactive login and no Entra
integration. **PixInsight and SharpCap Pro** ship as recognised-but-blocked entries.

---

## Search providers

Discovery needs a way to find a vendor's download page. There is **no hardcoded search engine**;
the provider is pluggable and privacy-respecting by default.

```powershell
wgfetch fetch nina --search-provider mojeek
wgfetch fetch nina --search-provider searxng --search-endpoint https://searx.example
wgfetch fetch nina --search-provider none      # recipes, GitHub and winget only
```

| Provider | Key required | Notes |
| --- | --- | --- |
| `duckduckgo` | no | Default. HTML endpoint, no tracking, no account. |
| `mojeek`, `startpage` | no | Independent / privacy-respecting alternatives. |
| `searxng` | optional | Self-hosted; set `--search-endpoint`. |
| `brave`, `google`, `bing` | yes | API keys via `--search-key` or `WGFETCH_SEARCH_KEY`; opt-in only. |
| `none` | — | Disables web search entirely; discovery degrades to recipes, GitHub releases and winget, and reports clearly why an app was unresolvable. |

---

## Privacy: every network destination

`wgfetch` has **no telemetry, no analytics, no crash reporting, no update checks and no phone-home
of any kind.** The complete list of hosts it can contact, and why:

| Destination | When | How to avoid it |
| --- | --- | --- |
| `huggingface.co`, and the CDN host it redirects large model files to (currently `us.aws.cdn.hf.co`) | `wgfetch prereqs install`, or `--download-prereqs` | Fetch the pinned models manually and point `--models-root` at them |
| The configured search provider | Only during LLM-assisted discovery | `--search-provider none` |
| `api.github.com` | Resolving GitHub-hosted releases | Not contacted when no target resolves via GitHub |
| Allowlisted vendor download hosts | Verification and download | Unavoidable; this is the product |
| Your own `--ai-endpoint` | Only under `--ai-mode remote` or `auto` with a remote configured | Default `--ai-mode local` never leaves the machine |

Inference is **offline-local by default** and runs on-box against the pinned ONNX models. The
optional OpenAI-compatible remote endpoint is opt-in only and never a silent fallback.

API keys and tokens are redacted from logs at every verbosity, from `provenance.json`, from `--json`
output and from `wgfetch diagnostics` bundles. Nothing is ever written to disk in cleartext by
`wgfetch` that it did not receive from you.

---

## astrostack-dsc interop

The primary consumer is [astrostack-dsc](https://github.com/dennispayne/astrostack-dsc).

```powershell
wgfetch import --format astrostack-dsc <dsc-repo>\manifest\components   # seed targets.yaml
wgfetch export --format astrostack-dsc --out .\patches                  # emit machine-owned fields
```

**Field ownership is the governing rule.** `wgfetch` owns exactly `downloadUrl`, `downloadFileName`,
`sha256`, `verified` and `availableVersion`. Every other field — `expectedVersion`, `checkPath`,
`versionSource`, `versionStrategy`, `versionRegex`, `silentInstallArgs`, `archiveContainsInstaller`,
`module`, `kind`, `notes`, `installNotes` — is human-owned and is **never** written.

In particular `expectedVersion` is **never auto-bumped**: it is a deliberate human pin that gates DSC
remediation. Newer builds are reported in `availableVersion` for a person to promote. Silently
advancing a pin would turn an audit tool into an uncontrolled auto-updater.

Exports are **per-component partial patches**, never whole-file replacements, so hand-written `notes`
survive. The export also contains a `path-mapping.json` so the DSC side resolves artifacts by ID
rather than guessing filenames — `installers/` keeps its winget-shaped layout, which is the contract
for the other three consumption paths.

Archives are not extracted. `nina.json` sets `archiveContainsInstaller: true` because the vendor
ships a ZIP bundle; `wgfetch` fetches that ZIP byte-for-byte and records `NestedInstallerType` and
`NestedInstallerFiles` in the winget manifest. Unpacking is the consumer's job.

---

## Licensing — read this before redistributing anything

`wgfetch` itself is **MIT licensed** (see [`LICENSE`](LICENSE)).

**Fetched installers are not.** Every installer `wgfetch` downloads remains under its own vendor's
licence and terms. Building a source tree for your own machines is one thing; **redistributing the
output directory, hosting it publicly, or sharing `installers/` may well be impermissible even though
`wgfetch` is MIT.** Check each vendor's terms before you publish anything `wgfetch` produced. The
tool makes acquisition convenient; it does not grant you any redistribution rights.

---

## Building from source

```bash
dotnet build wgfetch.slnx -c Release

# Hermetic suite: no network access, no model files required.
dotnet test wgfetch.slnx -c Release --filter "Category!=Live&Category!=Weights&Category!=Aot"
```

Additional, separately tagged tiers:

| Filter | What it does |
| --- | --- |
| `Category=Live` | Reaches real vendor hosts with range requests only; failures mean upstream changed |
| `Category=Weights` | Loads the real ONNX weights to verify E5 prefixes and pooling |
| `Category=Aot` | Runs against a published NativeAOT binary (`WGFETCH_AOT_BINARY`) |

Publish a portable `win-x64` NativeAOT ZIP:

```powershell
./scripts/publish.ps1
```

Mutation testing (Stryker.NET) is enforced in CI over the verification gate, the download and
atomic-rename logic and the version comparator, where a surviving mutant is a real security hole:

```bash
dotnet tool restore
dotnet tool run dotnet-stryker -- --config-file stryker-config.json
```

---

## Contributing and support

| I want to | Read |
| --- | --- |
| Contribute a change | [`CONTRIBUTING.md`](CONTRIBUTING.md) |
| Know how we behave here | [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) |
| Ask a question or report a bug | [`SUPPORT.md`](SUPPORT.md) |
| Report a vulnerability privately | [`SECURITY.md`](SECURITY.md) |

---

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Usage error |
| 2 | Unresolved — no discovery stage produced a verified candidate |
| 3 | Ambiguous — multiple candidates and no TTY to prompt |
| 4 | Verification failed |
| 5 | Hash mismatch |
| 6 | Missing prerequisite (model not installed) |
| 7 | Requires authentication (P1, unsupported) |
| 8 | Rate limited |
| 9 | Network error |
| 130 | Cancelled (Ctrl+C) |

Exit codes, log records and `--json` events always agree.
