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

    /// <summary>Overrides interactive terminal I/O so embedded callers can supply their own console.</summary>
    public IAnsiConsole? InteractiveConsole { get; init; }

    /// <summary>Overrides pinned models so interactive prerequisite management remains hermetic.</summary>
    public IReadOnlyList<PinnedModel>? PrereqModels { get; init; }

    /// <summary>
    /// Overrides whether a real, attached interactive terminal is present. Hermetic tests run with no
    /// TTY at all, so this lets them exercise the interactive config flow (with an injected
    /// <see cref="InteractiveConsole"/>) without a real console being attached.
    /// </summary>
    public bool? InteractiveTerminalOverride { get; init; }
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

        var parsed = CommandLineParser.Parse(args);

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
            var parseErrorSecrets = ResolveCliAndEnvironmentSecrets(parsed);
            foreach (var error in parsed.Errors)
            {
                _stderr.WriteLine($"wgfetch: {SecretRedactor.Redact(error, parseErrorSecrets)}");
            }

            _stderr.WriteLine("Run 'wgfetch --help' for usage.");
            return ExitCode.UsageError;
        }

        try
        {
            return await ExecuteAsync(parsed, cancellationToken).ConfigureAwait(false);
        }
        catch (TargetsFileException ex)
        {
            if (_events is not null)
            {
                Emit(new JsonEvent
                {
                    Event = "error",
                    Stage = "targets",
                    Status = "failed",
                    Message = ex.Message,
                    ExitCode = (int)ExitCode.UsageError,
                });
            }
            else
            {
                _stderr.WriteLine($"wgfetch: {ex.Message}");
            }

            return ExitCode.UsageError;
        }
        catch (OperationCanceledException)
        {
            _stderr.WriteLine("wgfetch: cancelled.");
            return ExitCode.Cancelled;
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
        WgFetchConfig config;
        try
        {
            config = await ConfigFile.LoadAsync(parsed.Value("--config"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            var path = parsed.Value("--config") ?? ConfigFile.DefaultPath;
            var message = SecretRedactor.Redact(
                $"unable to read config file '{path}': {exception.Message}",
                ResolveCliAndEnvironmentSecrets(parsed));
            _stderr.WriteLine($"wgfetch: {message}");
            return ExitCode.ConfigurationError;
        }

        if (parsed.Command == "config")
        {
            var configSecrets = ResolveConfigSecrets(parsed, config);
            _loggerProvider = new RedactingConsoleLoggerProvider(
                _stderr,
                LogLevelParser.Parse(parsed.Value("--log-level") ?? config.LogLevel),
                configSecrets);
            _logger = _loggerProvider.CreateLogger("wgfetch");

            if (parsed.Has("--json"))
            {
                _events = new JsonEventWriter(_stdout, configSecrets, deterministicOrder: true);
                _humanToStderr = true;
            }

            var configEnvironment = BuildTerminalEnvironment(
                ResolveConfigPlain(parsed.Has("--plain"), config.Plain),
                parsed.Has("--no-color"),
                parsed.Has("--json"));
            var isInteractiveTerminal = _dependencies.InteractiveTerminalOverride
                ?? TerminalCapability.IsInteractiveTerminal(configEnvironment);
            var plainRendering = TerminalCapability.Detect(configEnvironment) == TerminalMode.Plain;
            return await ConfigAsync(
                parsed,
                config,
                configSecrets,
                isInteractiveTerminal,
                plainRendering,
                cancellationToken).ConfigureAwait(false);
        }

        var settings = RunSettings.Resolve(parsed, config, _dependencies.Environment);

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

    /// <summary>
    /// A persisted <c>plain</c> setting must be honored the same way <see cref="RunSettings.Resolve"/>
    /// honors it for every other command, so <c>wgfetch config</c> does not emit ANSI escape sequences
    /// after <c>config set plain true</c> just because <c>--plain</c> was not also passed.
    /// </summary>
    internal static bool ResolveConfigPlain(bool plainFlag, bool? configPlain) =>
        plainFlag || configPlain == true;

    /// <summary>
    /// Collects every candidate credential value visible to the config command for redaction only.
    /// This deliberately aggregates CLI, environment and persisted values instead of resolving
    /// precedence, so stale or overridden secrets cannot leak from displayed persisted endpoints.
    /// </summary>
    private IReadOnlyList<string> ResolveConfigSecrets(ParsedCommandLine parsed, WgFetchConfig config) =>
        ResolveCliAndEnvironmentSecrets(parsed)
            .Concat([config.AiKey, config.SearchKey, config.GithubToken])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Collects CLI- and environment-supplied credential values for redaction only, used when a
    /// persisted config is unavailable (for example when it fails to load) so error messages still
    /// never render a credential that was passed on the command line or via the environment.
    /// </summary>
    private IReadOnlyList<string> ResolveCliAndEnvironmentSecrets(ParsedCommandLine parsed)
    {
        var secrets = new[] {
                parsed.Value("--ai-key"),
                Lookup(RunSettings.AiKeyEnvironmentVariable),
                parsed.Value("--search-key"),
                Lookup(RunSettings.SearchKeyEnvironmentVariable),
                parsed.Value("--github-token"),
                Lookup(RunSettings.GithubTokenEnvironmentVariable),
            }
            .Concat(PendingConfigSetCredential(parsed))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return secrets;

        string? Lookup(string name) => _dependencies.Environment is null
            ? Environment.GetEnvironmentVariable(name)
            : _dependencies.Environment.TryGetValue(name, out var value) ? value : null;
    }

    private static IEnumerable<string?> PendingConfigSetCredential(ParsedCommandLine parsed)
    {
        if (parsed.Command == "config" &&
            parsed.SubCommand == "set" &&
            parsed.Positional.Count == 2 &&
            IsConfigCredentialName(parsed.Positional[0]))
        {
            yield return parsed.Positional[1];
        }
    }

    private TerminalEnvironment BuildTerminalEnvironment(RunSettings settings) =>
        BuildTerminalEnvironment(settings.Plain, settings.NoColor, settings.Json);

    private TerminalEnvironment BuildTerminalEnvironment(bool plain, bool noColor, bool json)
    {
        if (_dependencies.Environment is null)
        {
            return TerminalEnvironment.FromProcess(plain, noColor, json);
        }

        return new TerminalEnvironment
        {
            OutputRedirected = Console.IsOutputRedirected,
            ErrorRedirected = Console.IsErrorRedirected,
            InputRedirected = Console.IsInputRedirected,
            Term = Lookup("TERM"),
            NoColorSet = !string.IsNullOrEmpty(Lookup("NO_COLOR")),
            CiSet = !string.IsNullOrEmpty(Lookup("CI")),
            PlainRequested = plain,
            NoColorRequested = noColor,
            JsonRequested = json,
        };

        string? Lookup(string name) =>
            _dependencies.Environment.TryGetValue(name, out var value) ? value : null;
    }

    private void Emit(JsonEvent @event) => _events?.Write(@event);

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
