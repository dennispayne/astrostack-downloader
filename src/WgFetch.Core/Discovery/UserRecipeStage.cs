using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Model;

namespace WgFetch.Core.Discovery;

/// <summary>Highest-trust discovery stage: a recipe explicitly supplied by the user for this run.</summary>
public sealed class UserRecipeStage : RecipeBackedStage
{
    public UserRecipeStage(IHttpGateway http, ILogger? logger = null) : base(http, logger)
    {
    }

    public override DiscoveryStage Stage => DiscoveryStage.UserRecipe;

    protected override Recipe? SelectRecipe(DiscoveryRequest request) => request.UserRecipe;
}
