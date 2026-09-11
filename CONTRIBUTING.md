# Contributing to wgfetch

Thanks for your interest in `wgfetch`. This document describes how to report problems, propose
changes and get a pull request merged.

Everyone participating in this project is expected to follow the
[Code of Conduct](CODE_OF_CONDUCT.md).

## Read the specification first

[`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md) is the **authoritative specification**. It governs
the safety invariant, the verification gate, the P0/P1 boundary, privacy rules, output layout, the
CLI surface, the astrostack-dsc consumer contract and the test plan. Read it before changing code,
tests, docs or CI. AI agents must additionally read [`AGENTS.md`](AGENTS.md).

If behaviour intentionally diverges from the spec, update `docs/REQUIREMENTS.md` **in the same pull
request** and say why in the description. If a requirement is impossible or self-contradictory,
stop and record the conflict in a `## Known conflicts` section rather than quietly picking an
alternative.

## Reporting bugs and requesting features

- **Security vulnerabilities:** do not open a public issue. Follow [`SECURITY.md`](SECURITY.md).
- **Bugs and features:** open an issue using the templates at
  [New issue](https://github.com/dennispayne/astrostack-downloader/issues/new/choose). Search
  existing issues first.
- **Questions and usage help:** see [`SUPPORT.md`](SUPPORT.md).

Good bug reports include the exact command line, the `wgfetch --version` output, the operating
system, and the relevant log or `--json` output. **Redact any API keys or tokens before pasting.**

## Development setup

You need the .NET SDK pinned in [`global.json`](global.json). Then:

```bash
dotnet build wgfetch.slnx -c Release

# Hermetic suite: no network access, no model files required.
dotnet test wgfetch.slnx -c Release --filter "Category!=Live&Category!=Weights&Category!=Aot"
```

The default test run must pass with **zero network access and zero model files present**. The
heavier tiers are tagged and opt-in:

| Filter | What it does |
| --- | --- |
| `Category=Live` | Reaches real vendor hosts with range requests only |
| `Category=Weights` | Loads the real ONNX weights |
| `Category=Aot` | Runs against a published NativeAOT binary (`WGFETCH_AOT_BINARY`) |

Formatting is enforced in CI by `dotnet format`; run it before you push:

```bash
dotnet format wgfetch.slnx
```

Changes touching `Verification/`, `Downloads/` or `Versioning/` must also keep the mutation score
above the threshold in [`stryker-config.json`](stryker-config.json):

```bash
dotnet tool restore
dotnet tool run dotnet-stryker -- --config-file stryker-config.json
```

## Rules that pull requests must respect

1. **The verification gate is not negotiable.** A model may propose candidate URLs; it may never
   authorize a download. Nothing unverified is written to the output tree, the catalog or
   `provenance.json`. Changes to `Verification/`, the download/atomic-rename path or the version
   comparator require accompanying tests, including adversarial cases.
2. **Privacy is absolute.** No telemetry, analytics, crash reporting, update checks or phone-home —
   not opt-out, absent. Never log secrets; redact keys and tokens everywhere, including
   `--diagnostics` bundles.
3. **This tool installs nothing and repacks nothing.** Do not extract archives or modify installer
   bytes. Rewriting a manifest's `InstallerUrl`/`InstallerSha256` is permitted.
4. **Human-owned fields in astrostack-dsc components are never overwritten.** `wgfetch` owns
   `downloadUrl`, `downloadFileName`, `sha256`, `verified` and `availableVersion` only.
5. **Tests are a first-class deliverable.** Untested code is incomplete. No test may reach the
   network outside the tagged live tier.

## Submitting a pull request

1. Fork the repository and create a branch from `main`.
2. Keep the change focused; unrelated fixes belong in their own pull request.
3. Add or update tests, and update the docs affected by your change.
4. Make sure `dotnet build`, the hermetic `dotnet test` run and `dotnet format` all pass locally.
5. Open the pull request, fill in the template, and link the issue it closes.

Pull requests are squash-merged once CI is green and a maintainer has approved. By contributing you
agree that your contribution is licensed under the repository's [MIT licence](LICENSE).
