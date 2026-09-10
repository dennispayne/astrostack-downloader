using System.Text.Json;
using System.Text.Json.Serialization;
using WgFetch.Core.Model;

namespace WgFetch.Core.Recipes;

/// <summary>
/// Source-generated <c>System.Text.Json</c> context for recipe (de)serialization. The library is
/// <c>IsAotCompatible</c>, so reflection-based serialization is never used for recipes.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Recipe))]
[JsonSerializable(typeof(Recipe[]))]
[JsonSerializable(typeof(List<Recipe>))]
internal sealed partial class RecipeJsonContext : JsonSerializerContext
{
}
