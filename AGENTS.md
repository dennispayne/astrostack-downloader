# AGENTS.md — instructions for contributors and AI agents

## Read the specification first

[`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md) is the **authoritative specification** for `wgfetch`.
It is committed verbatim from
`https://raw.githubusercontent.com/dennispayne/astrostack-dsc/master/docs/wgfetch/REQUIREMENTS.md`.

**Before making any change to this repository — code, tests, docs or CI — read
`docs/REQUIREMENTS.md` in full.** It governs every design decision: the safety invariant, the
verification gate, the P0/P1 boundary, privacy rules, output layout, CLI surface, the
astrostack-dsc consumer contract, and the test plan.

## Rules

1. **Never silently deviate from the specification.** If behaviour intentionally diverges,
   update `docs/REQUIREMENTS.md` **in the same pull request** that introduces the divergence,
   and explain why in the PR description.
2. **If a requirement is impossible or contradictory, stop.** Record the conflict in
   `docs/REQUIREMENTS.md` (a `## Known conflicts` section) rather than quietly choosing an
   alternative.
3. **The verification gate is not negotiable.** A model may propose candidate URLs; it may never
   authorize a download. Nothing unverified is ever written to the output tree, the catalog or
   `provenance.json`. Changes touching `Verification/`, the download/atomic-rename path, or the
   version comparator require accompanying tests, including adversarial cases.
4. **Privacy is absolute.** No telemetry, analytics, crash reporting, update checks or
   phone-home — not opt-out, absent. Never log secrets; redact keys and tokens everywhere,
   including `--diagnostics` bundles.
5. **Tests are a first-class deliverable.** Untested code is incomplete. New behaviour needs
   unit tests; changes to the verification gate, resume logic and version comparator need
   adversarial and property-based tests. No test may reach the network outside the tagged
   live tier.
6. **This tool installs nothing and repacks nothing.** Do not extract archives, do not modify
   installer bytes. Rewriting a manifest's `InstallerUrl`/`InstallerSha256` is permitted;
   touching installer bytes is not.
7. **Human-owned fields in astrostack-dsc components must never be overwritten.** wgfetch owns
   `downloadUrl`, `downloadFileName`, `sha256`, `verified` and `availableVersion` only.

## Build, test, lint

```bash
dotnet build wgfetch.slnx -c Release
dotnet test  wgfetch.slnx -c Release        # hermetic: no network, no model files required
```

The default `dotnet test` run must pass with **zero network access and zero model files
present**. Live-network tests are tagged and excluded by default:

```bash
dotnet test --filter "Category=Live"        # opt in explicitly
```

## Layout

| Path                      | Purpose                                                        |
| ------------------------- | -------------------------------------------------------------- |
| `docs/REQUIREMENTS.md`    | Authoritative specification — read before changing anything.    |
| `src/WgFetch.Core/`       | All logic. I/O sits behind interfaces so it can be faked.       |
| `src/WgFetch.Cli/`        | Thin NativeAOT shell over the library.                          |
| `tests/WgFetch.Core.Tests/` | xUnit suite: unit, property-based, golden-file, adversarial.  |
