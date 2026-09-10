namespace WgFetch.Core.Output;

/// <summary>
/// Canonical relative paths for the local winget source tree wgfetch emits
/// (docs/REQUIREMENTS.md, "Output layout"). Centralising these avoids the four artifact writers
/// (manifests, <c>index.db</c>, <c>rest/</c>, <c>provenance.json</c>) drifting out of sync on layout.
/// </summary>
public static class SourceLayout
{
    public const string ManifestsDirectoryName = "manifests";
    public const string InstallersDirectoryName = "installers";
    public const string RestDirectoryName = "rest";
    public const string IndexDatabaseFileName = "index.db";
    public const string ProvenanceFileName = "provenance.json";
    public const string TargetsFileName = "targets.yaml";

    /// <summary>
    /// The winget-pkgs convention manifest directory for a package version:
    /// <c>manifests/&lt;first-letter&gt;/&lt;Publisher&gt;/.../&lt;Package&gt;/&lt;Version&gt;/</c>.
    /// <see cref="packageIdentifier"/> is split on '.' into successive directory segments, matching
    /// how winget-pkgs lays out multi-segment identifiers (e.g. <c>Microsoft.VisualStudio.2022.Community</c>).
    /// </summary>
    public static string ManifestDirectory(string outputRoot, string packageIdentifier, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var firstLetter = char.ToLowerInvariant(packageIdentifier[0]).ToString();
        var segments = packageIdentifier.Split('.', StringSplitOptions.RemoveEmptyEntries);

        var parts = new List<string> { outputRoot, ManifestsDirectoryName, firstLetter };
        parts.AddRange(segments);
        parts.Add(version);
        return Path.Combine(parts.ToArray());
    }

    /// <summary>Relative (POSIX-style, forward-slash) form of <see cref="ManifestDirectory"/>, for provenance/index storage.</summary>
    public static string ManifestRelativeDirectory(string packageIdentifier, string version)
    {
        var full = ManifestDirectory(string.Empty, packageIdentifier, version);
        return NormalizeRelative(full);
    }

    public static string VersionManifestFileName(string packageIdentifier) => $"{packageIdentifier}.yaml";

    public static string InstallerManifestFileName(string packageIdentifier) => $"{packageIdentifier}.installer.yaml";

    public static string LocaleManifestFileName(string packageIdentifier, string locale) =>
        $"{packageIdentifier}.locale.{locale}.yaml";

    /// <summary>
    /// Installer directory for a package version. Original vendor filenames are preserved
    /// (docs/REQUIREMENTS.md, "Output layout": "downloaded binaries, unmodified, original filenames preserved").
    /// </summary>
    public static string InstallerDirectory(string outputRoot, string packageIdentifier, string version) =>
        Path.Combine(outputRoot, InstallersDirectoryName, packageIdentifier, version);

    public static string InstallerPath(string outputRoot, string packageIdentifier, string version, string originalFileName) =>
        Path.Combine(InstallerDirectory(outputRoot, packageIdentifier, version), originalFileName);

    public static string IndexDatabasePath(string outputRoot) => Path.Combine(outputRoot, IndexDatabaseFileName);

    public static string ProvenancePath(string outputRoot) => Path.Combine(outputRoot, ProvenanceFileName);

    public static string TargetsPath(string outputRoot) => Path.Combine(outputRoot, TargetsFileName);

    public static string RestDirectory(string outputRoot) => Path.Combine(outputRoot, RestDirectoryName);

    public static string RestInformationPath(string outputRoot) => Path.Combine(RestDirectory(outputRoot), "information.json");

    public static string RestPackageManifestPath(string outputRoot, string packageIdentifier) =>
        Path.Combine(RestDirectory(outputRoot), "packageManifests", $"{packageIdentifier}.json");

    private static string NormalizeRelative(string path) => path.Replace(Path.DirectorySeparatorChar, '/').TrimStart('/');
}
