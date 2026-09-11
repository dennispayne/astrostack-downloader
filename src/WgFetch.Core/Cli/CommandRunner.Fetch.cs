using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Catalog;
using WgFetch.Core.Discovery;
using WgFetch.Core.Downloads;
using WgFetch.Core.Inference;
using WgFetch.Core.Logging;
using WgFetch.Core.Model;
using WgFetch.Core.Output;
using WgFetch.Core.Prereqs;
using WgFetch.Core.Progress;
using WgFetch.Core.Recipes;
using WgFetch.Core.Search;
using WgFetch.Core.Targets;
using WgFetch.Core.Verification;
using WgFetch.Core.Versioning;

namespace WgFetch.Core.Cli;

/// <summary>
/// <c>fetch</c>, <c>resolve</c> and <c>refresh</c>: name resolution, discovery, the verification gate,
/// download and output emission (docs/REQUIREMENTS.md, "End-to-end flow").
/// </summary>
public sealed partial class CommandRunner
{
    /// <summary>One target's journey through the pipeline, kept so emission can run once at the end.</summary>
    private sealed record FetchOutcome
    {
        public required string Query { get; init; }

        public required ExitCode ExitCode { get; init; }

        public string? PackageIdentifier { get; init; }

        public string? Version { get; init; }

        public string? InstallerPath { get; init; }

        public DiscoveryOutcome? Discovery { get; init; }

        public Recipe? Recipe { get; init; }

        public string? Sha256 { get; init; }

        public string? Message { get; init; }
    }

    private async Task<ExitCode> FetchAsync(
        ParsedCommandLine parsed,
        RunSettings settings,
        bool download,
        CancellationToken cancellationToken)
    {
        var recipes = LoadRecipes(settings);
        var targets = await LoadTargetsAsync(settings, cancellationToken).ConfigureAwait(false);
        var explicitNames = await CollectNamesAsync(parsed, settings, cancellationToken).ConfigureAwait(false);

        // No names given: the repo's own wishlist is the input, which is the whole point of targets.yaml.
        var names = explicitNames.Count > 0
            ? explicitNames
            : targets.EntriesNeedingAcquisition(settings.OnlyMissing, settings.RefreshStale)
                .Select(t => t.Name)
                .ToArray();

        if (names.Count == 0)
        {
            Report(targets.Targets.Count == 0
                ? "No targets. Add some with 'wgfetch add <name>...'."
                : "Nothing to do: every target is already acquired and current.");
            return ExitCode.Success;
        }

        var resolver = await CreateResolverAsync(recipes, settings, cancellationToken).ConfigureAwait(false);
        if (resolver.ExitCode is { } prereqFailure)
        {
            _stderr.WriteLine(resolver.Message);
            return prereqFailure;
        }

        var gate = new VerificationGate(CreateHttpGateway(), new VerificationOptions(), _logger);
        var pipeline = new DiscoveryPipeline(BuildStages(settings, resolver.Router), gate, _logger);
        var downloader = new InstallerDownloader(CreateHttpGateway(), _logger);

        // Downloads fan out; the LLM funnel inside the pipeline serializes itself (LlmConcurrencyGate).
        var slots = new SemaphoreSlim(Math.Max(1, settings.ParallelDownloads));
        var hostSlots = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        var hostSlotsLock = new object();

        var tasks = names.Distinct(StringComparer.OrdinalIgnoreCase).Select(async name =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ProcessOneAsync(
                    name,
                    settings,
                    recipes,
                    targets,
                    resolver,
                    pipeline,
                    downloader,
                    download,
                    AcquireHostSlot,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        }).ToArray();

        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

        await TargetsFile.SaveAsync(targets, TargetsPath(settings), cancellationToken).ConfigureAwait(false);

        if (download && !settings.DryRun)
        {
            await EmitSourceAsync(settings, outcomes, cancellationToken).ConfigureAwait(false);
        }

        var worst = ExitCode.Success;
        foreach (var outcome in outcomes)
        {
            if (outcome.ExitCode != ExitCode.Success && worst == ExitCode.Success)
            {
                worst = outcome.ExitCode;
            }
        }

        Emit(new JsonEvent { Event = "run.finished", ExitCode = (int)worst });
        return worst;

        SemaphoreSlim AcquireHostSlot(string host)
        {
            lock (hostSlotsLock)
            {
                if (!hostSlots.TryGetValue(host, out var slot))
                {
                    slot = new SemaphoreSlim(Math.Max(1, settings.MaxPerHost));
                    hostSlots[host] = slot;
                }

                return slot;
            }
        }
    }

    private async Task<FetchOutcome> ProcessOneAsync(
        string name,
        RunSettings settings,
        RecipeStore recipes,
        TargetsDocument targets,
        ResolverContext resolver,
        DiscoveryPipeline pipeline,
        InstallerDownloader downloader,
        bool download,
        Func<string, SemaphoreSlim> hostSlot,
        CancellationToken cancellationToken)
    {
        _progress.StartTarget(name);
        var entry = targets.Find(name);

        try
        {
            _progress.Update(name, AcquisitionPhase.Acquiring);
            var resolution = await ResolveNameAsync(name, recipes, resolver, settings, cancellationToken)
                .ConfigureAwait(false);

            if (resolution.Failure is { } failure)
            {
                _progress.Complete(name, AcquisitionPhase.Failed, failure.Message);
                RecordFailure(entry, failure.Message);
                Emit(new JsonEvent
                {
                    Event = "target.failed",
                    Target = name,
                    Status = failure.ExitCode.ToString(),
                    Message = failure.Message,
                });
                _stderr.WriteLine($"{name}: {failure.Message}");
                return failure;
            }

            var recipe = resolution.Recipe!;
            _progress.Update(name, AcquisitionPhase.Acquiring, recipe.PackageId);

            var request = new DiscoveryRequest
            {
                Query = name,
                ComponentId = recipe.ComponentId,
                UserRecipe = recipes.UserRecipes.FirstOrDefault(r => Same(r.ComponentId, recipe.ComponentId)),
                SeedRecipe = recipes.SeedRecipes.FirstOrDefault(r => Same(r.ComponentId, recipe.ComponentId)),
                AutoRecipe = recipes.AutoRecipes.FirstOrDefault(r => Same(r.ComponentId, recipe.ComponentId)),
                Architecture = entry?.Arch ?? settings.Architecture,
                Scope = entry?.Scope ?? settings.Scope,
                Pin = entry?.Pin,
                KnownGitHubRepository = recipe.Repository,
                KnownWingetPackageId = recipe.PackageId,
            };

            var discovery = await pipeline.ResolveAsync(request, cancellationToken).ConfigureAwait(false);

            if (discovery.LearnedRecipe is { } learned)
            {
                recipes.AddAutoRecipe(learned);
                await new AutoRecipeCache(settings.CacheDirectory, _logger)
                    .SaveAsync(learned, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!discovery.Success)
            {
                var code = discovery.RequiresAuth
                    ? ExitCode.RequiresAuth
                    : WorstVerificationCode(discovery);

                _progress.Complete(name, AcquisitionPhase.Failed, discovery.Summary);
                RecordFailure(entry, discovery.Summary);
                await WriteProvenanceAsync(settings, name, recipe, discovery, null, null, resolution, cancellationToken)
                    .ConfigureAwait(false);
                Emit(new JsonEvent
                {
                    Event = "target.failed",
                    Target = name,
                    Status = code.ToString(),
                    Message = discovery.Summary,
                });
                _stderr.WriteLine($"{name}: {discovery.Summary}");
                return new FetchOutcome { Query = name, ExitCode = code, Message = discovery.Summary, Discovery = discovery };
            }

            var candidate = discovery.Accepted!;
            var verification = discovery.AcceptedVerification!;
            var version = candidate.Version ?? "unknown";
            var packageId = recipe.PackageId;

            if (entry is not null)
            {
                entry.Id ??= packageId;
                entry.ComponentId ??= recipe.ComponentId;
                entry.AvailableVersion = version;
                entry.LastAttempt = _dependencies.TimeProvider.GetUtcNow();
                entry.LastError = null;
                if (entry.State == TargetState.Listed)
                {
                    entry.State = TargetState.Resolved;
                }
            }

            Emit(new JsonEvent
            {
                Event = "candidate.accepted",
                Target = name,
                Stage = candidate.Stage.ToString(),
                Url = candidate.Url.ToString(),
                Version = version,
            });

            if (!download)
            {
                _progress.Complete(name, AcquisitionPhase.Done, $"{packageId} {version}");
                Report($"{name} -> {packageId} {version} {candidate.Url}");
                await WriteProvenanceAsync(settings, name, recipe, discovery, null, null, resolution, cancellationToken)
                    .ConfigureAwait(false);
                return new FetchOutcome
                {
                    Query = name,
                    ExitCode = ExitCode.Success,
                    PackageIdentifier = packageId,
                    Version = version,
                    Discovery = discovery,
                    Recipe = recipe,
                };
            }

            if (settings.DryRun)
            {
                _progress.Complete(name, AcquisitionPhase.Skipped, "dry run");
                Report($"[dry-run] would download {candidate.Url} -> {packageId} {version}");
                return new FetchOutcome
                {
                    Query = name,
                    ExitCode = ExitCode.Success,
                    PackageIdentifier = packageId,
                    Version = version,
                    Discovery = discovery,
                    Recipe = recipe,
                };
            }

            var downloadUrl = verification.FinalUrl ?? candidate.Url;
            var fileName = candidate.FileName is { Length: > 0 } named
                ? named
                : Path.GetFileName(downloadUrl.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"{packageId}-{version}.bin";
            }

            var finalPath = Path.Combine(settings.ResolveInstallerRoot(), packageId, version, fileName);

            var slot = hostSlot(downloadUrl.Host);
            await slot.WaitAsync(cancellationToken).ConfigureAwait(false);
            DownloadResult result;
            try
            {
                _progress.Update(name, AcquisitionPhase.Tracking, fileName, 0);
                using var scope = _timings.Measure("download");
                result = await downloader.DownloadAsync(
                    new DownloadRequest
                    {
                        Url = downloadUrl,
                        FinalPath = finalPath,
                        ExpectedLength = verification.ContentLength,
                        Validator = verification.Validator,
                        ServerAcceptsRanges = verification.AcceptsRanges,
                        UpstreamSha256 = candidate.UpstreamSha256,
                        RequireHashMatch = settings.RequireHashMatch,
                        NoResume = settings.NoResume,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                slot.Release();
            }

            if (!result.Success)
            {
                _progress.Complete(name, AcquisitionPhase.Failed, result.Reason);
                RecordFailure(entry, result.Reason);
                _stderr.WriteLine($"{name}: {result.Reason}");
                Emit(new JsonEvent
                {
                    Event = "target.failed",
                    Target = name,
                    Status = result.Status.ToString(),
                    Message = result.Reason,
                });
                return new FetchOutcome { Query = name, ExitCode = result.ToExitCode(), Message = result.Reason };
            }

            if (entry is not null)
            {
                entry.State = TargetState.Acquired;
                entry.AcquiredVersion = version;
            }

            _progress.Complete(name, AcquisitionPhase.Done, $"{packageId} {version} ({result.Status.ToString().ToLowerInvariant()})");
            Emit(new JsonEvent
            {
                Event = "download.completed",
                Target = name,
                Version = version,
                Sha256 = result.Sha256,
                Status = result.Status.ToString(),
            });

            await WriteProvenanceAsync(settings, name, recipe, discovery, result.Sha256, version, resolution, cancellationToken)
                .ConfigureAwait(false);

            return new FetchOutcome
            {
                Query = name,
                ExitCode = ExitCode.Success,
                PackageIdentifier = packageId,
                Version = version,
                InstallerPath = result.Path,
                Sha256 = result.Sha256,
                Discovery = discovery,
                Recipe = recipe,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = SecretRedactor.Redact(ex.Message, settings.Secrets);
            _progress.Complete(name, AcquisitionPhase.Failed, message);
            RecordFailure(entry, message);
            _logger.LogError("Fetching '{Name}' failed: {Message}", name, message);
            return new FetchOutcome { Query = name, ExitCode = ExitCode.NetworkError, Message = message };
        }

        void RecordFailure(TargetEntry? target, string? message)
        {
            if (target is null)
            {
                return;
            }

            target.LastAttempt = _dependencies.TimeProvider.GetUtcNow();
            target.LastError = SecretRedactor.Redact(message, settings.Secrets);
            if (target.State == TargetState.Acquired)
            {
                // A failed refresh must not erase a previously good acquisition.
                return;
            }

            target.State = TargetState.Listed;
        }
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static ExitCode WorstVerificationCode(DiscoveryOutcome discovery)
    {
        if (discovery.Attempts.Count == 0)
        {
            return ExitCode.Unresolved;
        }

        // A gate rejection is the interesting failure; a bare network error is not.
        foreach (var attempt in discovery.Attempts)
        {
            var code = attempt.Result.ToExitCode();
            if (code is ExitCode.VerificationFailed or ExitCode.RequiresAuth or ExitCode.RateLimited)
            {
                return code;
            }
        }

        return discovery.Attempts[^1].Result.ToExitCode();
    }

    /// <summary>Emits manifests, the REST shim, the pre-indexed database and prunes old versions.</summary>
    private async Task EmitSourceAsync(
        RunSettings settings,
        IReadOnlyList<FetchOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var acquired = outcomes
            .Where(o => o.ExitCode == ExitCode.Success && o.InstallerPath is { Length: > 0 } && o.Recipe is not null)
            .ToArray();

        if (acquired.Length == 0)
        {
            return;
        }

        var manifestWriter = new WingetManifestWriter();
        var restWriter = new RestSourceWriter();
        var indexed = new List<IndexedManifest>();

        foreach (var outcome in acquired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recipe = outcome.Recipe!;
            var installer = new WingetInstallerEntry
            {
                Architecture = recipe.Architecture ?? settings.Architecture,
                InstallerType = recipe.InstallerType ?? "exe",
                InstallerUrl = new Uri(outcome.Discovery!.Accepted!.Url.ToString()),
                InstallerSha256 = outcome.Sha256 ?? string.Empty,
                Scope = recipe.Scope ?? settings.Scope,
                NestedInstallerType = recipe.NestedInstallerType,
            };

            var result = await manifestWriter.WriteAsync(
                settings.OutputDirectory,
                new WingetManifestRequest
                {
                    PackageIdentifier = outcome.PackageIdentifier!,
                    PackageVersion = outcome.Version!,
                    Publisher = recipe.Publisher ?? recipe.DisplayName,
                    PackageName = recipe.DisplayName,
                    Installers = [installer],
                },
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                _stderr.WriteLine($"{outcome.Query}: manifest not written — {result.Reason}");
                continue;
            }

            indexed.Add(new IndexedManifest
            {
                PackageIdentifier = outcome.PackageIdentifier!,
                PackageName = recipe.DisplayName,
                Moniker = recipe.ComponentId.ToLowerInvariant(),
                PackageVersion = outcome.Version!,
                RelativeManifestPath = SourceLayout.ManifestRelativeDirectory(
                    outcome.PackageIdentifier!,
                    outcome.Version!),
            });

            await restWriter.WritePackageManifestAsync(
                settings.OutputDirectory,
                outcome.PackageIdentifier!,
                [
                    new RestPackageVersion
                    {
                        PackageVersion = outcome.Version!,
                        DefaultLocale = new RestDefaultLocale
                        {
                            PackageLocale = "en-US",
                            Publisher = recipe.Publisher ?? recipe.DisplayName,
                            PackageName = recipe.DisplayName,
                            License = "Proprietary",
                        },
                        Installers =
                        [
                            new RestInstaller
                            {
                                Architecture = installer.Architecture,
                                InstallerType = installer.InstallerType,
                                InstallerUrl = installer.InstallerUrl.ToString(),
                                InstallerSha256 = installer.InstallerSha256,
                                Scope = installer.Scope,
                                NestedInstallerType = installer.NestedInstallerType,
                            },
                        ],
                    },
                ],
                cancellationToken).ConfigureAwait(false);

            await PruneAsync(settings, outcome.PackageIdentifier!, cancellationToken).ConfigureAwait(false);
        }

        await restWriter.WriteInformationAsync(settings.OutputDirectory, cancellationToken).ConfigureAwait(false);
        await new PreIndexedSourceWriter()
            .BuildAsync(SourceLayout.IndexDatabasePath(settings.OutputDirectory), indexed, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PruneAsync(RunSettings settings, string packageIdentifier, CancellationToken cancellationToken)
    {
        var installerRoot = Path.Combine(settings.ResolveInstallerRoot(), packageIdentifier);
        if (!Directory.Exists(installerRoot))
        {
            return;
        }

        var artifacts = Directory.EnumerateDirectories(installerRoot)
            .Select(directory => new AcquiredVersionArtifacts
            {
                Version = Path.GetFileName(directory),
                ManifestDirectory = SourceLayout.ManifestDirectory(
                    settings.OutputDirectory,
                    packageIdentifier,
                    Path.GetFileName(directory)),
                InstallerPaths = Directory.EnumerateFiles(directory).ToArray(),
            })
            .ToArray();

        var targets = await LoadTargetsAsync(settings, cancellationToken).ConfigureAwait(false);
        var pin = targets.Targets.FirstOrDefault(t => Same(t.Id, packageIdentifier))?.Pin;

        var result = await new RetentionPruner()
            .PruneAsync(artifacts, pin, settings.KeepVersions, cancellationToken)
            .ConfigureAwait(false);

        foreach (var pruned in result.PrunedVersions)
        {
            _logger.LogInformation("Pruned {Package} {Version}.", packageIdentifier, pruned);
        }
    }

    private async Task WriteProvenanceAsync(
        RunSettings settings,
        string query,
        Recipe recipe,
        DiscoveryOutcome discovery,
        string? sha256,
        string? version,
        NameResolution resolution,
        CancellationToken cancellationToken)
    {
        var record = new ProvenanceRecord
        {
            PackageIdentifier = recipe.PackageId,
            ComponentId = recipe.ComponentId,
            FriendlyQuery = query,
            DiscoveryStage = (discovery.Accepted?.Stage ?? DiscoveryStage.LlmAssisted).ToString(),
            Candidates = discovery.Attempts.Select(a => new ProvenanceCandidate
            {
                Url = a.Candidate.Url.ToString(),
                Stage = a.Candidate.Stage.ToString(),
                Rationale = a.Candidate.Rationale,
                VerificationStatus = a.Result.Status.ToString(),
                Reason = a.Result.Reason,
                Accepted = a.Result.Accepted,
            }).ToArray(),
            AcceptedUrl = discovery.Accepted?.Url.ToString(),
            ResolvedVersion = version ?? discovery.Accepted?.Version ?? "unknown",
            SourceVersions = discovery.SourceVersions,
            Sha256 = sha256,
            UpstreamSha256 = discovery.Accepted?.UpstreamSha256,
            Timestamp = _dependencies.TimeProvider.GetUtcNow(),
            ResolutionTier = resolution.Tier,
            Confidence = resolution.Confidence,
            AiMode = settings.AiMode.ToString().ToLowerInvariant(),
            RecipeUsed = discovery.Accepted?.Stage is DiscoveryStage.UserRecipe
                or DiscoveryStage.SeedRecipe
                or DiscoveryStage.AutoRecipe,
            RecipeOrigin = recipe.Origin.ToString(),
        };

        await new ProvenanceWriter(_dependencies.TimeProvider)
            .WriteAsync(settings.OutputDirectory, record, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Re-checks upstream versions for acquired targets without downloading anything.</summary>
    private async Task<ExitCode> RefreshAsync(RunSettings settings, CancellationToken cancellationToken)
    {
        var parsed = CommandLineParser.Parse(["resolve"]);
        var refreshSettings = settings with { RefreshStale = true, DryRun = true };
        var exitCode = await FetchAsync(parsed, refreshSettings, download: false, cancellationToken)
            .ConfigureAwait(false);

        // Anything whose upstream version now exceeds what we hold becomes 'stale'.
        var targets = await LoadTargetsAsync(settings, cancellationToken).ConfigureAwait(false);
        var changed = false;
        foreach (var entry in targets.Targets)
        {
            if (entry.State != TargetState.Acquired ||
                entry.AcquiredVersion is not { Length: > 0 } acquired ||
                entry.AvailableVersion is not { Length: > 0 } available)
            {
                continue;
            }

            if (VersionComparator.Instance.Compare(available, acquired) > 0)
            {
                entry.State = TargetState.Stale;
                changed = true;
                Report($"{entry.Name}: {acquired} -> {available} available");
            }
        }

        if (changed)
        {
            await TargetsFile.SaveAsync(targets, TargetsPath(settings), cancellationToken).ConfigureAwait(false);
        }

        return exitCode;
    }
}
