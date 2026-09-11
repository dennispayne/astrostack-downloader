using System.Globalization;
namespace WgFetch.Core.Configuration;

/// <summary>Maps the persisted configuration schema to its command-line setting names.</summary>
public static class ConfigSettings
{
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "aiKey",
        "searchKey",
        "githubToken",
    };

    public static IReadOnlyList<string> Names { get; } =
    [
        "outputDirectory", "cacheDirectory", "modelsRoot", "threshold", "architecture", "scope",
        "keepVersions", "aiMode", "aiEndpoint", "aiModel", "aiKey", "searchProvider",
        "searchEndpoint", "searchKey", "githubToken", "logLevel", "parallelDownloads", "maxPerHost", "plain",
    ];

    public static bool IsSecret(string name) => SecretNames.Contains(name.Replace("-", string.Empty, StringComparison.Ordinal));

    public static bool TrySet(WgFetchConfig config, string name, string value, out WgFetchConfig updated, out string? error)
    {
        var normalized = Normalize(name);
        if (normalized == "architecture" && value is not ("x64" or "x86" or "arm64"))
        {
            updated = config; error = "architecture must be x64, x86, or arm64."; return false;
        }

        if (normalized == "scope" && value is not ("machine" or "user"))
        {
            updated = config; error = "scope must be machine or user."; return false;
        }

        if (normalized == "aimode" && value is not ("local" or "remote" or "auto"))
        {
            updated = config; error = "aiMode must be local, remote, or auto."; return false;
        }

        return normalized switch
        {
            "outputdirectory" => SetString(config, value, static (c, v) => c with { OutputDirectory = v }, out updated, out error),
            "cachedirectory" => SetString(config, value, static (c, v) => c with { CacheDirectory = v }, out updated, out error),
            "modelsroot" => SetString(config, value, static (c, v) => c with { ModelsRoot = v }, out updated, out error),
            "architecture" => SetString(config, value, static (c, v) => c with { Architecture = v }, out updated, out error),
            "scope" => SetString(config, value, static (c, v) => c with { Scope = v }, out updated, out error),
            "aimode" => SetString(config, value, static (c, v) => c with { AiMode = v }, out updated, out error),
            "aiendpoint" => SetString(config, value, static (c, v) => c with { AiEndpoint = v }, out updated, out error),
            "aimodel" => SetString(config, value, static (c, v) => c with { AiModel = v }, out updated, out error),
            "aikey" => SetString(config, value, static (c, v) => c with { AiKey = v }, out updated, out error),
            "searchprovider" => SetString(config, value, static (c, v) => c with { SearchProvider = v }, out updated, out error),
            "searchendpoint" => SetString(config, value, static (c, v) => c with { SearchEndpoint = v }, out updated, out error),
            "searchkey" => SetString(config, value, static (c, v) => c with { SearchKey = v }, out updated, out error),
            "githubtoken" => SetString(config, value, static (c, v) => c with { GithubToken = v }, out updated, out error),
            "loglevel" => SetString(config, value, static (c, v) => c with { LogLevel = v }, out updated, out error),
            "threshold" => SetDouble(config, value, out updated, out error),
            "keepversions" => SetInt(config, value, 1, int.MaxValue, static (c, v) => c with { KeepVersions = v }, out updated, out error),
            "paralleldownloads" => SetInt(config, value, 1, 16, static (c, v) => c with { ParallelDownloads = v }, out updated, out error),
            "maxperhost" => SetInt(config, value, 1, 8, static (c, v) => c with { MaxPerHost = v }, out updated, out error),
            "plain" => SetBool(config, value, out updated, out error),
            _ => UnknownResult(config, name, out updated, out error),
        };
    }

    public static bool TryUnset(WgFetchConfig config, string name, out WgFetchConfig updated, out string? error)
    {
        switch (Normalize(name))
        {
            case "outputdirectory": updated = config with { OutputDirectory = null }; break;
            case "cachedirectory": updated = config with { CacheDirectory = null }; break;
            case "modelsroot": updated = config with { ModelsRoot = null }; break;
            case "threshold": updated = config with { Threshold = null }; break;
            case "architecture": updated = config with { Architecture = null }; break;
            case "scope": updated = config with { Scope = null }; break;
            case "keepversions": updated = config with { KeepVersions = null }; break;
            case "aimode": updated = config with { AiMode = null }; break;
            case "aiendpoint": updated = config with { AiEndpoint = null }; break;
            case "aimodel": updated = config with { AiModel = null }; break;
            case "aikey": updated = config with { AiKey = null }; break;
            case "searchprovider": updated = config with { SearchProvider = null }; break;
            case "searchendpoint": updated = config with { SearchEndpoint = null }; break;
            case "searchkey": updated = config with { SearchKey = null }; break;
            case "githubtoken": updated = config with { GithubToken = null }; break;
            case "loglevel": updated = config with { LogLevel = null }; break;
            case "paralleldownloads": updated = config with { ParallelDownloads = null }; break;
            case "maxperhost": updated = config with { MaxPerHost = null }; break;
            case "plain": updated = config with { Plain = null }; break;
            default: return UnknownResult(config, name, out updated, out error);
        }

        error = null;
        return true;
    }

    public static string? GetRedactedValue(WgFetchConfig config, string name, out string? error)
    {
        var redacted = config.Redacted();
        error = null;
        return Normalize(name) switch
        {
            "outputdirectory" => redacted.OutputDirectory, "cachedirectory" => redacted.CacheDirectory,
            "modelsroot" => redacted.ModelsRoot, "threshold" => Format(redacted.Threshold),
            "architecture" => redacted.Architecture, "scope" => redacted.Scope,
            "keepversions" => Format(redacted.KeepVersions), "aimode" => redacted.AiMode,
            "aiendpoint" => redacted.AiEndpoint, "aimodel" => redacted.AiModel, "aikey" => redacted.AiKey,
            "searchprovider" => redacted.SearchProvider, "searchendpoint" => redacted.SearchEndpoint,
            "searchkey" => redacted.SearchKey, "githubtoken" => redacted.GithubToken,
            "loglevel" => redacted.LogLevel, "paralleldownloads" => Format(redacted.ParallelDownloads),
            "maxperhost" => Format(redacted.MaxPerHost), "plain" => Format(redacted.Plain),
            _ => UnknownValue(name, out error),
        };
    }

    public static IReadOnlyList<(string Name, string? Value)> GetRedactedValues(WgFetchConfig config) =>
        Names.Select(name => (name, GetRedactedValue(config, name, out _))).ToArray();

    private static bool SetString(WgFetchConfig config, string value, Func<WgFetchConfig, string, WgFetchConfig> setter, out WgFetchConfig updated, out string? error)
    {
        if (value is null)
        {
            updated = config; error = "value is required."; return false;
        }

        updated = setter(config, value);
        error = null;
        return true;
    }

    private static bool SetDouble(WgFetchConfig config, string value, out WgFetchConfig updated, out string? error)
    {
        if (double.TryParse(value, CultureInfo.InvariantCulture, out var result) && result is >= 0 and <= 1)
        {
            updated = config with { Threshold = result }; error = null;
            return true;
        }

        updated = config; error = "threshold must be a number from 0 through 1."; return false;
    }

    private static bool SetInt(WgFetchConfig config, string value, int minimum, int maximum, Func<WgFetchConfig, int, WgFetchConfig> setter, out WgFetchConfig updated, out string? error)
    {
        if (int.TryParse(value, CultureInfo.InvariantCulture, out var result) && result >= minimum && result <= maximum)
        {
            updated = setter(config, result); error = null;
            return true;
        }

        updated = config; error = "value must be an integer."; return false;
    }

    private static bool SetBool(WgFetchConfig config, string value, out WgFetchConfig updated, out string? error)
    {
        if (bool.TryParse(value, out var result))
        {
            updated = config with { Plain = result }; error = null; return true;
        }

        updated = config; error = "plain must be true or false."; return false;
    }

    private static bool UnknownResult(WgFetchConfig config, string name, out WgFetchConfig updated, out string? error)
    {
        updated = config; error = Unknown(name); return false;
    }

    private static string? UnknownValue(string name, out string? error)
    {
        error = Unknown(name); return null;
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        double number => number.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string Unknown(string name) =>
        $"unknown setting '{name}' (expected {string.Join(", ", Names)}).";

    private static string Normalize(string name) => name.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
