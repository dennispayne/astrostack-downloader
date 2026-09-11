using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WgFetch.Core.Model;

namespace WgFetch.Core.Recipes;

/// <summary>
/// Persists a learned resolution — URL pattern, allowlisted domain, extraction hint, version pattern —
/// after a <em>verified</em> success, so later runs are deterministic and skip the LLM
/// (docs/REQUIREMENTS.md, "Discovery pipeline": "this self-healing cache is a core feature, not an
/// optimization"). Never writes anything derived from an unverified candidate — callers must only
/// pass a <see cref="Recipe"/> built from a <see cref="Model.DiscoveryOutcome.LearnedRecipe"/> that the
/// pipeline produced strictly after <see cref="Verification.VerificationGate"/> accepted the candidate.
/// </summary>
public sealed class AutoRecipeCache
{
    private readonly string _directory;
    private readonly ILogger _logger;

    public AutoRecipeCache(string directory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _logger = logger ?? NullLogger.Instance;
    }

    private string PathFor(string componentId) =>
        Path.Combine(_directory, SanitizeFileName(componentId) + ".json");

    /// <summary>Reads a previously learned recipe for a component, or null if none is cached.</summary>
    public Recipe? TryLoad(string componentId)
    {
        var path = PathFor(componentId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var recipe = JsonSerializer.Deserialize(json, RecipeJsonContext.Default.Recipe);
            if (recipe is null)
            {
                return null;
            }

            if (recipe.SchemaVersion != Recipe.CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "Auto-recipe cache entry '{Path}' has unknown schemaVersion {Version}; ignoring.",
                    path,
                    recipe.SchemaVersion);
                return null;
            }

            return recipe;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Auto-recipe cache entry '{Path}' is corrupt and will be ignored: {Message}", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Atomically persists a learned recipe. Writes to a temp file in the same directory first, then
    /// renames into place, so a single-writer race or a crash mid-write never leaves a truncated or
    /// partially written cache entry behind.
    /// </summary>
    public async Task SaveAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Origin != RecipeOrigin.Auto)
        {
            throw new InvalidOperationException(
                "Only recipes learned from a verified resolution (Origin == Auto) may be cached.");
        }

        Directory.CreateDirectory(_directory);
        var finalPath = PathFor(recipe.ComponentId);
        var tempPath = finalPath + $".{Guid.NewGuid():N}.tmp";

        var json = JsonSerializer.Serialize(recipe, RecipeJsonContext.Default.Recipe);

        await using (var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Atomic on the same volume; a concurrent writer for the same component simply overwrites,
        // and readers never observe a partially written file because the rename is atomic.
        File.Move(tempPath, finalPath, overwrite: true);
        _logger.LogInformation("Learned auto-recipe for '{ComponentId}' cached at {Path}.", recipe.ComponentId, finalPath);
    }

    private static string SanitizeFileName(string componentId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = componentId.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
