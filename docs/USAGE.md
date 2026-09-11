# Usage

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

## Defaults (Windows)

| Purpose | Path |
| --- | --- |
| Models | `%LOCALAPPDATA%\wgfetch\models\{e5-small-v2,phi-3.5-mini-instruct-onnx}\` |
| Output source tree | `%LOCALAPPDATA%\wgfetch\source\` |
| Cache | `%LOCALAPPDATA%\wgfetch\cache\` |
| Config | `%LOCALAPPDATA%\wgfetch\config.json` |

Precedence is **CLI flag > environment variable > config file > built-in default**.

## Persisted defaults and first-run setup

Use `wgfetch config` with no arguments for the interactive setup menu. It shows persisted values with
keys masked, includes model installation status, and can install one or all pinned models. It requires
an attached terminal and exits with code 3 when run non-interactively.

```powershell
wgfetch config set outputDirectory D:\wgfetch-source
wgfetch config set modelsRoot D:\wgfetch-models
wgfetch config get outputDirectory
wgfetch config list                 # secrets are redacted
wgfetch config unset outputDirectory
```

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

## Licensing — read this before redistributing anything

`wgfetch` itself is **MIT licensed** (see [`LICENSE`](../LICENSE)).

**Fetched installers are not.** Every installer `wgfetch` downloads remains under its own vendor's
licence and terms. Building a source tree for your own machines is one thing; **redistributing the
output directory, hosting it publicly, or sharing `installers/` may well be impermissible even though
`wgfetch` is MIT.** Check each vendor's terms before you publish anything `wgfetch` produced. The
tool makes acquisition convenient; it does not grant you any redistribution rights.

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
