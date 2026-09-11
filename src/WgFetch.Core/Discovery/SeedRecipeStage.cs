using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Model;

namespace WgFetch.Core.Discovery;

/// <summary>Bundled seed recipe stage for the astro stack (docs/REQUIREMENTS.md, "Discovery pipeline", stage 2).</summary>
public sealed class SeedRecipeStage : RecipeBackedStage
{
    public SeedRecipeStage(IHttpGateway http, ILogger? logger = null) : base(http, logger)
    {
    }

    public override DiscoveryStage Stage => DiscoveryStage.SeedRecipe;

    protected override Recipe? SelectRecipe(DiscoveryRequest request) => request.SeedRecipe;
}
