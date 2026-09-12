using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Catalog;
using WgFetch.Core.Configuration;
using WgFetch.Core.Inference;
using WgFetch.Core.Logging;
using WgFetch.Core.Model;
using WgFetch.Core.Output;
using WgFetch.Core.Prereqs;
using WgFetch.Core.Progress;
using WgFetch.Core.Recipes;
using WgFetch.Core.Targets;

namespace WgFetch.Core.Cli;

/// <summary>
/// Dependencies a test may substitute. Every one of them is I/O — HTTP, model inference, time — so a
/// hermetic test can exercise the whole command surface with no network and no model files
/// (docs/REQUIREMENTS.md, "Testing": "All I/O ... sits behind interfaces with fakes").
/// </summary>
public sealed record RunnerDependencies
{
    public IHttpGateway? Http { get; init; }

    public IEmbeddingModel? Embeddings { get; init; }

    public ITextGenerator? LocalGenerator { get; init; }

    public ITextGenerator? RemoteGenerator { get; init; }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Overrides the process environment so terminal detection is testable.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Overrides terminal capability detection so presentation remains hermetically testable.</summary>
    public TerminalEnvironment? TerminalEnvironment { get; init; }
}

/// <summary>
/// Orchestrates every verb in the CLI surface (docs/REQUIREMENTS.md, "CLI surface"). Lives in the
/// class library so it is fully testable; the NativeAOT executable is a thin shell over it.
/// </summary>
[SuppressMessage(
    "Microsoft.Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The logger provider and progress renderer are created and disposed inside RunAsync's " +
        "finally block, so their lifetime never outlives a single run and the runner itself owns nothing " +
        "after it returns.")]
public sealed partial class CommandRunner
{
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly RunnerDependencies _dependencies;
    private readonly PhaseTimings _timings = new();

    private JsonEventWriter? _events;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private RedactingConsoleLoggerProvider? _loggerProvider;
    private IProgressRenderer _progress = new PlainProgressRenderer(TextWriter.Null);
    private bool _humanToStderr;
    private IReadOnlyList<string> _activeSecrets = Array.Empty<string>();

    public CommandRunner(TextWriter stdout, TextWriter stderr, RunnerDependencies? dependencies = null)
    {
        _stdout = stdout;
        _stderr = stderr;
        _dependencies = dependencies ?? new RunnerDependencies();
    }

    /// <summary>The version reported by <c>--version</c>.</summary>
    public static string Version =>
        typeof(CommandRunner).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<ExitCode> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        _activeSecrets = Array.Empty<string>();

        var parsed = CommandLineParser.Parse(args);

        try
        {
            if (parsed.NoCommandGiven &&
                parsed.Positional.Count == 0 &&
                parsed.Errors.All(error => error == "no command given"))
            {
                return await ShowLandingAsync(parsed, cancellationToken).ConfigureAwait(false);
            }

            // --help, --version and argument errors must be fast and must never load a model.
            if (parsed.HelpRequested)
            {
                _stdout.Write(CommandLineParser.RenderHelp(parsed.Command));
                return ExitCode.Success;
            }

            if (parsed.VersionRequested)
            {
                _stdout.WriteLine(Version);
                return ExitCode.Success;
            }

            if (parsed.HasErrors)
            {
                WriteUsageErrors(parsed);
                return ExitCode.UsageError;
            }

            return await ExecuteAsync(parsed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _stderr.WriteLine("wgfetch: cancelled.");
            return ExitCode.Cancelled;
        }
        catch (TargetsFileException ex)
        {
            _stderr.WriteLine($"wgfetch: {TerminalSafe(ex.Message)}");
            _stderr.WriteLine("Fix the file by hand, or move it aside and re-add your targets.");
            return ExitCode.UsageError;
        }
        finally
        {
            _events?.Flush();
            await _progress.DisposeAsync().ConfigureAwait(false);
            _loggerProvider?.Dispose();

            // Never leave the terminal in a broken state, on any exit path.
            if (!Console.IsOutputRedirected)
            {
                _stdout.Flush();
            }

            _stderr.Flush();
        }
    }

    private async Task<ExitCode> ExecuteAsync(ParsedCommandLine parsed, CancellationToken cancellationToken)
    {
        var config = await ConfigFile.LoadAsync(parsed.Value("--config"), cancellationToken).ConfigureAwait(false);
        if (!TryResolveSettings(parsed, config, out var settings))
        {
            return ExitCode.UsageError;
        }

        var terminal = TerminalCapability.Detect(BuildTerminalEnvironment(settings));
        _loggerProvider = new RedactingConsoleLoggerProvider(_stderr, settings.LogLevel, settings.Secrets);
        _logger = _loggerProvider.CreateLogger("wgfetch");

        if (settings.Json)
        {
            _events = new JsonEventWriter(_stdout, settings.Secrets, deterministicOrder: true);
            _humanToStderr = true;
        }

        _progress = terminal == TerminalMode.Plain
            ? new PlainProgressRenderer(_stderr)
            : new LiveProgressRenderer();

        var exitCode = parsed.Command switch
        {
            "fetch" => await FetchAsync(parsed, settings, download: true, cancellationToken).ConfigureAwait(false),
            "resolve" => await FetchAsync(parsed, settings, download: false, cancellationToken).ConfigureAwait(false),
            "add" => await AddAsync(parsed, settings, cancellationToken).ConfigureAwait(false),
            "remove" => await RemoveAsync(parsed, settings, cancellationToken).ConfigureAwait(false),
            "status" => await StatusAsync(settings, cancellationToken).ConfigureAwait(false),
            "list" => await ListAsync(settings, cancellationToken).ConfigureAwait(false),
            "export" => await ExportAsync(parsed, settings, cancellationToken).ConfigureAwait(false),
            "import" => await ImportAsync(parsed, settings, cancellationToken).ConfigureAwait(false),
            "refresh" => await RefreshAsync(settings, cancellationToken).ConfigureAwait(false),
            "prereqs" => await PrereqsAsync(parsed, settings, cancellationToken).ConfigureAwait(false),
            "verify" => await VerifyAsync(settings, cancellationToken).ConfigureAwait(false),
            "recipes" => await RecipesAsync(parsed, settings, cancellationToken).ConfigureAwait(false),
            "diagnostics" => await DiagnosticsAsync(parsed, settings, config, cancellationToken).ConfigureAwait(false),
            _ => ExitCode.UsageError,
        };

        if (settings.LogLevel <= LogLevel.Debug)
        {
            _logger.LogDebug("{Timings}", _timings.Summarize());
        }

        return exitCode;
    }

    private async Task<ExitCode> ShowLandingAsync(ParsedCommandLine parsed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // --json and redirected stdout must retain the parser's usage-error output verbatim, and this
        // check must happen before any --config or targets.yaml I/O so a bad/unreadable --config path
        // can never suppress it (both --json and redirection are pure CLI-flag/environment facts).
        if (parsed.Has("--json") || IsOutputRedirected())
        {
            WriteUsageErrors(parsed);
            return ExitCode.UsageError;
        }

        var config = await ConfigFile.LoadAsync(parsed.Value("--config"), cancellationToken).ConfigureAwait(false);
        if (!TryResolveSettings(parsed, config, out var settings))
        {
            return ExitCode.UsageError;
        }
        var environment = BuildTerminalEnvironment(settings);
        var terminal = TerminalCapability.Detect(environment);
        var displayOutputDirectory = SecretRedactor.Redact(settings.OutputDirectory, _activeSecrets);

        TargetsDocument? targets = null;
        string? targetsError = null;
        var targetsPath = TargetsPath(settings);
        try
        {
            // Let TargetsFile own the missing-file distinction: reading directly (rather than gating on
            // File.Exists here) means a path that exists but cannot be inspected as a regular file — a
            // directory named targets.yaml, or one blocked by permissions — fails closed below instead
            // of being silently treated as a first run.
            var (document, existed) = await TargetsFile.LoadDetailedAsync(targetsPath, cancellationToken).ConfigureAwait(false);
            if (existed)
            {
                targets = document;
            }
        }
        catch (TargetsFileException)
        {
            // The loader has already normalized every parser and I/O failure, so the landing view can
            // report a single stable message instead of leaking parser detail into cosmetic output.
            targetsError = "unable to read targets.yaml";
            _stderr.WriteLine($"wgfetch: {targetsError}.");
        }

        IAnsiConsole? console = null;
        if (terminal == TerminalMode.Interactive)
        {
            console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Yes,
                ColorSystem = ColorSystemSupport.TrueColor,
                Out = new AnsiConsoleOutput(_stdout),
            });
            console.Write(SplashScreen.CreateInteractiveHeader());
            console.WriteLine(string.Empty);
        }
        else
        {
            SplashScreen.WritePlainHeader(_stdout);
        }

        _stdout.Flush();

        // The landing view is cosmetic and must stay fast, so readiness uses the hashing-free presence
        // probe; full digest verification belongs to 'prereqs status' and 'verify'. It is also only
        // computed when a status block will actually show it — the first-run hint never does. The
        // default install intentionally installs only the required embedding model (Phi is optional
        // unless --include-llm is requested), so only that model may make the landing report state.
        var prerequisitesPresent = (targets is not null || targetsError is not null) &&
            PrereqInstaller.QuickReady(settings.ModelsRoot, [PinnedModels.Embedding]);
        var now = _dependencies.TimeProvider.GetUtcNow();
        if (console is not null)
        {
            console.Write(SplashScreen.CreateStatus(
                targets,
                displayOutputDirectory,
                prerequisitesPresent,
                now,
                targetsError));
        }
        else
        {
            SplashScreen.WriteStatus(
                _stdout,
                targets,
                displayOutputDirectory,
                prerequisitesPresent,
                now,
                targetsError);
        }

        _stdout.WriteLine();
        _stdout.Write(CommandLineParser.RenderHelp());
        return ExitCode.UsageError;
    }

    /// <summary>
    /// Whether stdout is redirected, honouring the injected <see cref="TerminalEnvironment"/> fixture
    /// when present so this stays hermetically testable without touching <c>--config</c> or settings.
    /// </summary>
    private bool IsOutputRedirected() =>
        _dependencies.TerminalEnvironment?.OutputRedirected ?? Console.IsOutputRedirected;

    private void WriteUsageErrors(ParsedCommandLine parsed)
    {
        foreach (var error in parsed.Errors)
        {
            _stderr.WriteLine($"wgfetch: {error}");
        }

        _stderr.WriteLine("Run 'wgfetch --help' for usage.");
    }

    private TerminalEnvironment BuildTerminalEnvironment(RunSettings settings)
    {
        if (_dependencies.TerminalEnvironment is { } environment)
        {
            return environment with
            {
                PlainRequested = settings.Plain,
                NoColorRequested = settings.NoColor,
                JsonRequested = settings.Json,
            };
        }

        if (_dependencies.Environment is null)
        {
            return TerminalEnvironment.FromProcess(settings.Plain, settings.NoColor, settings.Json);
        }

        return new TerminalEnvironment
        {
            OutputRedirected = Console.IsOutputRedirected,
            ErrorRedirected = Console.IsErrorRedirected,
            Term = Lookup("TERM"),
            NoColorSet = !string.IsNullOrEmpty(Lookup("NO_COLOR")),
            CiSet = !string.IsNullOrEmpty(Lookup("CI")),
            PlainRequested = settings.Plain,
            NoColorRequested = settings.NoColor,
            JsonRequested = settings.Json,
        };

        string? Lookup(string name) =>
            _dependencies.Environment.TryGetValue(name, out var value) ? value : null;
    }

    private void Emit(JsonEvent @event) => _events?.Write(@event);

    private bool TryResolveSettings(
        ParsedCommandLine parsed,
        WgFetchConfig config,
        [NotNullWhen(true)] out RunSettings? settings)
    {
        try
        {
            settings = RunSettings.Resolve(parsed, config, _dependencies.Environment);
            _activeSecrets = settings.Secrets;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _stderr.WriteLine($"wgfetch: invalid configuration or option value: {TerminalSafe(ex.Message)}");
            settings = null;
            return false;
        }
    }

    private string TerminalSafe(string value) =>
        TerminalTextSanitizer.Sanitize(SecretRedactor.Redact(value, _activeSecrets));

    /// <summary>
    /// Human-readable output. With <c>--json</c>, stdout belongs exclusively to the event stream, so
    /// prose moves to stderr and stdout stays parseable (docs/REQUIREMENTS.md, "Observability").
    /// </summary>
    private void Report(string message)
    {
        if (_humanToStderr)
        {
            _stderr.WriteLine(message);
        }
        else
        {
            _stdout.WriteLine(message);
        }
    }

    private RecipeStore LoadRecipes(RunSettings settings)
    {
        var store = new RecipeStore(_logger);
        store.LoadSeedRecipes();
        store.LoadAutoRecipes(settings.CacheDirectory);

        if (settings.RecipePath is { Length: > 0 } path)
        {
            store.AddUserRecipeFile(path);
        }

        if (settings.RecipeInline is { Length: > 0 } inline)
        {
            store.AddUserRecipeInline(inline);
        }

        return store;
    }

    /// <summary>
    /// The catalog is recipe-driven and small, so it is derived from the loaded recipes rather than
    /// mirroring winget-pkgs (docs/REQUIREMENTS.md, "Name resolution").
    /// </summary>
    private static IReadOnlyList<CatalogEntry> BuildCatalog(RecipeStore recipes) =>
        recipes.All
            .GroupBy(r => r.ComponentId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(recipe => new CatalogEntry
            {
                Id = recipe.ComponentId,
                DisplayName = recipe.DisplayName,
                Publisher = recipe.Publisher,
                Tags = recipe.Tags,
                Aliases = [recipe.PackageId, .. recipe.Aliases],
            })
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();

    private static string TargetsPath(RunSettings settings) => SourceLayout.TargetsPath(settings.OutputDirectory);

    private IHttpGateway CreateHttpGateway() => _dependencies.Http ?? new HttpGateway();
}
