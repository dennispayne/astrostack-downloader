using System.Text;
using System.Text.Json;
using WgFetch.Core.Configuration;
using WgFetch.Core.Downloads;
using WgFetch.Core.Export;
using WgFetch.Core.Logging;
using WgFetch.Core.Model;
using WgFetch.Core.Output;
using WgFetch.Core.Prereqs;
using WgFetch.Core.Progress;
using WgFetch.Core.Recipes;
using WgFetch.Core.Targets;

namespace WgFetch.Core.Cli;

/// <summary>
/// The verbs that only read or rearrange local state: targets, status, export/import, verify,
/// recipes, prereqs and diagnostics (docs/REQUIREMENTS.md, "CLI surface").
/// </summary>
public sealed partial class CommandRunner
{
    private static readonly string[] ConfigTableHeader = ["SETTING", "VALUE"];

    private async Task<ExitCode> ConfigAsync(
        ParsedCommandLine parsed,
        WgFetchConfig config,
        IReadOnlyList<string> redactionSecrets,
        bool isInteractiveTerminal,
        bool plainRendering,
        CancellationToken cancellationToken)
    {
        if (parsed.SubCommand is not null && parsed.Has("--interactive"))
        {
            _stderr.WriteLine("wgfetch config: --interactive cannot be combined with a subcommand.");
            return ExitCode.UsageError;
        }

        if (parsed.SubCommand is null)
        {
            return await InteractiveConfigAsync(
                config,
                parsed.Value("--config"),
                redactionSecrets,
                isInteractiveTerminal,
                plainRendering,
                cancellationToken).ConfigureAwait(false);
        }

        var path = parsed.Value("--config") ?? ConfigFile.DefaultPath;
        switch (parsed.SubCommand)
        {
            case "list":
                if (parsed.Positional.Count != 0)
                {
                    _stderr.WriteLine("wgfetch config list: does not accept arguments.");
                    return ExitCode.UsageError;
                }

                var rows = new List<string[]> { ConfigTableHeader };
                var settings = ConfigSettings.GetRedactedValues(config, redactionSecrets);
                rows.AddRange(settings.Select(setting => new[] { setting.Name, setting.Value ?? "-" }));
                Report(RenderTable(rows));
                foreach (var setting in settings)
                {
                    Emit(new JsonEvent { Event = "config", Target = setting.Name, Status = "value", Message = setting.Value ?? "-" });
                }
                return ExitCode.Success;

            case "get":
                if (parsed.Positional.Count != 1)
                {
                    _stderr.WriteLine("wgfetch config get: expected exactly one setting name.");
                    return ExitCode.UsageError;
                }

                var value = ConfigSettings.GetRedactedValue(config, parsed.Positional[0], out var getError, redactionSecrets);
                if (getError is not null)
                {
                    _stderr.WriteLine($"wgfetch config get: {SecretRedactor.Redact(getError, redactionSecrets)}");
                    return ExitCode.UsageError;
                }

                Report(value ?? "-");
                Emit(new JsonEvent { Event = "config", Target = parsed.Positional[0], Status = "value", Message = value ?? "-" });
                return ExitCode.Success;

            case "set":
                if (parsed.Positional.Count != 2)
                {
                    _stderr.WriteLine("wgfetch config set: expected a setting name and value.");
                    return ExitCode.UsageError;
                }

                string? setError = null;
                var setRedactionSecrets = IsConfigCredentialName(parsed.Positional[0])
                    ? IncludeSecret(redactionSecrets, parsed.Positional[1])
                    : redactionSecrets;
                var (updated, setInvalidConfig) = await TryUpdateConfigAsync(
                    path,
                    current =>
                    {
                        var success = ConfigSettings.TrySet(
                            current,
                            parsed.Positional[0],
                            parsed.Positional[1],
                            out var latest,
                            out setError);
                        return success ? latest : null;
                    },
                    setRedactionSecrets,
                    cancellationToken).ConfigureAwait(false);
                if (setInvalidConfig is not null)
                {
                    _stderr.WriteLine($"wgfetch config set: {setInvalidConfig}");
                    return ExitCode.ConfigurationError;
                }
                if (updated is null)
                {
                    _stderr.WriteLine($"wgfetch config set: {SecretRedactor.Redact(setError, setRedactionSecrets)}");
                    return ExitCode.UsageError;
                }

                Report($"{parsed.Positional[0]}: saved");
                Emit(new JsonEvent { Event = "config", Target = parsed.Positional[0], Status = "saved" });
                return ExitCode.Success;

            case "unset":
                if (parsed.Positional.Count != 1)
                {
                    _stderr.WriteLine("wgfetch config unset: expected exactly one setting name.");
                    return ExitCode.UsageError;
                }

                string? unsetError = null;
                var (without, unsetInvalidConfig) = await TryUpdateConfigAsync(
                    path,
                    current =>
                    {
                        var success = ConfigSettings.TryUnset(
                            current,
                            parsed.Positional[0],
                            out var latest,
                            out unsetError);
                        return success ? latest : null;
                    },
                    redactionSecrets,
                    cancellationToken).ConfigureAwait(false);
                if (unsetInvalidConfig is not null)
                {
                    _stderr.WriteLine($"wgfetch config unset: {unsetInvalidConfig}");
                    return ExitCode.ConfigurationError;
                }
                if (without is null)
                {
                    _stderr.WriteLine($"wgfetch config unset: {SecretRedactor.Redact(unsetError, redactionSecrets)}");
                    return ExitCode.UsageError;
                }

                Report($"{parsed.Positional[0]}: unset");
                Emit(new JsonEvent { Event = "config", Target = parsed.Positional[0], Status = "unset" });
                return ExitCode.Success;

            default:
                _stderr.WriteLine(
                    $"wgfetch config: unknown subcommand '{SecretRedactor.Redact(parsed.SubCommand, redactionSecrets)}' (expected set|get|list|unset).");
                return ExitCode.UsageError;
        }
    }

    private static bool IsConfigCredentialName(string name)
    {
        var normalized = name.Replace("-", string.Empty, StringComparison.Ordinal);
        return SecretSettingNames.Any(setting => string.Equals(
            setting.Replace("-", string.Empty, StringComparison.Ordinal),
            normalized,
            StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(WgFetchConfig? Updated, string? InvalidConfigMessage)> TryUpdateConfigAsync(
        string path,
        Func<WgFetchConfig, WgFetchConfig?> update,
        IEnumerable<string> redactionSecrets,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await ConfigFile.TryUpdateAsync(path, update, cancellationToken).ConfigureAwait(false);
            return (updated, null);
        }
        catch (InvalidDataException exception)
        {
            return (null, SecretRedactor.Redact(exception.Message, redactionSecrets));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, SecretRedactor.Redact($"unable to update config file '{path}': {exception.Message}", redactionSecrets));
        }
    }

    private async Task<TargetsDocument> LoadTargetsAsync(RunSettings settings, CancellationToken cancellationToken)
    {
        var path = TargetsPath(settings);
        return File.Exists(path)
            ? await TargetsFile.LoadAsync(path, cancellationToken).ConfigureAwait(false)
            : new TargetsDocument();
    }

    private async Task<ExitCode> AddAsync(
        ParsedCommandLine parsed,
        RunSettings settings,
        CancellationToken cancellationToken)
    {
        var names = await CollectNamesAsync(parsed, settings, cancellationToken).ConfigureAwait(false);
        if (names.Count == 0)
        {
            _stderr.WriteLine("wgfetch add: no names given. Pass names positionally or with --from-file.");
            return ExitCode.UsageError;
        }

        var document = await LoadTargetsAsync(settings, cancellationToken).ConfigureAwait(false);
        foreach (var name in names)
        {
            // Add is idempotent: an existing entry keeps its state and acquired version.
            var entry = document.Add(name);
            Report($"{entry.Name}: {entry.State.ToYamlString()}");
            Emit(new JsonEvent { Event = "target.added", Target = entry.Name, Status = entry.State.ToYamlString() });
        }

        await TargetsFile.SaveAsync(document, TargetsPath(settings), cancellationToken).ConfigureAwait(false);

        // `add` downloads nothing; --resolve additionally records the resolved ID and allowlist.
        return parsed.Has("--resolve")
            ? await FetchAsync(parsed, settings, download: false, cancellationToken).ConfigureAwait(false)
            : ExitCode.Success;
    }

    private async Task<ExitCode> RemoveAsync(
        ParsedCommandLine parsed,
        RunSettings settings,
        CancellationToken cancellationToken)
    {
        if (parsed.Positional.Count == 0)
        {
            _stderr.WriteLine("wgfetch remove: no names given.");
            return ExitCode.UsageError;
        }

        var document = await LoadTargetsAsync(settings, cancellationToken).ConfigureAwait(false);
        var removedAny = false;
        foreach (var name in parsed.Positional)
        {
            if (document.Remove(name))
            {
                removedAny = true;
                Report($"{name}: removed");
                Emit(new JsonEvent { Event = "target.removed", Target = name });
            }
            else
            {
                _stderr.WriteLine($"wgfetch remove: '{name}' is not in targets.yaml.");
            }
        }

        if (removedAny)
        {
            await TargetsFile.SaveAsync(document, TargetsPath(settings), cancellationToken).ConfigureAwait(false);
        }

        return ExitCode.Success;
    }

    /// <summary>
    /// One row per target. A populated-but-unacquired repo is valid and expected, so this never exits
    /// nonzero merely because targets remain unacquired.
    /// </summary>
    private async Task<ExitCode> StatusAsync(RunSettings settings, CancellationToken cancellationToken)
    {
        var document = await LoadTargetsAsync(settings, cancellationToken).ConfigureAwait(false);

        if (document.Targets.Count == 0)
        {
            Report("No targets. Add some with 'wgfetch add <name>...'.");
            return ExitCode.Success;
        }

        var rows = new List<string[]>();
        rows.Add(["NAME", "STATE", "ACQUIRED", "AVAILABLE", "LAST ATTEMPT", "LAST ERROR"]);

        foreach (var entry in document.Targets.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            rows.Add(
            [
                entry.Name,
                entry.State.ToYamlString(),
                entry.AcquiredVersion ?? "-",
                entry.AvailableVersion ?? "-",
                entry.LastAttempt?.UtcDateTime.ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                SecretRedactor.Redact(entry.LastError) is { Length: > 0 } error ? error : "-",
            ]);

            Emit(new JsonEvent
            {
                Event = "target.status",
                Target = entry.Name,
                Status = entry.State.ToYamlString(),
                Version = entry.AcquiredVersion,
                Message = entry.LastError,
            });
        }

        Report(RenderTable(rows));
        return ExitCode.Success;
    }

    private async Task<ExitCode> ListAsync(RunSettings settings, CancellationToken cancellationToken)
    {
        var provenance = await new ProvenanceWriter(_dependencies.TimeProvider)
            .ReadAllAsync(settings.OutputDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (provenance.Count == 0)
        {
            Report("No acquired packages.");
            return ExitCode.Success;
        }

        var rows = new List<string[]>();
        rows.Add(["PACKAGE", "VERSION", "SHA256", "STAGE"]);
        foreach (var record in provenance.OrderBy(r => r.PackageIdentifier, StringComparer.Ordinal))
        {
            rows.Add([record.PackageIdentifier, record.ResolvedVersion, record.Sha256 ?? "-", record.DiscoveryStage]);
            Emit(new JsonEvent
            {
                Event = "package.listed",
                Target = record.PackageIdentifier,
                Version = record.ResolvedVersion,
                Sha256 = record.Sha256,
                Stage = record.DiscoveryStage,
            });
        }

        Report(RenderTable(rows));
        return ExitCode.Success;
    }

    /// <summary>Re-hashes every acquired artifact against <c>provenance.json</c>.</summary>
    private async Task<ExitCode> VerifyAsync(RunSettings settings, CancellationToken cancellationToken)
    {
        var provenance = await new ProvenanceWriter(_dependencies.TimeProvider)
            .ReadAllAsync(settings.OutputDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (provenance.Count == 0)
        {
            Report("No provenance records to verify.");
            return ExitCode.Success;
        }

        var installerRoot = settings.ResolveInstallerRoot();
        var failures = 0;

        foreach (var record in provenance.OrderBy(r => r.PackageIdentifier, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (record.Sha256 is not { Length: > 0 } expected)
            {
                continue;
            }

            var directory = Path.Combine(installerRoot, record.PackageIdentifier, record.ResolvedVersion);
            var file = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault()
                : null;

            if (file is null)
            {
                failures++;
                _stderr.WriteLine($"{record.PackageIdentifier} {record.ResolvedVersion}: installer missing under {directory}");
                Emit(new JsonEvent
                {
                    Event = "artifact.verified",
                    Target = record.PackageIdentifier,
                    Version = record.ResolvedVersion,
                    Status = "missing",
                });
                continue;
            }

            var actual = await InstallerDownloader.ComputeSha256Async(file, cancellationToken).ConfigureAwait(false);
            var matched = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            if (!matched)
            {
                failures++;
                _stderr.WriteLine(
                    $"{record.PackageIdentifier} {record.ResolvedVersion}: SHA256 {actual} does not match recorded {expected}");
            }
            else
            {
                Report($"{record.PackageIdentifier} {record.ResolvedVersion}: ok");
            }

            Emit(new JsonEvent
            {
                Event = "artifact.verified",
                Target = record.PackageIdentifier,
                Version = record.ResolvedVersion,
                Sha256 = actual,
                Status = matched ? "ok" : "mismatch",
            });
        }

        return failures == 0 ? ExitCode.Success : ExitCode.HashMismatch;
    }

    private async Task<ExitCode> ExportAsync(
        ParsedCommandLine parsed,
        RunSettings settings,
        CancellationToken cancellationToken)
    {
        var format = settings.Format ?? "astrostack-dsc";
        var outDirectory = settings.OutDirectory ?? Path.Combine(settings.OutputDirectory, "export");
        var document = await LoadTargetsAsync(settings, cancellationToken).ConfigureAwait(false);

        switch (format.Trim().ToLowerInvariant())
        {
            case "applist":
                var listPath = Path.Combine(outDirectory, "applist.yaml");
                await new AppListExporter().ExportAsync(document, listPath, cancellationToken).ConfigureAwait(false);
                Report($"Wrote {listPath}");
                return ExitCode.Success;

            case "astrostack-dsc":
                var provenance = await new ProvenanceWriter(_dependencies.TimeProvider)
                    .ReadAllAsync(settings.OutputDirectory, cancellationToken)
                    .ConfigureAwait(false);

                var items = BuildExportItems(document, provenance, settings);
                var count = await new AstroStackDscExporter()
                    .ExportAsync(items, outDirectory, cancellationToken)
                    .ConfigureAwait(false);

                Report($"Wrote {count} patch(es) to {Path.Combine(outDirectory, "patches")}");
                Emit(new JsonEvent { Event = "export.written", Status = format, Message = outDirectory });
                return ExitCode.Success;

            default:
                _stderr.WriteLine($"wgfetch export: unknown --format '{format}' (expected astrostack-dsc or applist).");
                return ExitCode.UsageError;
        }
    }

    private static IReadOnlyList<AstroStackDscExportItem> BuildExportItems(
        TargetsDocument document,
        IReadOnlyList<ProvenanceRecord> provenance,
        RunSettings settings)
    {
        var byPackage = provenance
            .GroupBy(r => r.PackageIdentifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var items = new List<AstroStackDscExportItem>();
        foreach (var entry in document.Targets)
        {
            var componentId = entry.ComponentId ?? entry.Name;
            ProvenanceRecord? record = null;
            if (entry.Id is { Length: > 0 } id)
            {
                byPackage.TryGetValue(id, out record);
            }

            string? relativePath = null;
            if (record is not null && entry.Id is { Length: > 0 } packageId && record.AcceptedUrl is { Length: > 0 } url)
            {
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                relativePath = $"{SourceLayout.InstallersDirectoryName}/{packageId}/{record.ResolvedVersion}/{fileName}";
            }

            items.Add(new AstroStackDscExportItem
            {
                ComponentId = componentId,
                DownloadUrl = record?.AcceptedUrl,
                DownloadFileName = relativePath is null ? null : Path.GetFileName(relativePath),
                Sha256 = record?.Sha256,
                Verified = record is not null ? record.Sha256 is { Length: > 0 } : entry.State == TargetState.Acquired,
                AvailableVersion = entry.AvailableVersion ?? record?.ResolvedVersion,
                InstallerRelativePath = relativePath,
                PreviousAvailableVersion = entry.AcquiredVersion,
            });
        }

        _ = settings;
        return items;
    }

    private async Task<ExitCode> ImportAsync(
        ParsedCommandLine parsed,
        RunSettings settings,
        CancellationToken cancellationToken)
    {
        var format = settings.Format ?? "astrostack-dsc";
        if (!string.Equals(format, "astrostack-dsc", StringComparison.OrdinalIgnoreCase))
        {
            _stderr.WriteLine($"wgfetch import: unknown --format '{format}' (expected astrostack-dsc).");
            return ExitCode.UsageError;
        }

        if (parsed.Positional.Count != 1)
        {
            _stderr.WriteLine("wgfetch import: expected exactly one directory of astrostack-dsc components.");
            return ExitCode.UsageError;
        }

        var source = parsed.Positional[0];
        if (!Directory.Exists(source))
        {
            _stderr.WriteLine($"wgfetch import: '{source}' does not exist.");
            return ExitCode.UsageError;
        }

        var imported = await new AstroStackDscImporter().ImportAsync(source, cancellationToken).ConfigureAwait(false);
        var existing = await LoadTargetsAsync(settings, cancellationToken).ConfigureAwait(false);

        // Merging never downgrades local knowledge: an already-acquired entry keeps its state.
        foreach (var entry in imported.Targets)
        {
            if (existing.Find(entry.Name) is { } present)
            {
                present.ComponentId ??= entry.ComponentId;
                present.Id ??= entry.Id;
                continue;
            }

            existing.Targets.Add(entry);
        }

        await TargetsFile.SaveAsync(existing, TargetsPath(settings), cancellationToken).ConfigureAwait(false);
        Report($"Imported {imported.Targets.Count} component(s) into {TargetsPath(settings)}");
        return ExitCode.Success;
    }

    private async Task<ExitCode> PrereqsAsync(
        ParsedCommandLine parsed,
        RunSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.Equals(parsed.SubCommand, "status", StringComparison.Ordinal))
        {
            var statuses = await PrereqInstaller.StatusAsync(settings.ModelsRoot, cancellationToken).ConfigureAwait(false);
            var ready = true;

            foreach (var model in statuses)
            {
                Report($"{model.ModelId} ({model.DisplayName}) — {(model.Ready ? "ready" : "not ready")} at {model.Directory}");
                foreach (var asset in model.Assets)
                {
                    Report($"  {asset.RelativePath}: {asset.State}{(asset.SizeBytes is { } size ? $" ({size} bytes)" : string.Empty)}");
                }

                ready &= model.Ready;
                Emit(new JsonEvent
                {
                    Event = "prereq.status",
                    Target = model.ModelId,
                    Status = model.Ready ? "ready" : "missing",
                });
            }

            return ready ? ExitCode.Success : ExitCode.MissingPrerequisite;
        }

        var installer = new PrereqInstaller(CreateHttpGateway(), _logger);
        var result = await installer
            .InstallAsync(settings.ModelsRoot, parsed.Has("--include-llm"), settings.DryRun, cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in result.Messages)
        {
            if (result.Success)
            {
                Report(message);
            }
            else
            {
                _stderr.WriteLine(message);
            }
        }

        Emit(new JsonEvent
        {
            Event = "prereq.install",
            Status = result.Success ? "ok" : "failed",
            ExitCode = (int)result.ExitCode,
        });

        return result.ExitCode;
    }

    private async Task<ExitCode> RecipesAsync(
        ParsedCommandLine parsed,
        RunSettings settings,
        CancellationToken cancellationToken)
    {
        var store = LoadRecipes(settings);

        switch (parsed.SubCommand)
        {
            case "list":
                var recipeRows = new List<string[]>();
                recipeRows.Add(["COMPONENT", "PACKAGE", "ORIGIN", "SOURCE"]);
                foreach (var recipe in store.All.OrderBy(r => r.ComponentId, StringComparer.Ordinal))
                {
                    recipeRows.Add([recipe.ComponentId, recipe.PackageId, recipe.Origin.ToString(), recipe.SourceKind.ToString()]);
                    Emit(new JsonEvent { Event = "recipe.listed", Target = recipe.ComponentId, Stage = recipe.Origin.ToString() });
                }

                Report(RenderTable(recipeRows));
                return ExitCode.Success;

            case "show":
                if (parsed.Positional.Count != 1)
                {
                    _stderr.WriteLine("wgfetch recipes show: expected exactly one recipe name.");
                    return ExitCode.UsageError;
                }

                var found = store.Find(parsed.Positional[0]);
                if (found is null)
                {
                    _stderr.WriteLine($"wgfetch recipes show: no recipe matches '{parsed.Positional[0]}'.");
                    return ExitCode.Unresolved;
                }

                Report(JsonSerializer.Serialize(found, RecipeJsonContext.Default.Recipe));
                return ExitCode.Success;

            case "export":
                var directory = settings.OutDirectory ?? Path.Combine(settings.OutputDirectory, "recipes");
                Directory.CreateDirectory(directory);
                foreach (var recipe in store.All)
                {
                    var path = Path.Combine(directory, $"{recipe.ComponentId}.json");
                    await File.WriteAllTextAsync(
                        path,
                        JsonSerializer.Serialize(recipe, RecipeJsonContext.Default.Recipe),
                        new UTF8Encoding(false),
                        cancellationToken).ConfigureAwait(false);
                }

                Report($"Exported {store.All.Count} recipe(s) to {directory}");
                return ExitCode.Success;

            case "validate":
                var invalid = 0;
                foreach (var recipe in store.All)
                {
                    var problem = ValidateRecipe(recipe);
                    if (problem is null)
                    {
                        continue;
                    }

                    invalid++;
                    _stderr.WriteLine($"{recipe.ComponentId}: {problem}");
                }

                if (invalid == 0)
                {
                    Report($"All {store.All.Count} recipe(s) valid.");
                }

                return invalid == 0 ? ExitCode.Success : ExitCode.VerificationFailed;

            default:
                return ExitCode.UsageError;
        }
    }

    private static string? ValidateRecipe(Recipe recipe)
    {
        if (recipe.SchemaVersion != Recipe.CurrentSchemaVersion)
        {
            return $"unsupported schema version {recipe.SchemaVersion}";
        }

        if (recipe.RequiresAuth)
        {
            // Auth-walled recipes are recognised-but-blocked and legitimately carry no URL.
            return recipe.AuthReason is { Length: > 0 } ? null : "requiresAuth without an authReason";
        }

        if (recipe.Allowlist.Count == 0)
        {
            return "no allowlisted domain";
        }

        return recipe.SourceKind switch
        {
            RecipeSourceKind.GitHubRelease when string.IsNullOrWhiteSpace(recipe.Repository) => "githubRelease without a repository",
            RecipeSourceKind.DirectUrl when string.IsNullOrWhiteSpace(recipe.DirectUrl) => "directUrl without a url",
            RecipeSourceKind.DownloadPage when string.IsNullOrWhiteSpace(recipe.DownloadPageUrl) => "downloadPage without a page url",
            _ => null,
        };
    }

    /// <summary>Writes a redacted support bundle (docs/REQUIREMENTS.md, "Observability and diagnostics").</summary>
    private async Task<ExitCode> DiagnosticsAsync(
        ParsedCommandLine parsed,
        RunSettings settings,
        WgFetchConfig config,
        CancellationToken cancellationToken)
    {
        var directory = settings.OutDirectory ?? Path.Combine(settings.CacheDirectory, "diagnostics");
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "config.json"),
            JsonSerializer.Serialize(config.Redacted(settings.Secrets), ConfigJsonContext.Default.WgFetchConfig),
            cancellationToken).ConfigureAwait(false);

        var models = await PrereqInstaller.StatusAsync(settings.ModelsRoot, cancellationToken).ConfigureAwait(false);
        var report = new StringBuilder();
        report.AppendLine("wgfetch diagnostics");
        report.AppendLine($"version: {Version}");
        report.AppendLine($"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        report.AppendLine($"os: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        report.AppendLine($"architecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        report.AppendLine($"output: {settings.OutputDirectory}");
        report.AppendLine($"cache: {settings.CacheDirectory}");
        report.AppendLine($"models: {settings.ModelsRoot}");
        report.AppendLine($"aiMode: {settings.AiMode}");
        report.AppendLine($"searchProvider: {settings.SearchProvider}");
        report.AppendLine();
        report.AppendLine("models:");
        foreach (var model in models)
        {
            report.AppendLine($"  {model.ModelId}: {(model.Ready ? "ready" : "not ready")}");
            foreach (var asset in model.Assets)
            {
                report.AppendLine($"    {asset.RelativePath}: {asset.State} sha256={asset.Sha256 ?? "-"}");
            }
        }

        report.AppendLine();
        report.AppendLine(_timings.Summarize());

        // Everything in the bundle goes through the redactor, including the free-text report.
        await File.WriteAllTextAsync(
            Path.Combine(directory, "report.txt"),
            SecretRedactor.Redact(report.ToString(), settings.Secrets),
            cancellationToken).ConfigureAwait(false);

        var provenancePath = SourceLayout.ProvenancePath(settings.OutputDirectory);
        if (File.Exists(provenancePath))
        {
            var provenance = await File.ReadAllTextAsync(provenancePath, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "provenance.json"),
                SecretRedactor.Redact(provenance, settings.Secrets),
                cancellationToken).ConfigureAwait(false);
        }

        if (settings.LogFile is { Length: > 0 } logFile && File.Exists(logFile))
        {
            var log = await File.ReadAllTextAsync(logFile, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "run.log"),
                SecretRedactor.Redact(log, settings.Secrets),
                cancellationToken).ConfigureAwait(false);
        }

        Report($"Wrote a redacted diagnostics bundle to {directory}");
        Emit(new JsonEvent { Event = "diagnostics.written", Message = directory });
        _ = parsed;
        return ExitCode.Success;
    }

    private async Task<IReadOnlyList<string>> CollectNamesAsync(
        ParsedCommandLine parsed,
        RunSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.FromFile is not { Length: > 0 } path)
        {
            return parsed.Positional;
        }

        if (!File.Exists(path))
        {
            _stderr.WriteLine($"wgfetch: --from-file '{path}' does not exist.");
            return [];
        }

        var document = await TargetsFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return document.Targets.Select(t => t.Name).ToArray();
    }

    private static string RenderTable(IReadOnlyList<string[]> rows)
    {
        var widths = new int[rows[0].Length];
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                builder.Append(i == row.Length - 1 ? row[i] : row[i].PadRight(widths[i] + 2));
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }
}
