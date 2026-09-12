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
        var console = _dependencies.InteractiveConsole ?? CreateInteractiveConsole(_stdout, plainRendering);
        var current = config;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderConfig(console, current, redactionSecrets);
            var choice = console.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a setting to edit, or manage model prerequisites")
                    .AddChoices([.. ConfigSettings.Names, ModelPrerequisitesChoice, ExitChoice]));
            if (choice == ExitChoice)
            {
                return ExitCode.Success;
            }

            if (choice == ModelPrerequisitesChoice)
            {
                var prereqExit = await ManagePrerequisitesAsync(console, current, path, redactionSecrets, cancellationToken).ConfigureAwait(false);
                if (prereqExit != ExitCode.Success)
                {
                    return prereqExit;
                }

                current = await ConfigFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var prompt = new TextPrompt<string>($"New value for {choice} (leave blank to cancel)").AllowEmpty();
            if (SecretSettingNames.Contains(choice))
            {
                prompt.Secret();
            }

            var value = console.Prompt(prompt);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (choice is "outputDirectory" or "cacheDirectory" or "modelsRoot")
            {
                try
                {
                    Directory.CreateDirectory(value);
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    var display = Logging.SecretRedactor.Redact(value, redactionSecrets);
                    console.MarkupLine($"[red]Cannot create directory:[/] {Markup.Escape(display)}");
                    continue;
                }
            }

            string? updateError = null;
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
                redactionSecrets,
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
        foreach (var setting in ConfigSettings.GetRedactedValues(config, redactionSecrets))
        {
            table.AddRow(setting.Name, setting.Value is null ? "-" : Markup.Escape(setting.Value));
        }

        console.Write(table);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
            var root = console.Prompt(new TextPrompt<string>("Models root (leave blank to cancel)").AllowEmpty());
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
