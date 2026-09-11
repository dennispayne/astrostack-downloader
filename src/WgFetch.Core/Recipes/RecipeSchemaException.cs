namespace WgFetch.Core.Recipes;

/// <summary>
/// Thrown when a recipe declares a <c>schemaVersion</c> newer or otherwise unknown to this build.
/// Rejecting with a clear message is safer than guessing at an unknown shape (docs/REQUIREMENTS.md,
/// "Recipes": "Schema versioned and documented").
/// </summary>
public sealed class RecipeSchemaException : Exception
{
    public RecipeSchemaException(string source, int schemaVersion)
        : base(
            $"Recipe '{source}' declares schemaVersion {schemaVersion}, but this build of wgfetch only " +
            $"understands schema version {WgFetch.Core.Model.Recipe.CurrentSchemaVersion}. Upgrade wgfetch " +
            "or downgrade the recipe.")
    {
        Source = source;
        SchemaVersion = schemaVersion;
    }

    public new string Source { get; }

    public int SchemaVersion { get; }
}
