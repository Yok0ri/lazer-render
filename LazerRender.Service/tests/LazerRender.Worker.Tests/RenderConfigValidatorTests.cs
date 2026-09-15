using LazerRender.Api.Services;
using LazerRender.Contracts;
using Xunit;

namespace LazerRender.Worker.Tests;

public sealed class RenderConfigValidatorTests
{
    [Fact]
    public void Default_config_is_valid()
    {
        var errors = RenderConfigValidator.Validate(new RenderConfig());
        Assert.Empty(errors);
    }

    [Fact]
    public void Known_hud_components_are_accepted()
    {
        var config = new RenderConfig { Hud = new List<string> { "hp", "score", "judgements", "aim-error", "cosmetic", "spectators" } };
        var errors = RenderConfigValidator.Validate(config);
        Assert.Empty(errors);
    }

    [Fact]
    public void Empty_hud_list_is_accepted()
    {
        var config = new RenderConfig { Hud = new List<string>() };
        var errors = RenderConfigValidator.Validate(config);
        Assert.Empty(errors);
    }

    [Fact]
    public void Unknown_hud_component_is_rejected()
    {
        var config = new RenderConfig { Hud = new List<string> { "hp", "bogus" } };
        var errors = RenderConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("hud") && e.Contains("bogus"));
    }
}
