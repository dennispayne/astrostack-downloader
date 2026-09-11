using WgFetch.Core.Model;
using WgFetch.Core.Recipes;
using Xunit;

namespace WgFetch.Core.Tests.Recipes;

/// <summary>
/// Covers seed-recipe loading from embedded resources, user/seed/auto precedence, lookup by
/// package/component/alias/tag, and schema-version rejection (docs/REQUIREMENTS.md, "Recipes").
/// </summary>
public sealed class RecipeStoreTests
{
    [Fact]
    public void LoadSeedRecipes_LoadsAllBundledSeeds_WithExpectedComponentIds()
    {
        var store = new RecipeStore();
        store.LoadSeedRecipes();

        var expected = new[] { "nina", "phd2", "ascom-platform", "sharpcap", "astap", "stellarium", "sgp", "pixinsight", "sharpcap-pro" };
        foreach (var id in expected)
        {
            Assert.Contains(store.SeedRecipes, r => r.ComponentId == id);
        }
    }

    [Fact]
    public void SeedRecipes_AuthWalledEntries_HaveRequiresAuthAndReason()
    {
        var store = new RecipeStore();
        store.LoadSeedRecipes();

        foreach (var id in new[] { "pixinsight", "sharpcap-pro" })
        {
            var recipe = store.Find(id);
            Assert.NotNull(recipe);
            Assert.True(recipe!.RequiresAuth);
            Assert.False(string.IsNullOrWhiteSpace(recipe.AuthReason));
        }
    }

    [Fact]
    public void Find_MatchesByPackageIdComponentIdAliasAndTag()
    {
        var store = new RecipeStore();
        store.AddUserRecipe(new Recipe
        {
            PackageId = "AstroStack.Demo",
            ComponentId = "demo",
            DisplayName = "Demo App",
            SourceKind = RecipeSourceKind.DirectUrl,
            DirectUrl = "https://example.com/demo.exe",
            Allowlist = ["example.com"],
            Aliases = ["demoapp"],
            Tags = ["astro"],
        });

        Assert.NotNull(store.Find("AstroStack.Demo"));
        Assert.NotNull(store.Find("demo"));
        Assert.NotNull(store.Find("demoapp"));
        Assert.NotNull(store.Find("astro"));
        Assert.Null(store.Find("nonexistent"));
    }

    [Fact]
    public void Precedence_UserOutranksSeedOutranksAuto()
    {
        var store = new RecipeStore();
        Recipe Make(RecipeOrigin origin, string url) => new()
        {
            PackageId = "AstroStack.Demo",
            ComponentId = "demo",
            DisplayName = "Demo",
            SourceKind = RecipeSourceKind.DirectUrl,
            DirectUrl = url,
            Allowlist = ["example.com"],
            Origin = origin,
        };

        store.AddAutoRecipe(Make(RecipeOrigin.Auto, "https://example.com/auto.exe"));
        store.LoadSeedRecipes(); // unrelated seeds; "demo" is not among them
        store.AddUserRecipe(Make(RecipeOrigin.User, "https://example.com/user.exe"));

        var found = store.Find("demo");
        Assert.NotNull(found);
        Assert.Equal("https://example.com/user.exe", found!.DirectUrl);
    }

    [Fact]
    public void AddUserRecipe_UnknownSchemaVersion_ThrowsRecipeSchemaException()
    {
        var store = new RecipeStore();
        var recipe = new Recipe
        {
            SchemaVersion = 9999,
            PackageId = "AstroStack.Demo",
            ComponentId = "demo",
            DisplayName = "Demo",
            SourceKind = RecipeSourceKind.DirectUrl,
            DirectUrl = "https://example.com/demo.exe",
        };

        Assert.Throws<RecipeSchemaException>(() => store.AddUserRecipe(recipe));
    }

    [Fact]
    public void AddUserRecipeInline_ParsesValidJson()
    {
        var store = new RecipeStore();
        const string json =
            "{\"schemaVersion\":1,\"packageId\":\"AstroStack.Demo\",\"componentId\":\"demo\"," +
            "\"displayName\":\"Demo\",\"sourceKind\":\"DirectUrl\",\"directUrl\":\"https://example.com/demo.exe\"," +
            "\"allowlist\":[\"example.com\"]}";

        var recipe = store.AddUserRecipeInline(json);

        Assert.Equal("demo", recipe.ComponentId);
        Assert.NotNull(store.Find("demo"));
    }

    [Fact]
    public void LoadAutoRecipes_SkipsCorruptOrUnknownSchemaEntries_WithoutThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wgfetch-test-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "corrupt.json"), "{ not valid json");
            File.WriteAllText(
                Path.Combine(dir, "future-schema.json"),
                "{\"schemaVersion\":9999,\"packageId\":\"x\",\"componentId\":\"x\",\"displayName\":\"x\",\"sourceKind\":\"DirectUrl\"}");
            File.WriteAllText(
                Path.Combine(dir, "good.json"),
                "{\"schemaVersion\":1,\"packageId\":\"AstroStack.Good\",\"componentId\":\"good\",\"displayName\":\"Good\"," +
                "\"sourceKind\":\"DirectUrl\",\"directUrl\":\"https://example.com/good.exe\",\"allowlist\":[\"example.com\"]}");

            var store = new RecipeStore();
            store.LoadAutoRecipes(dir);

            Assert.Single(store.AutoRecipes);
            Assert.Equal("good", store.AutoRecipes[0].ComponentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
