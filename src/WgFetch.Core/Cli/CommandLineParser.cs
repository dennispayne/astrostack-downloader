namespace WgFetch.Core.Cli;

/// <summary>Whether an option carries a value or is a bare switch.</summary>
public enum OptionArity
{
    Flag,
    Value,
    MultiValue,
}

public sealed record OptionSpec(string Name, OptionArity Arity, string Description, string? EnvironmentVariable = null);

/// <summary>One wgfetch verb and the options it accepts.</summary>
public sealed record CommandSpec(string Name, string Summary, IReadOnlyList<OptionSpec> Options, IReadOnlyList<string> SubCommands)
{
    public bool Accepts(string option) => Options.Any(o => string.Equals(o.Name, option, StringComparison.Ordinal));
}

/// <summary>The result of parsing a command line; parsing never performs I/O and never loads a model.</summary>
public sealed record ParsedCommandLine
{
    public string? Command { get; init; }

    public string? SubCommand { get; init; }

    public IReadOnlyList<string> Positional { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, List<string>> Options { get; init; } =
        new Dictionary<string, List<string>>(StringComparer.Ordinal);

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public bool HelpRequested { get; init; }

    public bool VersionRequested { get; init; }

    public bool NoCommandGiven { get; init; }

    public bool HasErrors => Errors.Count > 0;

    public bool Has(string option) => Options.ContainsKey(option);

    public string? Value(string option) =>
        Options.TryGetValue(option, out var values) && values.Count > 0 ? values[^1] : null;

    public IReadOnlyList<string> Values(string option) =>
        Options.TryGetValue(option, out var values) ? values : Array.Empty<string>();

    public int? IntValue(string option) =>
        int.TryParse(Value(option), out var value) ? value : null;

    public double? DoubleValue(string option) =>
        double.TryParse(Value(option), System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// A dependency-free command-line parser. Startup must be fast and <c>--help</c>, <c>--version</c> and
/// argument errors must never load a model (docs/REQUIREMENTS.md, "Responsiveness").
/// </summary>
public static class CommandLineParser
{
    private static readonly OptionSpec[] GlobalOptions =
    [
        new("--log-level", OptionArity.Value, "trace|debug|info|warn|error|none (default info)"),
        new("--log-file", OptionArity.Value, "write structured logs to a file"),
        new("--json", OptionArity.Flag, "machine-readable events on stdout; human logging on stderr"),
        new("--verbose", OptionArity.Flag, "shorthand for --log-level debug"),
        new("--no-color", OptionArity.Flag, "disable colour"),
        new("--plain", OptionArity.Flag, "plain output, no animation, no escape codes"),
        new("--config", OptionArity.Value, "path to config.json"),
        new("--help", OptionArity.Flag, "show help"),
        new("--version", OptionArity.Flag, "show version"),
    ];

    private static readonly OptionSpec[] AcquisitionOptions =
    [
        new("--winget-repo", OptionArity.Value, "existing source tree whose targets.yaml drives the run"),
        new("--from-file", OptionArity.Value, "YAML/JSON batch input list"),
        new("--output", OptionArity.Value, "output source directory"),
        new("--installer-dir", OptionArity.Value, "installer directory override"),
        new("--cache-dir", OptionArity.Value, "cache directory override"),
        new("--only-missing", OptionArity.Flag, "skip refreshing stale entries"),
        new("--refresh-stale", OptionArity.Flag, "limit the run to already-acquired entries"),
        new("--embedding-model", OptionArity.Value, "path to the E5-small-v2 ONNX model"),
        new("--llm-model", OptionArity.Value, "path to the Phi-3.5-mini ONNX model"),
        new("--models-root", OptionArity.Value, "root directory holding pinned models"),
        new("--recipe", OptionArity.Value, "path to a recipe file"),
        new("--recipe-inline", OptionArity.Value, "inline recipe expression"),
        new("--arch", OptionArity.Value, "x64|x86|arm64 (default: host)"),
        new("--scope", OptionArity.Value, "machine|user (default machine)"),
        new("--threshold", OptionArity.Value, "tier-1 confidence threshold"),
        new("--keep-versions", OptionArity.Value, "retention count (default 2)"),
        new("--require-hash-match", OptionArity.Flag, "upstream hash mismatch is fatal"),
        new("--ai-mode", OptionArity.Value, "local|remote|auto (default local)"),
        new("--ai-endpoint", OptionArity.Value, "OpenAI-compatible endpoint"),
        new("--ai-model", OptionArity.Value, "remote model name"),
        new("--ai-key", OptionArity.Value, "remote API key", "WGFETCH_AI_KEY"),
        new("--github-token", OptionArity.Value, "GitHub token", "GITHUB_TOKEN"),
        new("--parallel-downloads", OptionArity.Value, "concurrent downloads (default 3)"),
        new("--max-per-host", OptionArity.Value, "concurrent downloads per host (default 2)"),
        new("--search-provider", OptionArity.Value, "duckduckgo|brave|startpage|mojeek|searxng|google|bing|none"),
        new("--search-endpoint", OptionArity.Value, "self-hosted search endpoint"),
        new("--search-key", OptionArity.Value, "search API key", "WGFETCH_SEARCH_KEY"),
        new("--no-resume", OptionArity.Flag, "force a clean refetch"),
        new("--download-prereqs", OptionArity.Flag, "fetch missing models inline"),
        new("--dry-run", OptionArity.Flag, "resolve and verify only; move no bytes"),
    ];

    /// <summary>Every supported verb (docs/REQUIREMENTS.md, "CLI surface").</summary>
    public static IReadOnlyList<CommandSpec> Commands { get; } =
    [
        new("fetch", "Acquire installers and emit the source tree", Combine(AcquisitionOptions), []),
        new("resolve", "Resolve and verify without downloading", Combine(AcquisitionOptions), []),
        new("add", "Append targets to targets.yaml", Combine(AcquisitionOptions, new OptionSpec("--resolve", OptionArity.Flag, "also resolve each target")), []),
        new("remove", "Remove targets from targets.yaml", Combine([new OptionSpec("--winget-repo", OptionArity.Value, "source tree"), new OptionSpec("--output", OptionArity.Value, "output directory")]), []),
        new("status", "Per-target state table", Combine([new OptionSpec("--winget-repo", OptionArity.Value, "source tree"), new OptionSpec("--output", OptionArity.Value, "output directory")]), []),
        new("export", "Export machine-owned fields", Combine([new OptionSpec("--format", OptionArity.Value, "astrostack-dsc|applist"), new OptionSpec("--out", OptionArity.Value, "output directory"), new OptionSpec("--winget-repo", OptionArity.Value, "source tree"), new OptionSpec("--output", OptionArity.Value, "output directory")]), []),
        new("import", "Seed targets.yaml from an existing consumer repo", Combine([new OptionSpec("--format", OptionArity.Value, "astrostack-dsc"), new OptionSpec("--winget-repo", OptionArity.Value, "source tree"), new OptionSpec("--output", OptionArity.Value, "output directory")]), []),
        new("refresh", "Update catalog, embeddings and recipe cache", Combine(AcquisitionOptions), []),
        new("prereqs", "Install or report on pinned models", Combine([new OptionSpec("--models-dir", OptionArity.Value, "model directory"), new OptionSpec("--include-llm", OptionArity.Flag, "also fetch Phi-3.5-mini"), new OptionSpec("--dry-run", OptionArity.Flag, "report only")]), ["install", "status"]),
        new("verify", "Re-hash artifacts against provenance.json", Combine([new OptionSpec("--output", OptionArity.Value, "output directory"), new OptionSpec("--winget-repo", OptionArity.Value, "source tree")]), []),
        new("recipes", "Inspect bundled and cached recipes", Combine([new OptionSpec("--out", OptionArity.Value, "output directory"), new OptionSpec("--cache-dir", OptionArity.Value, "cache directory")]), ["list", "show", "export", "validate"]),
        new("list", "List acquired packages", Combine([new OptionSpec("--output", OptionArity.Value, "output directory"), new OptionSpec("--winget-repo", OptionArity.Value, "source tree")]), []),
        new("diagnostics", "Write a redacted support bundle", Combine([new OptionSpec("--out", OptionArity.Value, "bundle directory"), new OptionSpec("--output", OptionArity.Value, "output directory")]), []),
    ];

    public static CommandSpec? FindCommand(string? name) =>
        name is null ? null : Commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    public static ParsedCommandLine Parse(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var positional = new List<string>();
        var errors = new List<string>();
        string? command = null;
        string? subCommand = null;
        var help = false;
        var version = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg is "--")
            {
                for (var j = i + 1; j < args.Count; j++)
                {
                    positional.Add(args[j]);
                }

                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg;
                string? inlineValue = null;
                var equals = arg.IndexOf('=');
                if (equals > 2)
                {
                    name = arg[..equals];
                    inlineValue = arg[(equals + 1)..];
                }

                switch (name)
                {
                    case "--help":
                        help = true;
                        continue;
                    case "--version":
                        version = true;
                        continue;
                }

                var spec = Lookup(command, name);
                if (spec is null)
                {
                    errors.Add(command is null
                        ? $"unknown option '{name}'"
                        : $"unknown option '{name}' for command '{command}'");
                    continue;
                }

                if (spec.Arity == OptionArity.Flag)
                {
                    if (inlineValue is not null)
                    {
                        errors.Add($"option '{name}' does not take a value");
                        continue;
                    }

                    Append(options, name, "true");
                    continue;
                }

                var value = inlineValue;
                if (value is null)
                {
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        errors.Add($"option '{name}' requires a value");
                        continue;
                    }

                    value = args[++i];
                }

                Append(options, name, value);
                continue;
            }

            if (arg.StartsWith('-') && arg.Length > 1)
            {
                errors.Add($"unknown option '{arg}'");
                continue;
            }

            if (command is null)
            {
                command = arg;
                if (FindCommand(command) is null)
                {
                    errors.Add($"unknown command '{command}'");
                }

                continue;
            }

            var spec2 = FindCommand(command);
            if (subCommand is null && spec2 is { SubCommands.Count: > 0 })
            {
                if (spec2.SubCommands.Contains(arg, StringComparer.Ordinal))
                {
                    subCommand = arg;
                    continue;
                }

                errors.Add($"unknown subcommand '{arg}' for command '{command}' (expected {string.Join('|', spec2.SubCommands)})");
                continue;
            }

            positional.Add(arg);
        }

        if (command is null && !help && !version)
        {
            errors.Add("no command given");
        }

        if (FindCommand(command) is { SubCommands.Count: > 0 } withSubs && subCommand is null && !help && !version)
        {
            errors.Add($"command '{command}' requires a subcommand ({string.Join('|', withSubs.SubCommands)})");
        }

        if (options.TryGetValue("--verbose", out _) && !options.ContainsKey("--log-level"))
        {
            Append(options, "--log-level", "debug");
        }

        return new ParsedCommandLine
        {
            Command = command,
            SubCommand = subCommand,
            Positional = positional,
            Options = options,
            Errors = errors,
            HelpRequested = help,
            VersionRequested = version,
            NoCommandGiven = command is null && !help && !version,
        };
    }

    private static OptionSpec? Lookup(string? command, string name)
    {
        var global = GlobalOptions.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));
        if (global is not null)
        {
            return global;
        }

        return FindCommand(command)?.Options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));
    }

    private static void Append(Dictionary<string, List<string>> options, string name, string value)
    {
        if (!options.TryGetValue(name, out var list))
        {
            list = [];
            options[name] = list;
        }

        list.Add(value);
    }

    private static IReadOnlyList<OptionSpec> Combine(IReadOnlyList<OptionSpec> options, params OptionSpec[] extra) =>
        options.Concat(extra).Concat(GlobalOptions).DistinctBy(o => o.Name, StringComparer.Ordinal).ToArray();

    /// <summary>Renders help text. Must be produced without touching the filesystem or the network.</summary>
    public static string RenderHelp(string? command = null)
    {
        var writer = new StringWriter();
        var spec = FindCommand(command);
        if (spec is null)
        {
            writer.WriteLine("wgfetch — resolve fuzzy app names to vendor installers and lay them out as a local winget source.");
            writer.WriteLine();
            writer.WriteLine("Usage: wgfetch <command> [options]");
            writer.WriteLine();
            writer.WriteLine("Commands:");
            foreach (var c in Commands)
            {
                var name = c.SubCommands.Count > 0 ? $"{c.Name} {string.Join('|', c.SubCommands)}" : c.Name;
                writer.WriteLine($"  {name,-28}{c.Summary}");
            }

            writer.WriteLine();
            writer.WriteLine("Run 'wgfetch <command> --help' for command options.");
            writer.WriteLine("wgfetch installs nothing, repacks nothing, and sends no telemetry.");
            return writer.ToString();
        }

        var usage = spec.SubCommands.Count > 0
            ? $"Usage: wgfetch {spec.Name} {string.Join('|', spec.SubCommands)} [options]"
            : $"Usage: wgfetch {spec.Name} [<name>...] [options]";
        writer.WriteLine(spec.Summary);
        writer.WriteLine();
        writer.WriteLine(usage);
        writer.WriteLine();
        writer.WriteLine("Options:");
        foreach (var option in spec.Options)
        {
            writer.WriteLine($"  {option.Name,-24}{option.Description}");
        }

        return writer.ToString();
    }
}
