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
        bool isInteractiveTerminal,
        CancellationToken cancellationToken)
    {
        if (!isInteractiveTerminal)
        {
            _stderr.WriteLine("wgfetch config: interactive mode requires an attached terminal.");
            return ExitCode.Ambiguous;
        }

        var path = configuredPath ?? ConfigFile.DefaultPath;
        var console = _dependencies.InteractiveConsole ?? AnsiConsole.Console;
        var current = config;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderConfig(console, current);
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
                var prereqExit = await ManagePrerequisitesAsync(console, current, path, cancellationToken).ConfigureAwait(false);
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
                    console.MarkupLine($"[red]Cannot create directory:[/] {Markup.Escape(value)}");
                    continue;
                }
            }

            if (!ConfigSettings.TrySet(current, choice, value, out var updated, out var error))
            {
                console.MarkupLine($"[red]{Markup.Escape(error!)}[/]");
                continue;
            }

            await ConfigFile.SaveAsync(updated, path, cancellationToken).ConfigureAwait(false);
            current = updated;
            console.MarkupLine("[green]Saved.[/]");
        }
    }

    private static void RenderConfig(IAnsiConsole console, WgFetchConfig config)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("wgfetch configuration");
        table.AddColumn("Setting");
        table.AddColumn("Persisted value");
        foreach (var setting in ConfigSettings.GetRedactedValues(config))
        {
            table.AddRow(setting.Name, setting.Value is null ? "-" : Markup.Escape(setting.Value));
        }

        console.Write(table);
    }

    /// <summary>
    /// Resolves the persisted <c>modelsRoot</c> to an absolute path without ever throwing: a config
    /// file is arbitrary JSON, and an invalid value (for example an embedded NUL) must send the user
    /// back to a repairable menu instead of crashing the whole interactive session.
    /// </summary>
    private static bool TryResolveModelsRoot(WgFetchConfig config, IAnsiConsole console, out string modelsRoot)
    {
        var candidate = string.IsNullOrWhiteSpace(config.ModelsRoot) ? WgFetchPaths.ModelsDirectory : config.ModelsRoot;
        try
        {
            modelsRoot = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var display = Logging.SecretRedactor.Redact(candidate, config.Secrets);
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
        CancellationToken cancellationToken)
    {
        if (!TryResolveModelsRoot(config, console, out var modelsRoot))
        {
            return ExitCode.Success;
        }

        var models = _dependencies.PrereqModels ?? PinnedModels.All;
        var statuses = await PrereqInstaller.StatusAsync(modelsRoot, models, cancellationToken).ConfigureAwait(false);
        var modelsRootDisplay = Logging.SecretRedactor.Redact(modelsRoot, config.Secrets);
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
                console.MarkupLine($"[red]Cannot create directory:[/] {Markup.Escape(root)}");
                return ExitCode.Success;
            }

            if (!ConfigSettings.TrySet(config, "modelsRoot", root, out var updated, out var error))
            {
                console.MarkupLine($"[red]{Markup.Escape(error!)}[/]");
                return ExitCode.UsageError;
            }

            await ConfigFile.SaveAsync(updated, configPath, cancellationToken).ConfigureAwait(false);
            return ExitCode.Success;
        }

        IReadOnlySet<string> selected = choice == InstallAllModelsChoice
            ? models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>([installChoices[choice]], StringComparer.Ordinal);
        PrereqInstallResult? result = null;
        await console.Status()
            .StartAsync("Installing pinned model files...", async _ =>
            {
                result = await new PrereqInstaller(CreateHttpGateway(), _logger, models)
                    .InstallAsync(modelsRoot, selected, dryRun: false, cancellationToken)
                    .ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        foreach (var message in result!.Messages)
        {
            console.WriteLine(message);
        }

        return result.ExitCode;
    }
}
