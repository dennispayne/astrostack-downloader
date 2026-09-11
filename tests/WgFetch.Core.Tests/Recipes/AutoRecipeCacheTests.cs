using WgFetch.Core.Model;
using WgFetch.Core.Recipes;
using Xunit;

namespace WgFetch.Core.Tests.Recipes;

/// <summary>
/// Covers atomic single-writer persistence, schema-version rejection, and the structural guard that
/// only <see cref="RecipeOrigin.Auto"/> recipes may ever be cached (docs/REQUIREMENTS.md, "Recipes":
/// "never writes anything derived from an unverified candidate").
/// </summary>
public sealed class AutoRecipeCacheTests
{
    private static Recipe AutoRecipe(string componentId, string url) => new()
    {
        PackageId = $"AstroStack.{componentId}",
        ComponentId = componentId,
        DisplayName = componentId,
        SourceKind = RecipeSourceKind.DirectUrl,
        DirectUrl = url,
        Allowlist = [new Uri(url).Host],
        Origin = RecipeOrigin.Auto,
    };

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wgfetch-test-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task SaveAsync_ThenTryLoad_RoundTrips()
    {
        var dir = NewTempDir();
        try
        {
            var cache = new AutoRecipeCache(dir);
            var recipe = AutoRecipe("demo", "https://example.com/demo.exe");

            await cache.SaveAsync(recipe, CancellationToken.None);
            var loaded = cache.TryLoad("demo");

            Assert.NotNull(loaded);
            Assert.Equal("https://example.com/demo.exe", loaded!.DirectUrl);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_RejectsNonAutoOriginRecipes()
    {
        var dir = NewTempDir();
        try
        {
            var cache = new AutoRecipeCache(dir);
            var recipe = AutoRecipe("demo", "https://example.com/demo.exe") with { Origin = RecipeOrigin.Seed };

            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.SaveAsync(recipe, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_UnknownSchemaVersion_ReturnsNull_DoesNotThrow()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "demo.json"),
                "{\"schemaVersion\":9999,\"packageId\":\"AstroStack.Demo\",\"componentId\":\"demo\",\"displayName\":\"Demo\",\"sourceKind\":\"DirectUrl\"}");

            var cache = new AutoRecipeCache(dir);
            Assert.Null(cache.TryLoad("demo"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_CorruptJson_ReturnsNull_DoesNotThrow()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "demo.json"), "{ not valid json at all");

            var cache = new AutoRecipeCache(dir);
            Assert.Null(cache.TryLoad("demo"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        var dir = NewTempDir();
        try
        {
            var cache = new AutoRecipeCache(dir);
            Assert.Null(cache.TryLoad("does-not-exist"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTempFilesBehind_OnSuccess()
    {
        var dir = NewTempDir();
        try
        {
            var cache = new AutoRecipeCache(dir);
            await cache.SaveAsync(AutoRecipe("demo", "https://example.com/demo.exe"), CancellationToken.None);

            var files = Directory.GetFiles(dir);
            Assert.Single(files);
            Assert.EndsWith("demo.json", files[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
