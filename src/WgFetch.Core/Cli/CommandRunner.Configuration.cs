using Spectre.Console;
using WgFetch.Core.Configuration;
using WgFetch.Core.Prereqs;
using WgFetch.Core.Progress;

namespace WgFetch.Core.Cli;

public sealed partial class CommandRunner
{
    private const string ModelPrerequisitesChoice = "Model prerequisites";
    private const string ExitChoice = "Exit";
    private const string InstallAllModelsChoice = "Install all pinned models";
    private const string ChangeModelsRootChoice = "Change models root";
    private const string BackChoice = "Back";
    private const string UseThisDirectoryChoice = "[Use this directory]";
    private const string EnterPathManuallyChoice = "[Enter path manually]";
    private const string ParentDirectoryChoice = "[.. parent directory]";
    private const string CancelPathChoice = "[Cancel]";

    private static readonly HashSet<string> SecretSettingNames = new(StringComparer.Ordinal)
    {
        "aiKey", "searchKey", "githubToken",
    };

    private async Task<ExitCode> InteractiveConfigAsync(
        WgFetchConfig config,
        string? configuredPath,
        IReadOnlyList<string> redactionSecrets,
        bool isInteractiveTerminal,
        bool plainRendering,
        CancellationToken cancellationToken)
    {
        if (!isInteractiveTerminal)
        {
            _stderr.WriteLine("wgfetch config: interactive mode requires an attached terminal.");
            return ExitCode.Ambiguous;
        }

        var path = configuredPath ?? ConfigFile.DefaultPath;
        var fallbackDirectory = Path.GetDirectoryName(path) is { Length: > 0 } configDirectory
            ? configDirectory
            : Environment.CurrentDirectory;
        var console = _dependencies.InteractiveConsole ?? CreateInteractiveConsole(_stdout, plainRendering);
        var current = config;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentRedactionSecrets = IncludeConfigSecrets(redactionSecrets, current);
            RenderConfig(console, current, currentRedactionSecrets);
            var choice = console.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a setting to edit, or manage model prerequisites")
                    .UseConverter(FormatMenuChoice)
                    .AddChoices([.. ConfigSettings.Names, ModelPrerequisitesChoice, ExitChoice]));
            if (choice == ExitChoice)
            {
                return ExitCode.Success;
            }

            if (choice == ModelPrerequisitesChoice)
            {
                var prereqExit = await ManagePrerequisitesAsync(console, current, path, currentRedactionSecrets, cancellationToken).ConfigureAwait(false);
                if (prereqExit != ExitCode.Success)
                {
                    return prereqExit;
                }

                current = await ConfigFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                continue;
            }

            string? value;
            if (ConfigSettings.PathNames.Contains(choice))
            {
                value = PickDirectory(console, GetExistingPathValue(current, choice, fallbackDirectory), currentRedactionSecrets);
                if (value is null)
                {
                    continue;
                }
            }
            else
            {
                var prompt = new TextPrompt<string>($"New value for {choice} (leave blank to cancel)").AllowEmpty();
                if (SecretSettingNames.Contains(choice))
                {
                    prompt.Secret();
                }

                value = console.Prompt(prompt);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }
            }

            if (ConfigSettings.PathNames.Contains(choice))
            {
                try
                {
                    Directory.CreateDirectory(value);
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    var display = Logging.SecretRedactor.Redact(value, currentRedactionSecrets);
                    console.MarkupLine($"[red]Cannot create directory:[/] {Markup.Escape(display)}");
                    continue;
                }
            }

            string? updateError = null;
            var updateRedactionSecrets = SecretSettingNames.Contains(choice)
                ? IncludeSecret(currentRedactionSecrets, value)
                : currentRedactionSecrets;
            var (persisted, invalidConfig) = await TryUpdateConfigAsync(
                path,
                latest =>
                {
                    updateError = null;
                    if (!ConfigSettings.TrySet(latest, choice, value, out var merged, out updateError))
                    {
                        return null;
                    }

                    return merged;
                },
                updateRedactionSecrets,
                cancellationToken).ConfigureAwait(false);
            if (invalidConfig is not null)
            {
                console.MarkupLine($"[red]{Markup.Escape(invalidConfig)}[/]");
                continue;
            }
            if (persisted is null)
            {
                console.MarkupLine($"[red]{Markup.Escape(updateError ?? $"Failed to save '{choice}'.")}[/]");
                continue;
            }

            current = persisted;
            console.MarkupLine("[green]Saved.[/]");
        }
    }

    private static IReadOnlyList<string> IncludeConfigSecrets(IEnumerable<string> redactionSecrets, WgFetchConfig config) =>
        redactionSecrets
            .Concat([config.AiKey, config.SearchKey, config.GithubToken])
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .Select(secret => secret!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> IncludeSecret(IEnumerable<string> redactionSecrets, string secret) =>
        redactionSecrets
            .Append(secret)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    internal static IAnsiConsole CreateInteractiveConsole(TextWriter output, bool plainRendering) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = plainRendering ? AnsiSupport.No : AnsiSupport.Detect,
            ColorSystem = plainRendering ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            // Plain controls rendering only; the attached terminal must still accept prompts.
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(output),
        });

    private static void RenderConfig(IAnsiConsole console, WgFetchConfig config, IEnumerable<string> redactionSecrets)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("wgfetch configuration");
        table.AddColumn("Setting");
        table.AddColumn("Persisted value");
        table.AddColumn("What it does");
        foreach (var setting in ConfigSettings.GetRedactedValues(config, redactionSecrets))
        {
            var hint = ConfigSettings.Hints.TryGetValue(setting.Name, out var text) ? text : string.Empty;
            table.AddRow(setting.Name, setting.Value is null ? "-" : Markup.Escape(setting.Value), Markup.Escape(hint));
        }

        console.Write(table);
    }

    /// <summary>Appends each setting's hint to its menu entry so the picker doubles as inline help.</summary>
    private static string FormatMenuChoice(string choice) =>
        ConfigSettings.Hints.TryGetValue(choice, out var hint)
            ? Markup.Escape($"{choice} — {hint}")
            : Markup.Escape(choice);

    /// <summary>The directory a path-picker should open in: the persisted value, or a deterministic fallback.</summary>
    private static string GetExistingPathValue(WgFetchConfig config, string name, string fallbackDirectory) => name switch
    {
        "outputDirectory" => string.IsNullOrWhiteSpace(config.OutputDirectory) ? fallbackDirectory : config.OutputDirectory,
        "cacheDirectory" => string.IsNullOrWhiteSpace(config.CacheDirectory) ? fallbackDirectory : config.CacheDirectory,
        "modelsRoot" => string.IsNullOrWhiteSpace(config.ModelsRoot) ? fallbackDirectory : config.ModelsRoot,
        _ => fallbackDirectory,
    };

    /// <summary>
    /// A basic directory browser for path-valued settings: step into subdirectories, go up, jump to a
    /// typed path, or cancel. Listing failures (permissions, unmounted drives) degrade to an empty
    /// listing instead of crashing the interactive session. Returns <see langword="null"/> only when
    /// the user cancels; a manually typed path is returned as-is (it need not exist yet — creating it
    /// is handled by the caller) rather than resolved, so a brand-new directory name is preserved.
    /// </summary>
    private static string? PickDirectory(IAnsiConsole console, string startingPath, IReadOnlyCollection<string> redactionSecrets)
    {
        var current = ResolveExistingDirectoryOrCurrent(startingPath);
        while (true)
        {
            var display = Logging.SecretRedactor.Redact(current, redactionSecrets);
            var choices = new List<string>();
            var parent = SafeGetParent(current);
            if (parent is not null)
            {
                choices.Add(ParentDirectoryChoice);
            }

            choices.AddRange(SafeListSubdirectories(current, console, redactionSecrets));
            choices.Add(UseThisDirectoryChoice);
            choices.Add(EnterPathManuallyChoice);
            choices.Add(CancelPathChoice);

            var choice = console.Prompt(
                new SelectionPrompt<string>()
                    .Title($"Browse for a directory — currently [grey]{Markup.Escape(display)}[/]")
                    .UseConverter(Markup.Escape)
                    .PageSize(15)
                    .AddChoices(choices));

            switch (choice)
            {
                case UseThisDirectoryChoice:
                    return current;
                case CancelPathChoice:
                    return null;
                case ParentDirectoryChoice:
                    current = parent!;
                    continue;
                case EnterPathManuallyChoice:
                    var manual = console.Prompt(new TextPrompt<string>("Path (leave blank to cancel)").AllowEmpty());
                    if (string.IsNullOrWhiteSpace(manual))
                    {
                        continue;
                    }

                    return manual;
                default:
                    current = Path.Combine(current, choice);
                    continue;
            }
        }
    }

    private static string? SafeGetParent(string path)
    {
        try
        {
            return Directory.GetParent(path)?.FullName;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SafeListSubdirectories(string path, IAnsiConsole console, IReadOnlyCollection<string> redactionSecrets)
    {
        try
        {
            return Directory.GetDirectories(path)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var display = Logging.SecretRedactor.Redact(exception.Message, redactionSecrets);
            console.MarkupLine($"[yellow]Cannot list subdirectories:[/] {Markup.Escape(display)}");
            return [];
        }
    }

    /// <summary>
    /// Resolves a candidate to an existing directory to browse from, walking up to the nearest
    /// existing ancestor when the candidate itself doesn't exist yet (a not-yet-created path, for
    /// example). Falls back to the current directory only if nothing in the chain can be resolved.
    /// </summary>
    private static string ResolveExistingDirectoryOrCurrent(string candidate)
    {
        try
        {
            var probe = Path.GetFullPath(string.IsNullOrWhiteSpace(candidate) ? "." : candidate);
            for (var depth = 0; depth < 64 && !string.IsNullOrEmpty(probe); depth++)
            {
                if (Directory.Exists(probe))
                {
                    return probe;
                }

                probe = Path.GetDirectoryName(probe);
            }

            return Environment.CurrentDirectory;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Environment.CurrentDirectory;
        }
    }

    /// <summary>
    /// Resolves the persisted <c>modelsRoot</c> to an absolute path without ever throwing: a config
    /// file is arbitrary JSON, and an invalid value (for example an embedded NUL, or a path that
    /// names an existing regular file) must send the user back to a repairable menu instead of
    /// crashing the whole interactive session (installing would otherwise call
    /// <c>Directory.CreateDirectory</c> on that file path and throw an unhandled <see cref="IOException"/>).
    /// </summary>
    private static bool TryResolveModelsRoot(
        WgFetchConfig config,
        IAnsiConsole console,
        IEnumerable<string> redactionSecrets,
        out string modelsRoot)
    {
        var candidate = string.IsNullOrWhiteSpace(config.ModelsRoot) ? WgFetchPaths.ModelsDirectory : config.ModelsRoot;
        try
        {
            var resolved = Path.GetFullPath(candidate);
            if (File.Exists(resolved))
            {
                var fileDisplay = Logging.SecretRedactor.Redact(candidate, redactionSecrets);
                console.MarkupLine(
                    $"[red]The persisted modelsRoot names an existing file:[/] {Markup.Escape(fileDisplay)}. " +
                    "Edit or unset the 'modelsRoot' setting to continue.");
                modelsRoot = string.Empty;
                return false;
            }

            modelsRoot = resolved;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var display = Logging.SecretRedactor.Redact(candidate, redactionSecrets);
            console.MarkupLine(
                $"[red]The persisted modelsRoot is invalid:[/] {Markup.Escape(display)}. " +
                "Edit or unset the 'modelsRoot' setting to continue.");
            modelsRoot = string.Empty;
            return false;
        }
    }

    private async Task<ExitCode> ManagePrerequisitesAsync(
        IAnsiConsole console,
        WgFetchConfig config,
        string configPath,
        IReadOnlyList<string> redactionSecrets,
        CancellationToken cancellationToken)
    {
        if (!TryResolveModelsRoot(config, console, redactionSecrets, out var modelsRoot))
        {
            return ExitCode.Success;
        }

        var models = _dependencies.PrereqModels ?? PinnedModels.All;
        IReadOnlyList<PrereqModelStatus> statuses = [];
        try
        {
            await console.Status()
                .StartAsync("Checking pinned model files...", async _ =>
                {
                    statuses = await PrereqInstaller.StatusAsync(modelsRoot, models, cancellationToken).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            var display = Logging.SecretRedactor.Redact(exception.Message, redactionSecrets);
            console.MarkupLine($"[red]Unable to check pinned model files:[/] {Markup.Escape(display)}");
            return ExitCode.MissingPrerequisite;
        }
        var modelsRootDisplay = Logging.SecretRedactor.Redact(modelsRoot, redactionSecrets);
        var table = new Table().Border(TableBorder.Rounded).Title($"Model prerequisites ({Markup.Escape(modelsRootDisplay)})");
        table.AddColumn("Model");
        table.AddColumn("Status");
        foreach (var model in statuses)
        {
            var status = model.Ready ? "[green]present[/]"
                : model.Assets.Any(asset => asset.State == PrereqState.HashMismatch) ? "[red]digest mismatch[/]"
                : model.Assets.Any(asset => asset.State == PrereqState.Missing) ? "[yellow]missing[/]"
                : "[yellow]unpinned[/]";
            table.AddRow(model.ModelId, status);
        }

        console.Write(table);
        var installChoices = models.ToDictionary(
            model => $"Install {model.Id}",
            model => model.Id,
            StringComparer.Ordinal);
        var choices = installChoices.Keys
            .Append(InstallAllModelsChoice).Append(ChangeModelsRootChoice).Append(BackChoice).ToArray();
        var choice = console.Prompt(new SelectionPrompt<string>().Title("Model action").AddChoices(choices));
        if (choice == BackChoice)
        {
            return ExitCode.Success;
        }

        if (choice == ChangeModelsRootChoice)
        {
            // When modelsRoot is unset, `modelsRoot` here already resolved to the real machine default
            // (WgFetchPaths.ModelsDirectory), which is not deterministic across environments. Prefer
            // browsing from beside the config file in that case so the picker's starting point stays
            // predictable; once a modelsRoot is actually configured, start from it as expected.
            var startingDirectory = string.IsNullOrWhiteSpace(config.ModelsRoot)
                ? (Path.GetDirectoryName(configPath) is { Length: > 0 } configDirectory ? configDirectory : Environment.CurrentDirectory)
                : modelsRoot;
            var root = PickDirectory(console, startingDirectory, redactionSecrets);
            if (string.IsNullOrWhiteSpace(root))
            {
                return ExitCode.Success;
            }

            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                var display = Logging.SecretRedactor.Redact(root, redactionSecrets);
                console.MarkupLine($"[red]Cannot create directory:[/] {Markup.Escape(display)}");
                return ExitCode.Success;
            }

            string? updateError = null;
            var (persisted, invalidConfig) = await TryUpdateConfigAsync(
                configPath,
                latest =>
                {
                    updateError = null;
                    if (!ConfigSettings.TrySet(latest, "modelsRoot", root, out var merged, out updateError))
                    {
                        return null;
                    }

                    return merged;
                },
                redactionSecrets,
                cancellationToken).ConfigureAwait(false);
            if (invalidConfig is not null)
            {
                console.MarkupLine($"[red]{Markup.Escape(invalidConfig)}[/]");
                return ExitCode.Success;
            }
            if (persisted is null)
            {
                console.MarkupLine($"[red]{Markup.Escape(updateError ?? "Failed to save 'modelsRoot'.")}[/]");
                return ExitCode.UsageError;
            }

            return ExitCode.Success;
        }

        IReadOnlySet<string> selected = choice == InstallAllModelsChoice
            ? models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>([installChoices[choice]], StringComparer.Ordinal);
        PrereqInstallResult? result = null;
        try
        {
            await console.Status()
                .StartAsync("Installing pinned model files...", async _ =>
                {
                    result = await new PrereqInstaller(CreateHttpGateway(), _logger, models)
                        .InstallAsync(modelsRoot, selected, dryRun: false, cancellationToken)
                        .ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            var display = Logging.SecretRedactor.Redact(exception.Message, redactionSecrets);
            console.MarkupLine($"[red]Unable to install pinned model files:[/] {Markup.Escape(display)}");
            return ExitCode.MissingPrerequisite;
        }

        foreach (var message in result!.Messages)
        {
            console.WriteLine(message);
        }

        return result.ExitCode;
    }
}
