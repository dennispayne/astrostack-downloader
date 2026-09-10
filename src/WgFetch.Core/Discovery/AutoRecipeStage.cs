using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Model;

namespace WgFetch.Core.Discovery;

/// <summary>
/// Cached auto-generated recipe stage: a previously learned resolution from
/// <see cref="Recipes.AutoRecipeCache"/>, so unchanged vendors skip the LLM entirely
/// (docs/REQUIREMENTS.md, "Discovery pipeline", stage 2/self-healing cache).
/// </summary>
public sealed class AutoRecipeStage : RecipeBackedStage
{
    public AutoRecipeStage(IHttpGateway http, ILogger? logger = null) : base(http, logger)
    {
    }

    public override DiscoveryStage Stage => DiscoveryStage.AutoRecipe;

    protected override Recipe? SelectRecipe(DiscoveryRequest request) => request.AutoRecipe;
}
