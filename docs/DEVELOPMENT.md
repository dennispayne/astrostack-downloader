# Building `wgfetch` from source

```bash
dotnet build wgfetch.slnx -c Release

# Hermetic suite: no network access, no model files required.
dotnet test wgfetch.slnx -c Release --filter "Category!=Live&Category!=Weights&Category!=Aot"
```

Package versions are centrally managed in [`Directory.Packages.props`](../Directory.Packages.props);
individual `.csproj` files reference packages without a `Version` attribute.

## Test tiers

The hermetic suite above is what CI runs on every push and PR. Three additional tiers are separately
tagged and opt-in, because they need real network access, real model weights, or a published binary:

| Filter | What it does | When it runs in CI |
| --- | --- | --- |
| `Category=Live` | Reaches real vendor hosts with range requests only; failures mean upstream changed, not that the code is broken | Weekly schedule, or manual dispatch |
| `Category=Weights` | Loads the real ONNX weights to verify E5 prefixes and pooling | Weekly schedule, or manual dispatch |
| `Category=Aot` | Runs against a published NativeAOT binary (`WGFETCH_AOT_BINARY`) | Every push/PR (Windows), plus an Intel-SDE no-AVX2 variant on schedule/dispatch |

Publish a portable `win-x64` NativeAOT ZIP:

```powershell
./scripts/publish.ps1
```

## Mutation testing

Stryker.NET is enforced in CI over the verification gate, the download/atomic-rename logic and the
version comparator — the paths where a surviving mutant is a real security hole:

```bash
dotnet tool restore
dotnet tool run dotnet-stryker -- --config-file stryker-config.json
```

## Reference hardware approximation

A dedicated CI job (`reference-hardware`, scheduled/manual only) approximates the target N5105-class
box: 4 cgroup-limited cores, an 8 GiB memory ceiling, and NativeAOT execution under Intel SDE with no
AVX2. See [`.github/workflows/build.yml`](../.github/workflows/build.yml) for the exact mechanism.

## CI workflows at a glance

| Workflow | Purpose |
| --- | --- |
| `build.yml` | Hermetic tests + coverage gate, mutation testing, NativeAOT publish/smoke, and the opt-in live/weights/reference-hardware tiers |
| `lint.yml` | Enforces `.slnx` over `.sln`, runs `dotnet format --verify-no-changes` |
| `codeql.yml` | CodeQL analysis for GitHub Actions workflows and C# |
| `dependabot.yml` | Weekly, grouped dependency updates for NuGet and GitHub Actions |

All workflows skip cleanly while a PR is still a draft (an agent's tracking PR), and pick up on the
next push once it's marked ready for review.
