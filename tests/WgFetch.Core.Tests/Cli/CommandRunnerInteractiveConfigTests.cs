using Spectre.Console;
using Spectre.Console.Testing;
using WgFetch.Core.Cli;
using WgFetch.Core.Configuration;
using WgFetch.Core.Prereqs;
using WgFetch.Core.Tests.Support;

namespace WgFetch.Core.Tests.Cli;

/// <summary>
/// Drives the interactive <c>wgfetch config</c> TUI end to end through an injected
/// <see cref="TestConsole"/>, so the menu, secret-editing, models-root repair and prerequisite
/// installation branches all run without a real terminal or network (docs/REQUIREMENTS.md, "Testing").
/// </summary>
public sealed class CommandRunnerInteractiveConfigTests
{
    // Derived at runtime (rather than hardcoded) so a change to ConfigSettings.Names cannot silently
    // make these tests select the wrong menu item.
    private static readonly List<string> SettingNames = ConfigSettings.Names.ToList();
    private static readonly int OutputDirectoryIndex = SettingNames.IndexOf("outputDirectory");
    private static readonly int AiKeyIndex = SettingNames.IndexOf("aiKey");
    private static readonly int ModelPrerequisitesIndex = SettingNames.Count;
    private static readonly int ExitIndex = SettingNames.Count + 1;

    private static PinnedModel TestModel(string id, string url, byte[] bytes) =>
        new(id, id, "test/repository", "revision", IsLanguageModel: false,
        [new ModelAsset("model.bin", url, bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)))]);

    private static TestConsole CreateInteractiveConsole()
    {
        var console = new TestConsole();
        console.Interactive();
        return console;
    }

    private static void SelectByIndex(TestConsole console, int index)
    {
        for (var i = 0; i < index; i++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Enter);
    }

    private static (CommandRunner Runner, StringWriter Out, StringWriter Error) CreateInteractiveRunner(
        TestConsole console,
        IReadOnlyList<PinnedModel>? models = null,
        StubHttpGateway? http = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            Http = http ?? new StubHttpGateway(),
            InteractiveConsole = console,
            InteractiveTerminalOverride = true,
            PrereqModels = models,
        });

        return (runner, stdout, stderr);
    }

    [Fact]
    public async Task Interactive_edit_saves_a_setting_and_exits()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        var console = CreateInteractiveConsole();
        SelectByIndex(console, OutputDirectoryIndex);
        console.Input.PushTextWithEnter(temp.Combine("source"));
        SelectByIndex(console, ExitIndex);
        var (runner, _, stderr) = CreateInteractiveRunner(console);

        var exit = await runner.RunAsync(["config", "--interactive", "--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr.ToString());
        var saved = await ConfigFile.LoadAsync(configPath, CancellationToken.None);
        Assert.Equal(temp.Combine("source"), saved.OutputDirectory);
        Assert.True(Directory.Exists(temp.Combine("source")));
    }

    [Fact]
    public async Task Interactive_edit_of_a_credential_uses_a_masked_prompt_and_never_echoes_it()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        var console = CreateInteractiveConsole();
        SelectByIndex(console, AiKeyIndex);
        console.Input.PushTextWithEnter("super-secret-ai-key");
        SelectByIndex(console, ExitIndex);
        var (runner, _, _) = CreateInteractiveRunner(console);

        var exit = await runner.RunAsync(["config", "--interactive", "--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.DoesNotContain("super-secret-ai-key", console.Output, StringComparison.Ordinal);
        var saved = await ConfigFile.LoadAsync(configPath, CancellationToken.None);
        Assert.Equal("super-secret-ai-key", saved.AiKey);
    }

    [Fact]
    public async Task Interactive_session_repairs_rather_than_crashes_on_an_invalid_persisted_models_root()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        await File.WriteAllTextAsync(configPath, """{"modelsRoot":"\u0000"}""", CancellationToken.None);
        var console = CreateInteractiveConsole();
        SelectByIndex(console, ModelPrerequisitesIndex);
        SelectByIndex(console, ExitIndex);
        var (runner, _, _) = CreateInteractiveRunner(console);

        var exit = await runner.RunAsync(["config", "--interactive", "--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("invalid", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Interactive_session_installs_a_single_selected_model()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        await ConfigFile.SaveAsync(new WgFetchConfig { ModelsRoot = temp.Combine("models") }, configPath, CancellationToken.None);
        var bytes = "model bytes"u8.ToArray();
        var model = TestModel("demo-model", "https://models.example/demo.bin", bytes);
        var http = new StubHttpGateway().Map(model.Assets[0].Url, StubResponse.Binary(bytes));

        var console = CreateInteractiveConsole();
        SelectByIndex(console, ModelPrerequisitesIndex);
        SelectByIndex(console, 0); // "Install demo-model" is the only model choice.
        SelectByIndex(console, ExitIndex);
        var (runner, _, _) = CreateInteractiveRunner(console, models: [model], http: http);

        var exit = await runner.RunAsync(["config", "--interactive", "--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.True(File.Exists(temp.Combine("models", "demo-model", "model.bin")));
    }

    [Fact]
    public async Task Interactive_session_installs_all_models()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        await ConfigFile.SaveAsync(new WgFetchConfig { ModelsRoot = temp.Combine("models") }, configPath, CancellationToken.None);
        var firstBytes = "first model"u8.ToArray();
        var secondBytes = "second model"u8.ToArray();
        var first = TestModel("first-model", "https://models.example/first.bin", firstBytes);
        var second = TestModel("second-model", "https://models.example/second.bin", secondBytes);
        var http = new StubHttpGateway()
            .Map(first.Assets[0].Url, StubResponse.Binary(firstBytes))
            .Map(second.Assets[0].Url, StubResponse.Binary(secondBytes));

        var console = CreateInteractiveConsole();
        SelectByIndex(console, ModelPrerequisitesIndex);
        SelectByIndex(console, 2); // Install all follows the two individual model choices.
        SelectByIndex(console, ExitIndex);
        var (runner, _, _) = CreateInteractiveRunner(console, models: [first, second], http: http);

        var exit = await runner.RunAsync(["config", "--interactive", "--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.True(File.Exists(temp.Combine("models", "first-model", "model.bin")));
        Assert.True(File.Exists(temp.Combine("models", "second-model", "model.bin")));
    }

    [Fact]
    public async Task Interactive_session_persists_a_changed_models_root()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        var newRoot = temp.Combine("new-models");
        var model = TestModel("demo-model", "https://models.example/demo.bin", "bytes"u8.ToArray());
        var console = CreateInteractiveConsole();
        SelectByIndex(console, ModelPrerequisitesIndex);
        SelectByIndex(console, 2); // Change models root follows install-one and install-all.
        console.Input.PushTextWithEnter(newRoot);
        SelectByIndex(console, ExitIndex);
        var (runner, _, _) = CreateInteractiveRunner(console, models: [model]);

        var exit = await runner.RunAsync(["config", "--interactive", "--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.True(Directory.Exists(newRoot));
        var saved = await ConfigFile.LoadAsync(configPath, CancellationToken.None);
        Assert.Equal(newRoot, saved.ModelsRoot);
    }

    [Fact]
    public async Task Interactive_install_failure_is_written_to_the_redacting_logger()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        await ConfigFile.SaveAsync(new WgFetchConfig { ModelsRoot = temp.Combine("models") }, configPath, CancellationToken.None);
        var model = TestModel("demo-model", "https://models.example/demo.bin", "bytes"u8.ToArray());
        var http = new StubHttpGateway().Map(model.Assets[0].Url, StubResponse.Status(503));
        var console = CreateInteractiveConsole();
        SelectByIndex(console, ModelPrerequisitesIndex);
        SelectByIndex(console, 0);
        var (runner, _, stderr) = CreateInteractiveRunner(console, models: [model], http: http);

        var exit = await runner.RunAsync(["config", "--interactive", "--config", configPath], CancellationToken.None);

        Assert.NotEqual(ExitCode.Success, exit);
        Assert.Contains("HTTP 503", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interactive_prerequisites_menu_shows_progress_while_scanning_model_status()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        await ConfigFile.SaveAsync(new WgFetchConfig { ModelsRoot = temp.Combine("models") }, configPath, CancellationToken.None);
        var model = TestModel("demo-model", "https://models.example/demo.bin", "bytes"u8.ToArray());
        var console = CreateInteractiveConsole();
        SelectByIndex(console, ModelPrerequisitesIndex);
        SelectByIndex(console, 3); // Back.
        SelectByIndex(console, ExitIndex);
        var (runner, _, _) = CreateInteractiveRunner(console, models: [model]);

        var exit = await runner.RunAsync(["config", "--interactive", "--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("Checking pinned model files", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_tty_running_in_plain_render_mode_is_still_treated_as_interactive()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        var console = CreateInteractiveConsole();
        SelectByIndex(console, ExitIndex);
        var (runner, _, stderr) = CreateInteractiveRunner(console);

        var exit = await runner.RunAsync(["config", "--interactive", "--config", configPath, "--plain"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.DoesNotContain("requires an attached terminal", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_interactive_console_emits_no_ansi_escape_sequences()
    {
        var output = new StringWriter();
        var console = CommandRunner.CreateInteractiveConsole(output, plainRendering: true);

        console.MarkupLine("[red]plain output[/]");

        Assert.Equal($"plain output{Environment.NewLine}", output.ToString());
        Assert.DoesNotContain('\u001b', output.ToString());
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void A_persisted_plain_setting_is_honored_even_without_the_command_line_flag(
        bool plainFlag, bool? configPlain, bool expected)
    {
        Assert.Equal(expected, CommandRunner.ResolveConfigPlain(plainFlag, configPlain));
    }
}
