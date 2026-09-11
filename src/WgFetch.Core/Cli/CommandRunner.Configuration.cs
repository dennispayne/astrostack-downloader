using Spectre.Console;
using WgFetch.Core.Configuration;
using WgFetch.Core.Prereqs;

namespace WgFetch.Core.Cli;

public sealed partial class CommandRunner
{
    private const string ModelPrerequisitesChoice = "Model prerequisites";
    private const string ExitChoice = "Exit";
    private const string InstallAllModelsChoice = "Install all pinned models";
    private const string ChangeModelsRootChoice = "Change models root";
    private const string BackChoice = "Back";

    private async Task<ExitCode> InteractiveConfigAsync(
        WgFetchConfig config,
        string? configuredPath,
        CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected || _humanToStderr)
        {
            _stderr.WriteLine("wgfetch config: interactive mode requires an attached terminal.");
            return ExitCode.Ambiguous;
        }

        var path = configuredPath ?? ConfigFile.DefaultPath;
        var current = config;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderConfig(current);
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a setting to edit, or manage model prerequisites")
                    .AddChoices([.. ConfigSettings.Names, ModelPrerequisitesChoice, ExitChoice]));
            if (choice == ExitChoice)
            {
                return ExitCode.Success;
            }

            if (choice == ModelPrerequisitesChoice)
            {
                var prereqExit = await ManagePrerequisitesAsync(current, path, cancellationToken).ConfigureAwait(false);
                if (prereqExit != ExitCode.Success)
                {
                    return prereqExit;
                }

                current = await ConfigFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var prompt = new TextPrompt<string>($"New value for {choice} (leave blank to cancel)");
            if (ConfigSettings.IsSecret(choice))
            {
                prompt.Secret();
            }

            var value = AnsiConsole.Prompt(prompt);
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
                    AnsiConsole.MarkupLine($"[red]Cannot create directory:[/] {Markup.Escape(value)}");
                    continue;
                }
            }

            if (!ConfigSettings.TrySet(current, choice, value, out var updated, out var error))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error!)}[/]");
                continue;
            }

            await ConfigFile.SaveAsync(updated, path, cancellationToken).ConfigureAwait(false);
            current = updated;
            AnsiConsole.MarkupLine("[green]Saved.[/]");
        }
    }

    private static void RenderConfig(WgFetchConfig config)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("wgfetch configuration");
        table.AddColumn("Setting");
        table.AddColumn("Persisted value");
        foreach (var setting in ConfigSettings.GetRedactedValues(config))
        {
            table.AddRow(setting.Name, setting.Value is null ? "-" : Markup.Escape(setting.Value));
        }

        AnsiConsole.Write(table);
    }

    private async Task<ExitCode> ManagePrerequisitesAsync(
        WgFetchConfig config,
        string configPath,
        CancellationToken cancellationToken)
    {
        var modelsRoot = Path.GetFullPath(config.ModelsRoot ?? WgFetchPaths.ModelsDirectory);
        var statuses = await PrereqInstaller.StatusAsync(modelsRoot, cancellationToken).ConfigureAwait(false);
        var table = new Table().Border(TableBorder.Rounded).Title($"Model prerequisites ({Markup.Escape(modelsRoot)})");
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

        AnsiConsole.Write(table);
        var installChoices = PinnedModels.All.ToDictionary(
            model => $"Install {model.Id}",
            model => model.Id,
            StringComparer.Ordinal);
        var choices = installChoices.Keys
            .Append(InstallAllModelsChoice).Append(ChangeModelsRootChoice).Append(BackChoice).ToArray();
        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Model action").AddChoices(choices));
        if (choice == BackChoice)
        {
            return ExitCode.Success;
        }

        if (choice == ChangeModelsRootChoice)
        {
            var root = AnsiConsole.Prompt(new TextPrompt<string>("Models root"));
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
                AnsiConsole.MarkupLine($"[red]Cannot create directory:[/] {Markup.Escape(root)}");
                return ExitCode.Success;
            }

            if (!ConfigSettings.TrySet(config, "modelsRoot", root, out var updated, out var error))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error!)}[/]");
                return ExitCode.UsageError;
            }

            await ConfigFile.SaveAsync(updated, configPath, cancellationToken).ConfigureAwait(false);
            return ExitCode.Success;
        }

        IReadOnlySet<string> selected = choice == InstallAllModelsChoice
            ? PinnedModels.All.Select(model => model.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>([installChoices[choice]], StringComparer.Ordinal);
        PrereqInstallResult? result = null;
        await AnsiConsole.Status()
            .StartAsync("Installing pinned model files...", async _ =>
            {
                result = await new PrereqInstaller(CreateHttpGateway(), _logger)
                    .InstallAsync(modelsRoot, selected, dryRun: false, cancellationToken)
                    .ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        foreach (var message in result!.Messages)
        {
            AnsiConsole.WriteLine(message);
        }

        return result.ExitCode;
    }
}
