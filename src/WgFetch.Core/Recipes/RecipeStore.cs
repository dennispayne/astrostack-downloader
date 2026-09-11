using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WgFetch.Core.Model;

namespace WgFetch.Core.Recipes;

/// <summary>
/// Loads and ranks recipes from the three sources described in docs/REQUIREMENTS.md ("Recipes"):
/// bundled seed recipes, user-supplied recipes and the auto-generated recipe cache. Precedence is
/// <c>User &gt; Seed &gt; Auto</c>. Lookup is by package id, component id, alias or tag.
/// </summary>
public sealed class RecipeStore
{
    private readonly List<Recipe> _user = new();
    private readonly List<Recipe> _seed = new();
    private readonly List<Recipe> _auto = new();
    private readonly ILogger _logger;

    public RecipeStore(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>All recipes currently loaded, in precedence order (user, then seed, then auto).</summary>
    public IReadOnlyList<Recipe> All => _user.Concat(_seed).Concat(_auto).ToArray();

    public IReadOnlyList<Recipe> UserRecipes => _user;

    public IReadOnlyList<Recipe> SeedRecipes => _seed;

    public IReadOnlyList<Recipe> AutoRecipes => _auto;

    /// <summary>
    /// Loads the seed recipes bundled with the binary as embedded JSON resources under
    /// <c>Recipes/Seed/*.json</c>.
    /// </summary>
    public void LoadSeedRecipes(Assembly? assembly = null)
    {
        assembly ??= typeof(RecipeStore).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains(".Recipes.Seed.", StringComparison.Ordinal) ||
                !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' could not be opened.");
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var recipe = ParseRecipe(json, resourceName);
            _seed.Add(recipe with { Origin = RecipeOrigin.Seed });
        }
    }

    /// <summary>Adds a user-supplied recipe, e.g. from <c>--recipe</c> or a batch manifest column.</summary>
    public void AddUserRecipe(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ValidateSchema(recipe, recipe.ComponentId);
        _user.Add(recipe with { Origin = RecipeOrigin.User });
    }

    /// <summary>Parses and adds a user recipe supplied inline as JSON (<c>--recipe-inline</c>).</summary>
    public Recipe AddUserRecipeInline(string json)
    {
        var recipe = ParseRecipe(json, "<inline>");
        AddUserRecipe(recipe);
        return recipe;
    }

    /// <summary>Parses and adds a user recipe file (<c>--recipe &lt;path&gt;</c>).</summary>
    public Recipe AddUserRecipeFile(string path)
    {
        var json = File.ReadAllText(path);
        var recipe = ParseRecipe(json, path);
        AddUserRecipe(recipe);
        return recipe;
    }

    /// <summary>
    /// Loads every recipe previously learned into the auto-recipe cache directory
    /// (see <see cref="AutoRecipeCache"/>). Corrupt or unknown-schema entries are skipped with a
    /// warning rather than aborting the whole load.
    /// </summary>
    public void LoadAutoRecipes(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(file);
                var recipe = ParseRecipe(json, file);
                _auto.Add(recipe with { Origin = RecipeOrigin.Auto });
            }
            catch (RecipeSchemaException ex)
            {
                _logger.LogWarning("Skipping auto-recipe '{File}': {Message}", file, ex.Message);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Skipping corrupt auto-recipe '{File}': {Message}", file, ex.Message);
            }
        }
    }

    /// <summary>Adds an already-materialized auto-generated recipe (e.g. one just learned this run).</summary>
    public void AddAutoRecipe(Recipe recipe)
    {
        ValidateSchema(recipe, recipe.ComponentId);
        _auto.Add(recipe with { Origin = RecipeOrigin.Auto });
    }

    /// <summary>
    /// Finds the highest-precedence recipe matching a package id, component id, alias or tag
    /// (case-insensitive). User recipes outrank seed recipes, which outrank auto-generated ones.
    /// </summary>
    public Recipe? Find(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        return FindIn(_user, query) ?? FindIn(_seed, query) ?? FindIn(_auto, query);
    }

    private static Recipe? FindIn(List<Recipe> recipes, string query)
    {
        foreach (var recipe in recipes)
        {
            if (Matches(recipe, query))
            {
                return recipe;
            }
        }

        return null;
    }

    private static bool Matches(Recipe recipe, string query)
    {
        if (string.Equals(recipe.ComponentId, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(recipe.PackageId, query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var alias in recipe.Aliases)
        {
            if (string.Equals(alias, query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var tag in recipe.Tags)
        {
            if (string.Equals(tag, query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Recipe ParseRecipe(string json, string source)
    {
        Recipe? recipe;
        try
        {
            recipe = JsonSerializer.Deserialize(json, RecipeJsonContext.Default.Recipe);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Recipe '{source}' is not valid JSON: {ex.Message}", ex);
        }

        if (recipe is null)
        {
            throw new JsonException($"Recipe '{source}' deserialized to null.");
        }

        ValidateSchema(recipe, source);
        return recipe;
    }

    private static void ValidateSchema(Recipe recipe, string source)
    {
        if (recipe.SchemaVersion != Recipe.CurrentSchemaVersion)
        {
            throw new RecipeSchemaException(source, recipe.SchemaVersion);
        }
    }
}
