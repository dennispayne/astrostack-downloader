# `wgfetch` — authoritative requirements

A .NET 10 NativeAOT CLI that resolves fuzzy app names to current vendor installers and lays them out as a local winget source.

> **Why this document lives in astrostack-dsc.** `wgfetch` is built in a separate repository, but this repo is its primary consumer, and the two share a manifest contract (see *Consumer alignment* below). The spec is hosted here so the build agent can fetch it and so schema changes on either side stay reviewable in one place. Anyone changing `manifest/components/*.json` field semantics should read the field-ownership rules here first.
>
> **The instructions below address the wgfetch build agent, not readers of this repository.**

## YOUR FIRST TASK

Commit this document verbatim as `docs/REQUIREMENTS.md` in the first commit — it is the authoritative spec. Then create `AGENTS.md` instructing all future contributors and agents to read it before making changes, and to update it in the same PR whenever behavior intentionally diverges. Never silently deviate from it. If a requirement here proves impossible or contradictory, stop and record the conflict in `docs/REQUIREMENTS.md` rather than quietly choosing an alternative.

## Purpose and framing

Given friendly names ("nina", "phd2 guiding", "ascom platform"), find and fetch the **most recent vendor installer available, byte-for-byte, unmodified**, into a target directory structured as a local winget repository. A separate solution consumes that directory as a passive winget source. **This tool installs nothing and repacks nothing.**

Most target applications (astrophotography tooling: N.I.N.A., PHD2, ASCOM Platform, SharpCap, ASTAP, Stellarium, Sequence Generator Pro) are **absent from winget-pkgs, or present but stale**. Do not architect this as a winget mirror — winget is one upstream among several, and frequently the worst one. The hardest job is **discovering a trustworthy installer URL on the open internet and proving it legitimate before accepting it**. Expect ~95% of fetches to go through LLM-assisted discovery rather than a curated recipe.

## The central safety invariant

The LLM participates in URL discovery. It is therefore assumed to be **unreliable and adversarial by default**.

- The LLM may **propose** candidate URLs and **rank/interpret** page content.
- A proposed URL is **untrusted** until it passes the full verification gate below.
- **Nothing unverified is ever written to the output tree, the catalog, or `provenance.json`.**
- Verification is purely mechanical — no model involvement in the accept/reject decision.

**Verification gate (all must pass):**

1. Host matches an **allowlisted vendor domain** for the resolved package. The allowlist is seeded by recipe or established on first human-confirmed resolution — never inferred by the model at fetch time.
2. HTTPS only. Redirects followed only within the allowlist; a redirect off-allowlist is a hard failure.
3. `HEAD`/ranged `GET` succeeds.
4. `Content-Type` indicates a binary payload, not `text/html`.
5. **Magic bytes** of the leading chunk match a known installer format (PE/MZ, MSI/OLE compound, ZIP, Inno, NSIS).
6. Content length is plausible for an installer and inconsistent with an error or interstitial page.

Failing any check: reject, log the reason, exit nonzero. **Never write a login page, error page, or interstitial to disk and never hash it as an installer.**

## Priority boundary

**P0 — build now.** Everything here for installers **publicly downloadable without authentication, purchase, or license acceptance**.

**P0 — auth detection.** Detect packages that cannot be fetched anonymously: Microsoft Store / Entra-licensed packages, paid or account-gated vendors, HTTP 401/403, or a response whose content-type/size/magic-bytes indicate a login or interstitial page. On detection: skip, emit `requires authentication (P1, unsupported)`, exit with a dedicated code, write nothing.

**P1 — leave seams, do not implement.** Authenticated/licensed acquisition (vendor logins, purchased-product portals, Store license files). Provide a pluggable credential/auth-provider interface and a per-package `requiresAuth` catalog flag. No credential storage, no interactive login, no Entra integration in P0. PixInsight and SharpCap Pro are known P1 cases — ship them as recognized-but-blocked entries.

## License

**MIT.** All dependencies are MIT-compatible: .NET 10, ONNX Runtime, ONNX Runtime GenAI, `Microsoft.ML.Tokenizers`, Phi-3.5-mini-instruct-onnx, E5-small-v2, winget-cli/winget-pkgs. Flag any dependency under a more restrictive license before adding it. README must state that **fetched installers remain under their vendors' licenses** — redistributing the output directory may be impermissible even though `wgfetch` is MIT.

## Stack

- **.NET 10**, **NativeAOT**, portable single-directory (no installer, no MSIX, no Inno/NSIS).
- `Microsoft.ML.OnnxRuntime` (embeddings), `Microsoft.ML.OnnxRuntimeGenAI` (local generative), `Microsoft.ML.Tokenizers`.
- Pinned models: **E5-small-v2** (ONNX int8) and **Phi-3.5-mini-instruct-onnx** (`cpu-int4-rtn-block-32`).
- Do not introduce llama.cpp/GGUF or any non-ONNX-Runtime inference path.

**Reference hardware — design to it:** Intel Celeron N5105, 4 cores @ 2.0GHz, **no AVX2**, 8GB RAM, integrated UHD (no GPU inference), Windows 11 Pro. CPU-only, scalar/SSE4.2 paths; Phi runs at roughly 2–6 tok/s. Keep local-model outputs to short structured selections and pre-filter HTML aggressively before it reaches the model.

## AI execution modes

- **Offline-local is the default.** All inference runs on-box against the pinned ONNX models. No network AI calls unless explicitly enabled.
- **Optional remote inference** via a **generic OpenAI-compatible endpoint**: `--ai-endpoint <url>`, `--ai-model <name>`, `--ai-key <key>` (or env var). Opt-in only, never a silent fallback. Useful when local Phi is too slow for HTML-heavy discovery.
- `--ai-mode local|remote|auto` — `auto` prefers local and escalates to remote only if configured and the local tier fails or exceeds a timeout.

## Discovery pipeline

Ordered, first success wins; every attempt recorded in provenance.

1. **User-supplied recipe** (highest trust). Deterministic: explicit source type (GitHub repo, vendor URL + extraction rule), version-detection rule, allowlisted domains. Recipes will be **few and frequently stale** — always validate a recipe's output through the verification gate, and on failure fall through to the next stage with a warning rather than aborting.
2. **Bundled seed recipes** for the astro stack (a deliverable): N.I.N.A., PHD2, ASCOM Platform, SharpCap, ASTAP, Stellarium, Sequence Generator Pro, plus PixInsight and SharpCap Pro as auth-blocked entries.
3. **Structured upstreams** — GitHub Releases API (`/releases/latest`, assets filtered by arch/type), then winget manifests via installed sources or `microsoft/winget-pkgs`.
4. **LLM-assisted discovery** (expected primary path, ~95%). Locate the vendor's download page via the configured search provider, fetch and reduce the HTML, have the model propose candidate links and a version string, then **subject every candidate to the verification gate**. On success, persist the learned resolution — URL pattern, allowlisted domain, extraction hint — as an **auto-generated recipe** in the cache so later runs are deterministic and cheap. This self-healing cache is a core feature, not an optimization.

## Web search — pluggable, no hardcoded engine

Discovery needs web search, and engine choice is a contested matter of user preference and privacy posture. **Never hardcode one.**

- Define an `ISearchProvider` interface (query → ranked result URLs + snippets) with multiple shipped implementations selected by `--search-provider <name>` or config. At minimum: **DuckDuckGo**, **Brave Search API**, **Startpage**, **Mojeek**, **SearXNG** (user-supplied instance URL), **Google/Bing** via API key, and **`none`**.
- **Ship a privacy-respecting default** (DuckDuckGo or a user-specified SearXNG instance), never one requiring an account. `--search-endpoint` and `--search-key` support self-hosted or API-keyed providers.
- **`--search-provider none` must be fully supported**: discovery then relies solely on recipes, cached auto-recipes, GitHub, and winget, degrading gracefully and stating clearly why an app could not be resolved.
- Honor `robots.txt`, send a truthful identifying `User-Agent` with the project URL, and rate-limit politely. Never scrape a search engine's HTML when an API is offered. Adding a provider must require implementing one interface and nothing else.

## Privacy

Treat user data as radioactive. This runs on a personal machine and must be defensible to a privacy-conscious audience.

- **No telemetry. No analytics. No crash reporting. No "anonymous usage statistics." Ever.** Not opt-out — absent.
- **No network calls that are not strictly required** to resolve or fetch a requested app. No update checks, no phone-home, no background beacons.
- **Local-first by default:** inference is on-box; nothing leaves the machine unless the user explicitly enables remote AI or a keyed search provider. Under `--ai-mode remote`, **log prominently and per-request** that page content is being transmitted off-box, and to which endpoint.
- **Never log secrets.** API keys, tokens, and credentials redacted in all output, logs, and `provenance.json`.
- Must run correctly with **no outbound access except vendor download hosts and explicitly configured providers** — a user should be able to firewall it tightly and still have it work. Document every network destination in the README.

## Prior art — use it, don't reimplement it

**`winget download --id <id> --version <v> --download-directory <dir>` is the fetch engine whenever the package exists in a configured winget source**; direct HTTPS is the fallback, and in practice the common case. Review `microsoft/winget-create`, `microsoft/winget-cli-restsource`, `noyard/LocalWinget` and `noyard/CustomWinGet` before writing manifest-generation or mirror logic and borrow their conventions. The novel contribution here is the **local-model resolution layer**, the **verification gate** and the **multi-format source emission** — everything else is thin glue.

## Recipes

- Supplied as an **optional column in the batch input manifest**, inline or referencing a fuller recipe file. For single invocations: `--recipe <path>` / `--recipe-inline <expr>`.
- Bundled seed recipes ship in-repo; user recipes override by package ID; auto-generated recipes live in the cache and rank below both. Schema versioned and documented.

## Version handling

- Prefer semver but **do not assume it** — vendor versions are often date-stamped, build-numbered or irregular (`1.2 HF3`, `2024.11.1`, `4.1b7`). Implement a deterministic comparator covering semver, dotted-numeric, date-based and mixed alphanumeric forms. **Use the LLM only when the comparator cannot order two versions**, and record that it was used.
- **Multi-source conflict:** newest wins and the fetch proceeds; emit a **non-blocking warning** naming every source, its version and the winner. All sources recorded in provenance.

## Prerequisites — frictionless first run

- `wgfetch prereqs install [--models-dir <path>] [--include-llm] [--dry-run]` downloads pinned models from Hugging Face, verifies each against **SHA256 hashes compiled into the binary**, and writes an install manifest. Hard-fails on mismatch. `wgfetch prereqs status` reports presence, paths, sizes and verification state.
- `--download-prereqs` on `fetch`/`resolve`/`refresh` fetches missing models inline with identical verification. Off by default; when off, a missing model errors with the expected file, its size and the exact command to obtain it.
- **Defaults:** models at `%LOCALAPPDATA%\wgfetch\models\{e5-small-v2,phi-3.5-mini-instruct-onnx}\`; output at `...\source\`; cache at `...\cache\`. First use must be exactly `wgfetch prereqs install` then `wgfetch fetch nina`, no other flags.

## Name resolution

- **Tier 1 — embeddings.** Embed package identifiers, display names, publishers, tags and aliases at refresh; embed the query at request time; cosine similarity → ranked candidates with confidence. **E5 requires `"passage: "` on catalog entries and `"query: "` on user input** — omitting these measurably degrades accuracy. Mean-pool, then L2-normalize. Target <100ms.
- **Tier 2 — Phi-3.5-mini**, invoked only when Tier 1 confidence is below `--threshold` or top candidates are clustered. Loaded on demand, disposed immediately.
- **Ambiguity:** prompt when attached to a TTY; else print ranked candidates and exit nonzero.
- The catalog is small and recipe-driven, not a 10k-package mirror — embed it eagerly and keep full-catalog winget indexing lazy.

## Configuration and inputs

- **Config file** at `%LOCALAPPDATA%\wgfetch\config.json` — default paths, threshold, arch/scope, retention, AI mode/endpoint, search provider and credentials, log level, theming, GitHub token. Precedence: CLI flag > env var > config file > built-in default.
- **Batch input:** `--from-file <path>` accepting a YAML/JSON app list with optional per-entry recipe, version pin, arch, and scope columns. This is how the consuming solution declares its desired app set declaratively.
- **GitHub token:** `GITHUB_TOKEN` / `--github-token` lifts the 60 req/hr anonymous ceiling to 5000. Warn when unauthenticated and approaching it.
- **Defaults:** architecture matches host, machine scope preferred. **Retention: keep N most recent versions, default 2**, pruning both installers and manifests, never pruning a version referenced by a pinned entry.

## Downloads — resumable and atomic

- **Range resume** (`Range: bytes=N-`) only when the server advertises `Accept-Ranges: bytes` and returns `206`. Detect capability; never assume it.
- **Guard every resume with a validator.** Store `ETag`/`Last-Modified` from the first response and send `If-Range` on resume. A `200` + full body means the artifact changed — **discard the partial and restart**. Splicing two builds produces a plausible-looking corrupt installer and must be impossible. **No validator → do not resume**; restart from zero, because bandwidth is cheaper than a silently corrupt binary.
- Download to `installers/.partial/<pkg>-<version>-<random>.tmp` with a sidecar `.meta` JSON (URL, validator, expected length, bytes written, upstream hash). **Atomically rename into the final path only after** full-length and SHA256 verification — the final path must never exist in an unverified state. The random suffix prevents collisions between concurrent runs; clean stale partials by age on startup; `--no-resume` forces a clean refetch.
- Run magic-byte verification on the **first chunk** — reject a login page at 4KB, not 200MB.
- Skip re-download when a file at the final path already matches the expected hash. Compute SHA256 of what was written, compare against any upstream-published hash, report mismatches loudly (`--require-hash-match` makes it fatal). Record architecture, installer type, scope, and silent switches from the upstream manifest verbatim.

## Parallelism

Network is idle; the N5105 is the bottleneck. Model this as a pipeline with separate pools, not one degree-of-parallelism knob: wide I/O-bound fan-out for name resolution, page fetches and API calls → a **width-1 funnel** for LLM discovery → wide, per-host-capped fan-out for verify/download/hash → a serialized funnel for manifest, index and provenance writes.

- **Parallelize:** downloads (default 3, **max 2 per host** — hobbyist-scale servers; respect `Retry-After`), page fetches, HTML reduction, GitHub API calls (centralized rate-limit accounting so a burst cannot blow the ceiling), and hashing.
- **Serialize hard:** **Phi inference is strictly one at a time** behind a global semaphore with a single model instance — two instances is ~5.4GB against 8GB RAM contending for 4 AVX2-less cores, which is slower *and* risks paging. Embeddings use one instance with batching.
- **Ordering:** queue apps needing LLM assist first so downloads overlap with inference.
- **Shared state:** `index.db`, `provenance.json`, `targets.yaml`, pruning, and recipe-cache writes need a single writer or a transaction per artifact. Pruning concurrent with a fetch of the same package is a race.
- **Surface:** `--parallel-downloads <n>` (default 3), `--max-per-host <n>` (default 2). **No flag for LLM concurrency — hardcode 1**; exposing it invites someone to set it to 8 and OOM the box. Under `--ai-mode remote` the funnel may widen to ~4, and only then.

## Observability and diagnostics

The tool must be debuggable by someone who did not write it, from logs alone, on a machine they cannot attach a debugger to.

- **Structured logging** via `Microsoft.Extensions.Logging`: `--log-level trace|debug|info|warn|error|none` (default `info`), `--log-file <path>`, and `--json` emitting machine-readable events on stdout with **all human-readable logging on stderr** so piping stays clean.
- **Every meaningful decision logged with its reasoning**: which discovery stage was attempted and why, each candidate URL and the exact check that rejected it, confidence scores against the threshold, version-comparison outcomes and the branch that decided them, cache hits and misses, and every retry with its cause.
- **Correlation IDs** per app and per operation so interleaved parallel work is reconstructible from a flat log.
- **Timing instrumentation** on every phase — search, page fetch, HTML reduction, inference, verification, download, hash — at debug level and summarized at end of run. Knowing whether 40 seconds went to Phi or a slow vendor host is the difference between a fixable problem and a mystery.
- `--diagnostics` writes a **redacted** bundle (config with secrets stripped, resolved paths, model presence and hashes, run log, provenance, timings, environment and runtime versions) for bug reports.
- Log at `trace` the exact prompt and raw completion for any model call, so hallucination and prompt-injection incidents are reconstructible.
- Exit codes, log records, and `--json` events must agree. Test that they do.

## Responsiveness — never block, never freeze

- **Async throughout.** No synchronous blocking I/O, no `.Result`, no `.Wait()`, no thread-pool starvation. Cap CPU-bound work below the core count; hashing and inference must not monopolize every core.
- **Ctrl+C honored immediately** — a `CancellationToken` flows through every operation, in-flight downloads stop promptly, partials and locks are cleaned up, and the process exits with a distinct cancellation code. Never require a second Ctrl+C, and never leave the terminal in a broken state (restore cursor and color on every exit path, including unhandled exceptions).
- **Startup must be fast.** Print something meaningful within ~100ms. Defer model, catalog and network setup until needed — `--help`, `--version` and argument errors must never load a model.
- **Anything exceeding ~500ms shows live progress**; nothing ever appears hung. Phi inference must stream visible progress rather than sitting silent.
- **Every network operation has a timeout** and bounded retry with backoff and jitter. Inference and hashing run off the render path so progress keeps animating.

## Progress display — lightweight, tasteful, astrophotography-themed

- **Detect terminal capability and degrade cleanly.** Full rendering for interactive ANSI terminals; plain incremental lines when redirected, piped, in CI, when `TERM=dumb`, or when `NO_COLOR` / `--no-color` / `--plain` is set. **Never emit escape codes into a log file or a pipe.** Use a well-maintained MIT console library (Spectre.Console fits) rather than hand-rolling ANSI, and **verify it survives NativeAOT trimming**.
- **Concurrent multi-line progress**: one live row per app showing its phase, plus an overall summary. Rows must not tear or interleave.
- **Astrophotography theming, never at the cost of clarity:** moon-phase spinner (`🌑🌒🌓🌔🌕🌖🌗🌘`) with ASCII fallback; phase vocabulary — *acquiring* (discovery), *tracking* (downloading), *plate solving* (verification), *stacking* (writing manifests/index), *calibrating* (prereqs and refresh); a completed run summarized as a short "session report" (targets acquired, integration time, frames rejected). Progress bars may render as a star field or exposure meter provided they stay instantly legible.
- **Legibility beats cleverness.** Warnings and errors are never hidden inside animation and always survive plain mode. Theming is disableable via `--plain` and must add negligible CPU overhead — capped refresh rate, no busy-waiting.

## Target list — repo-driven acquisition

A winget manifest cannot be a stub: `installer.yaml` requires a real `InstallerUrl` and `InstallerSha256`, and one invalid manifest breaks `winget validate` and the `index.db` build for the entire source. **Never write placeholder manifests.** `manifests/`, `index.db` and `rest/` contain acquired artifacts only.

Instead the output tree carries its declared intent in a root-level `targets.yaml`, which winget ignores. The repo becomes self-describing — the wishlist lives with the artifacts and no external YAML is required.

- `wgfetch add <name>...` appends entries and downloads nothing; `--resolve` also records the resolved ID and allowlisted domain. `wgfetch add --from-file <p>` imports a list. `wgfetch remove <name>...`.
- Per-entry state: **`listed`** (name only), **`resolved`** (ID + verified URL, no bytes), **`acquired`** (installer present, hashed, manifest emitted), **`stale`** (newer upstream exists), **`blocked`** (P1 auth-walled). Use **acquire/acquired** as the vocabulary throughout; do not use "hydrate".
- `wgfetch fetch --winget-repo <path>` reads that repo's `targets.yaml` and acquires everything `listed`, `resolved` or `stale`. **This is the steady-state command.** `--from-file` remains for external input; the two are mutually exclusive, and with neither, positional names are used.
- `--only-missing` skips stale refresh; `--refresh-stale` limits the run to already-acquired entries.
- `wgfetch status --winget-repo <path>` prints one row per target: state, acquired version, available version, last attempt and last error. **A populated-but-unacquired repo is valid and expected** — never treat it as corruption or an error, and never exit nonzero merely because targets remain unacquired.

`targets.yaml` is human-editable and round-trippable; preserve comments and key order on rewrite where the YAML library allows. Shape:

```yaml
version: 1
targets:
  - name: nina                      # required; the only mandatory field
    id: AstroStack.NINA             # filled on first successful resolution
    state: acquired                 # listed|resolved|acquired|stale|blocked
    acquiredVersion: 3.2.0.9001
    availableVersion: 3.2.0.9001
    arch: x64                       # optional override
    scope: machine
    pin: null                       # optional exact version pin
    allowlist: [nighttime-imaging.eu, github.com]
    recipe: null                    # optional inline recipe or path
    lastAttempt: 2026-09-10T14:22:11Z
    lastError: null
```

Unknown keys must round-trip untouched so a future version cannot destroy hand-authored data.

## Consumer alignment — astrostack-dsc

The primary consumer is **https://github.com/dennispayne/astrostack-dsc** (DSC v3, default branch `master`). **Read `manifest/components/*.json` there before designing any export** and conform to what actually exists. Its component shape today:

```json
{
  "id": "nina", "kind": "Application", "module": "imaging-app",
  "displayName": "N.I.N.A. - Nighttime Imaging 'N' Astronomy",
  "versionSource": "DisplayVersion", "expectedVersion": "3.2.0.9001",
  "downloadUrl": "https://github.com/isbeorn/nina/releases/download/...zip",
  "downloadFileName": "NINASetupBundle_3.2.0.9001.zip",
  "sha256": "4273b751...", "archiveContainsInstaller": true,
  "silentInstallArgs": null, "installNotes": "...", "verified": true
}
```

Those components **already model unacquired entries** — `sharpcap.json` and `astap-db-h18.json` currently sit at `downloadUrl: null, verified: false`. That is exactly the `listed` state, and filling those fields is precisely wgfetch's job. The two designs already agree; keep them that way.

**Field ownership is the governing rule.** wgfetch owns `downloadUrl`, `downloadFileName`, `sha256`, `verified` and a new `availableVersion`. Every other field — `expectedVersion`, `checkPath`, `versionSource`, `versionStrategy`, `versionRegex`, `silentInstallArgs`, `archiveContainsInstaller`, `module`, `kind`, `notes`, `installNotes` — is human-owned and **must never be overwritten**. Specifically **do not auto-bump `expectedVersion`**: it is a deliberate human pin that gates DSC remediation, so report newer builds in `availableVersion` and let a person promote them. Silently advancing a pin would turn an audit tool into an uncontrolled auto-updater.

- `wgfetch export --format astrostack-dsc --out <dir>` emits **per-component partial JSON patches containing machine-owned fields only**, plus a summary of what changed and what is newly available. **Never emit whole-file replacements** — that would destroy the hand-written `notes` fields those components depend on.
- `wgfetch export --format applist` emits the plain target list for round-tripping into another repo.
- `wgfetch import --format astrostack-dsc <dir>` seeds `targets.yaml` from existing components, mapping `id` → target name, `downloadUrl: null` → `listed`, and populated+`verified: true` → `acquired`, so an established DSC repo adopts wgfetch in one command.
- Golden-file tests cover both export formats and **assert human-owned fields are absent from patches**. Include a fixture reproducing the real `nina`, `astap`, `sharpcap` and `astap-db-h18` components.

**Interop assumptions to surface, not silently invent.** astrostack-dsc requires a caller-supplied `DownloadsRoot` and expects installers under it; wgfetch's `installers/` layout is winget-shaped and will not match by accident. Emit an explicit path-mapping file in the export so the DSC side resolves artifacts by ID rather than guessing filenames. Do not reorganize `installers/` to suit DSC — the winget layout is the contract for the other three consumption paths. Where a component's `id` is a lowercase slug (`nina`, `astap`) and the winget `PackageIdentifier` is not (`AstroStack.NINA`), record **both** in `targets.yaml` and in the export so the join key is never ambiguous.

**Do not extract archives.** `nina.json` sets `archiveContainsInstaller: true` because the vendor ships a ZIP bundle. wgfetch fetches that ZIP byte-for-byte and records `NestedInstallerType`/`NestedInstallerFiles` in the winget manifest; unpacking is the consumer's job and would violate the no-repacking rule.

## Output layout

Emit all of the following; let the consumer choose.

- `manifests/<first-letter>/<Publisher>/<Package>/<Version>/*.yaml` — winget-pkgs convention, three-file set, `InstallerUrl` rewritten to the local path/URI, `InstallerSha256` set to the locally computed hash. **Rewriting a manifest is not repacking — installer bytes stay untouched.**
- `installers/...` — downloaded binaries, unmodified, original filenames preserved.
- `index.db` — SQLite in winget's pre-indexed source schema, for `Microsoft.PreIndexed.Package` consumers.
- `rest/` — static JSON shaped to the winget REST source API, for consumers fronting the directory with a trivial HTTP server.
- `provenance.json` — per artifact: friendly query, resolved ID, discovery stage, all candidate URLs and their verification results, accepted URL, resolved version, conflicting source versions, computed SHA256, upstream hash if any, timestamp, resolution tier, confidence, AI mode, and whether a recipe was used or auto-generated.

README must state plainly that **winget cannot `source add` a bare manifest folder**, and spell out what each of the three consumption paths requires.

## CLI surface

```
wgfetch fetch <name>...      [--winget-repo] [--from-file] [--output] [--installer-dir] [--cache-dir]
                             [--only-missing] [--refresh-stale]
                             [--embedding-model] [--llm-model] [--models-root]
                             [--recipe] [--recipe-inline] [--arch] [--scope]
                             [--threshold] [--keep-versions] [--require-hash-match]
                             [--ai-mode] [--ai-endpoint] [--ai-model] [--ai-key]
                             [--github-token] [--parallel-downloads] [--max-per-host]
                             [--search-provider] [--search-endpoint] [--search-key]
                             [--log-level] [--log-file] [--no-color] [--plain]
                             [--no-resume] [--download-prereqs] [--dry-run] [--json]
wgfetch resolve <name>...    # resolution + verification, no download
wgfetch add <name>...        # append to targets.yaml [--resolve] [--from-file]
wgfetch remove <name>...
wgfetch status               # per-target state table [--winget-repo]
wgfetch export --format astrostack-dsc|applist [--out]
wgfetch import --format astrostack-dsc <dir>
wgfetch refresh              # update catalog + embeddings + recipe cache
wgfetch prereqs install|status
wgfetch verify [--output]    # re-hash artifacts against provenance.json
wgfetch recipes list|show|export|validate
wgfetch list [--output]
wgfetch diagnostics          # redacted support bundle
```

`--json` on every command, `--verbose`, and **distinct exit codes per failure class**: unresolved, ambiguous, verification-failed, hash-mismatch, missing-prereq, requires-auth, rate-limited, network-error, cancelled.

## Target workflow — optimize for this

```
wgfetch prereqs install --include-llm            # once
wgfetch add nina phd2 astap ascom-platform       # build the target list, no downloads
wgfetch fetch --winget-repo .\source --dry-run   # resolve + verify only
wgfetch fetch --winget-repo .\source             # acquire; emit source tree
wgfetch status --winget-repo .\source            # acquired / stale / blocked
wgfetch export --format astrostack-dsc --out .\patches
```

Steady state is `wgfetch fetch --winget-repo .\source`: acquire new targets, refresh stale ones. `--dry-run` must print, per app, the resolved package ID, version, verification result, and originating discovery stage, so a user can review before any bytes move. A run a month later should hit cached auto-generated recipes and skip the LLM entirely for unchanged vendors — that speedup is a core design goal.

## Testing — maximize rigor, this is not optional

Treat the test suite as a primary deliverable of equal weight to the application. Untested code is incomplete.

**Structure for testability:** all logic lives in a class library; the NativeAOT executable is a thin shell. All I/O — HTTP, filesystem, process invocation, model inference — sits behind interfaces with fakes. No test may reach the network unless it is in the explicitly-tagged live tier.

**Required tiers:**

1. **Unit — xUnit, fast, hermetic, the bulk of the suite.** **>85% line coverage** enforced in CI, covering *every* documented failure mode, not just happy paths.
2. **Mutation testing — Stryker.NET, required.** Coverage alone does not prove assertions are meaningful. Enforce a mutation-score threshold in CI, focused on the verification gate, atomic-rename/resume logic and the version comparator, where a surviving mutant is a real security hole.
3. **Property-based tests** on the version comparator and manifest/HTML parsers — comparison must be total, antisymmetric and transitive across all supported formats. Fuzz parsers with malformed input; they must never crash or hang.
4. **Golden-file tests** for every emitted artifact — manifests, `index.db` schema, `rest/` JSON, `provenance.json`, and both export formats.
5. **Live-network integration tier**, tagged and excluded from the default run, schedulable in CI to catch **recipe rot and upstream drift**. Must **not** download full installers: range-request the leading bytes and assert magic-byte/content-type correctness. Failures here mean "upstream changed," not a code defect.
6. **NativeAOT smoke tests** against the published binary — startup, `--help`, `--json` shape, and exit codes must survive AOT trimming. AOT reflection/serialization breakage will not surface in library tests.

**Adversarial cases that must be tested explicitly:**

- LLM proposes a nonexistent domain, or a plausible but off-allowlist URL → rejected at the allowlist check. A URL that redirects off-allowlist → rejected.
- LLM proposes a URL returning HTML (login/404/interstitial) with a `200` status → rejected at content-type and magic-byte checks.
- LLM returns malformed, empty, truncated or prompt-injected output → fails closed, surfaces Tier-1 candidates, writes nothing.
- **Page content containing prompt-injection text attempting to redirect the download** → no injected instruction can move a URL past the mechanical gate.
- Hash mismatch against upstream → loud failure; fatal under `--require-hash-match`.
- Auth-walled package → detected, skipped, dedicated exit code, nothing written.
- Stale recipe pointing at a dead URL → falls through to the next stage with a warning, does not abort.
- Retention pruning → never deletes a pinned version, never orphans a manifest or installer.
- **Resume: server ignores `Range` and returns `200`** → must restart, never splice. **Validator changed between attempts** → must discard the partial.
- Truncated response whose length coincidentally matches expected → must fail hash verification.
- **Hard assertion: no file ever appears at the final installer path without passing full verification.** Make this a dedicated mutation-testing target.
- **Unacquired targets never leak into the source:** with `listed`/`blocked` entries present, every emitted manifest passes `winget validate`, `index.db` builds cleanly, and no stub manifest or zero-byte installer exists. Export patches contain **zero** human-owned fields.
- Concurrent fetches of the same package resolve to one download, not two; `--json` ordering is deterministic regardless of completion order.
- Rate-limit and `403` handling against a fake GitHub.
- **Secret redaction:** a known key/token value must never appear in any log at any verbosity, in `provenance.json`, in `--json` output, or in a `--diagnostics` bundle.
- **No unexpected network calls:** with a deny-all fake HTTP layer, assert the tool contacts only vendor download hosts and configured providers — no telemetry, update checks or beacons.
- **`--search-provider none`** degrades gracefully to recipes/GitHub/winget and reports clearly why an app was unresolvable.
- **Terminal capability:** redirected/piped/CI/`TERM=dumb`/`NO_COLOR`/`--plain` output contains **zero ANSI escape sequences**; human logging goes to stderr while `--json` stdout stays parseable. Parallel progress rendering does not tear, interleave or drop warnings and errors.
- **Cancellation:** Ctrl+C mid-download and mid-inference stops on the first signal, cleans up partials and locks, and exits with the cancellation code.
- **Startup latency:** `--help`, `--version`, and invalid-argument paths return fast and **load no model files** — assert with a model directory that would fail if touched.

**Also required:** deterministic model-inference fakes so resolution tests never load real ONNX weights, plus a small separately-tagged tier that does load them to verify E5 prefix and pooling correctness. Provide a documented `dotnet test` entry point running the hermetic suite with **zero network access and zero model files present**.

## Deliverables

The NativeAOT CLI; `docs/REQUIREMENTS.md` and `AGENTS.md` as the first commit; prereq installer with pinned hashes; catalog and refresh; both resolution tiers; the verification gate; the discovery pipeline with self-healing recipe cache; `targets.yaml` with the `add`/`status`/`export`/`import` commands; the pluggable search-provider abstraction; structured logging, timing instrumentation and the `diagnostics` bundle; the themed progress display with plain-mode fallback; bundled astro-stack seed recipes; the four output artifacts; the full test suite with CI wiring for coverage and mutation thresholds; a portable ZIP publish script (`win-x64`, `PublishAot`, native ORT DLLs alongside the EXE) plus an optional winget manifest for `wgfetch` itself (`InstallerType: zip` + `NestedInstallerType: portable`); and a README covering first-use, the three consumption paths, the P0/P1 boundary, search-provider config, every network destination, astrostack-dsc interop, and the installer-licensing caveat.

## Non-goals (P0)

No authenticated or paid-app acquisition. No credential storage. No telemetry of any kind. No GPU inference. No repacking or modification of installers. No MSIX/Inno/NSIS packaging of fetched apps. No installing anything. No concurrency guards for other workloads on the machine — known and accepted footgun.
