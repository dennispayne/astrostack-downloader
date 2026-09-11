using System.Text;

namespace WgFetch.Core.Output;

/// <summary>
/// Emits the winget-pkgs convention three-file manifest set:
/// <c>&lt;Id&gt;.yaml</c> (version), <c>&lt;Id&gt;.installer.yaml</c> and
/// <c>&lt;Id&gt;.locale.&lt;lang&gt;.yaml</c> (default locale), under
/// <c>manifests/&lt;first-letter&gt;/&lt;Publisher&gt;/&lt;Package&gt;/&lt;Version&gt;/</c>
/// (docs/REQUIREMENTS.md, "Output layout").
///
/// <para>
/// A manifest is never written unless the installer path, SHA256 and version are all present —
/// one invalid manifest breaks <c>winget validate</c> and the whole <c>index.db</c> build for the
/// entire source (docs/REQUIREMENTS.md, "Target list — repo-driven acquisition").
/// </para>
/// </summary>
public sealed class WingetManifestWriter
{
    public const string ManifestVersion = "1.6.0";

    private const string VersionSchema = "https://aka.ms/winget-manifest.version.1.6.0.schema.json";
    private const string InstallerSchema = "https://aka.ms/winget-manifest.installer.1.6.0.schema.json";
    private const string LocaleSchema = "https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json";

    /// <summary>
    /// Validates <paramref name="request"/> and, only if it fully qualifies, writes the three-file
    /// manifest set under <paramref name="outputRoot"/>. Never writes a placeholder/stub manifest:
    /// returns a failed result instead when required fields are missing.
    /// </summary>
    public async Task<ManifestWriteResult> WriteAsync(
        string outputRoot,
        WingetManifestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(request);

        var validation = Validate(request);
        if (validation is not null)
        {
            return new ManifestWriteResult { Success = false, Reason = validation };
        }

        var directory = SourceLayout.ManifestDirectory(outputRoot, request.PackageIdentifier, request.PackageVersion);
        Directory.CreateDirectory(directory);

        var versionPath = Path.Combine(directory, SourceLayout.VersionManifestFileName(request.PackageIdentifier));
        var installerPath = Path.Combine(directory, SourceLayout.InstallerManifestFileName(request.PackageIdentifier));
        var localePath = Path.Combine(
            directory,
            SourceLayout.LocaleManifestFileName(request.PackageIdentifier, request.DefaultLocale));

        var versionYaml = BuildVersionManifest(request);
        var installerYaml = BuildInstallerManifest(request);
        var localeYaml = BuildLocaleManifest(request);

        // Write all three or none: a torn manifest set is as dangerous as a stub.
        var tempVersion = versionPath + ".tmp";
        var tempInstaller = installerPath + ".tmp";
        var tempLocale = localePath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(tempVersion, versionYaml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(tempInstaller, installerYaml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(tempLocale, localeYaml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            File.Move(tempVersion, versionPath, overwrite: true);
            File.Move(tempInstaller, installerPath, overwrite: true);
            File.Move(tempLocale, localePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempVersion);
            TryDelete(tempInstaller);
            TryDelete(tempLocale);
            throw;
        }

        return new ManifestWriteResult
        {
            Success = true,
            Reason = "manifest set written",
            VersionManifestPath = versionPath,
            InstallerManifestPath = installerPath,
            LocaleManifestPath = localePath,
        };
    }

    /// <summary>Returns a failure reason, or null when <paramref name="request"/> qualifies for a real manifest.</summary>
    internal static string? Validate(WingetManifestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PackageIdentifier))
        {
            return "PackageIdentifier is required";
        }

        if (string.IsNullOrWhiteSpace(request.PackageVersion))
        {
            return "PackageVersion is required — a manifest cannot be written for an unresolved version";
        }

        if (string.IsNullOrWhiteSpace(request.Publisher))
        {
            return "Publisher is required";
        }

        if (string.IsNullOrWhiteSpace(request.PackageName))
        {
            return "PackageName is required";
        }

        if (request.Installers.Count == 0)
        {
            return "at least one installer entry is required — refusing to write a stub manifest";
        }

        for (var i = 0; i < request.Installers.Count; i++)
        {
            var installer = request.Installers[i];
            if (installer.InstallerUrl is null || string.IsNullOrWhiteSpace(installer.InstallerUrl.ToString()))
            {
                return $"installer[{i}]: InstallerUrl is required — a stub with no fetched bytes must never be emitted";
            }

            if (string.IsNullOrWhiteSpace(installer.InstallerSha256) || !LooksLikeSha256(installer.InstallerSha256))
            {
                return $"installer[{i}]: InstallerSha256 must be a locally computed 64-character hex digest";
            }

            if (string.IsNullOrWhiteSpace(installer.Architecture))
            {
                return $"installer[{i}]: Architecture is required";
            }

            if (string.IsNullOrWhiteSpace(installer.InstallerType))
            {
                return $"installer[{i}]: InstallerType is required";
            }

            var isArchive = string.Equals(installer.InstallerType, "zip", StringComparison.OrdinalIgnoreCase);
            if (isArchive && string.IsNullOrWhiteSpace(installer.NestedInstallerType))
            {
                return $"installer[{i}]: NestedInstallerType is required for a zip bundle (no archive is ever extracted)";
            }
        }

        return null;
    }

    private static bool LooksLikeSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildVersionManifest(WingetManifestRequest request)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, VersionSchema);
        AppendScalar(sb, "PackageIdentifier", request.PackageIdentifier);
        AppendScalar(sb, "PackageVersion", request.PackageVersion);
        AppendScalar(sb, "DefaultLocale", request.DefaultLocale);
        AppendScalar(sb, "ManifestType", "version");
        AppendScalar(sb, "ManifestVersion", ManifestVersion);
        return sb.ToString();
    }

    private static string BuildInstallerManifest(WingetManifestRequest request)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, InstallerSchema);
        AppendScalar(sb, "PackageIdentifier", request.PackageIdentifier);
        AppendScalar(sb, "PackageVersion", request.PackageVersion);
        sb.Append("Installers:\n");

        foreach (var installer in request.Installers)
        {
            sb.Append("- Architecture: ").Append(installer.Architecture).Append('\n');
            sb.Append("  InstallerType: ").Append(installer.InstallerType).Append('\n');

            if (!string.IsNullOrWhiteSpace(installer.Scope))
            {
                sb.Append("  Scope: ").Append(installer.Scope).Append('\n');
            }

            sb.Append("  InstallerUrl: ").Append(YamlScalar(installer.InstallerUrl.ToString())).Append('\n');
            sb.Append("  InstallerSha256: ").Append(installer.InstallerSha256).Append('\n');

            if (installer.Switches is { IsEmpty: false } switches)
            {
                sb.Append("  InstallerSwitches:\n");
                if (!string.IsNullOrWhiteSpace(switches.Silent))
                {
                    sb.Append("    Silent: ").Append(YamlScalar(switches.Silent)).Append('\n');
                }

                if (!string.IsNullOrWhiteSpace(switches.SilentWithProgress))
                {
                    sb.Append("    SilentWithProgress: ").Append(YamlScalar(switches.SilentWithProgress)).Append('\n');
                }
            }

            if (!string.IsNullOrWhiteSpace(installer.NestedInstallerType))
            {
                sb.Append("  NestedInstallerType: ").Append(installer.NestedInstallerType).Append('\n');
            }

            if (installer.NestedInstallerFiles.Count > 0)
            {
                sb.Append("  NestedInstallerFiles:\n");
                foreach (var file in installer.NestedInstallerFiles)
                {
                    sb.Append("  - RelativeFilePath: ").Append(YamlScalar(file)).Append('\n');
                }
            }
        }

        AppendScalar(sb, "ManifestType", "installer");
        AppendScalar(sb, "ManifestVersion", ManifestVersion);
        return sb.ToString();
    }

    private static string BuildLocaleManifest(WingetManifestRequest request)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, LocaleSchema);
        AppendScalar(sb, "PackageIdentifier", request.PackageIdentifier);
        AppendScalar(sb, "PackageVersion", request.PackageVersion);
        AppendScalar(sb, "PackageLocale", request.DefaultLocale);
        AppendScalar(sb, "Publisher", request.Publisher);

        if (!string.IsNullOrWhiteSpace(request.PublisherUrl))
        {
            AppendScalar(sb, "PublisherUrl", request.PublisherUrl);
        }

        AppendScalar(sb, "PackageName", request.PackageName);

        if (!string.IsNullOrWhiteSpace(request.PackageUrl))
        {
            AppendScalar(sb, "PackageUrl", request.PackageUrl);
        }

        AppendScalar(sb, "License", request.License);

        if (!string.IsNullOrWhiteSpace(request.ShortDescription))
        {
            AppendScalar(sb, "ShortDescription", request.ShortDescription);
        }

        AppendScalar(sb, "ManifestType", "defaultLocale");
        AppendScalar(sb, "ManifestVersion", ManifestVersion);
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, string schemaUrl) =>
        sb.Append("# yaml-language-server: $schema=").Append(schemaUrl).Append('\n');

    private static void AppendScalar(StringBuilder sb, string key, string value) =>
        sb.Append(key).Append(": ").Append(YamlScalar(value)).Append('\n');

    /// <summary>Quotes a scalar only when YAML would otherwise misinterpret it, keeping output tidy but safe.</summary>
    private static string YamlScalar(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuoting =
            value.Contains(": ") || value.EndsWith(':') || value.Contains(" #") || value.StartsWith('#') ||
            value.Contains('\'') || value.Contains('"') ||
            value.Contains('\n') || value.StartsWith(' ') || value.EndsWith(' ') ||
            value is "null" or "true" or "false" or "~" ||
            value.StartsWith('-') || value.StartsWith('[') || value.StartsWith('{') || value.StartsWith('&') ||
            value.StartsWith('*') || value.StartsWith('!') || value.StartsWith('|') || value.StartsWith('>') ||
            value.StartsWith('%') || value.StartsWith('@') || value.StartsWith('`');

        if (!needsQuoting)
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
    }
}
