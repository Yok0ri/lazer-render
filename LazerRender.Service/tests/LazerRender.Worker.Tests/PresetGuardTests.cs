using System.Text.Json;
using LazerRender.Api.Services;
using LazerRender.Contracts;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// M-1 (audit): presets were stored verbatim with no length or schema check, so an authenticated user
/// could persist up to the 220 MB body limit per preset — and every listing returns the document.
/// </summary>
public sealed class PresetGuardTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_default_render_config_is_accepted()
    {
        Assert.Null(PresetGuard.Validate(JsonSerializer.Serialize(new RenderConfig(), JsonOptions)));
    }

    [Fact]
    public void An_oversized_document_is_rejected()
    {
        string oversized = new('x', PresetGuard.MaxConfigJsonBytes + 1);

        string? error = PresetGuard.Validate(oversized);

        Assert.NotNull(error);
        Assert.Contains("KB limit", error);
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        Assert.NotNull(PresetGuard.Validate("{ not json"));
    }

    [Fact]
    public void An_empty_document_is_rejected()
    {
        Assert.NotNull(PresetGuard.Validate(""));
        Assert.NotNull(PresetGuard.Validate("   "));
    }

    /// <summary>A preset is validated like a job config, so out-of-range values cannot be stored.</summary>
    [Fact]
    public void An_out_of_range_value_is_rejected()
    {
        string config = JsonSerializer.Serialize(new RenderConfig { Fps = 15 }, JsonOptions);

        string? error = PresetGuard.Validate(config);

        Assert.NotNull(error);
        Assert.Contains("fps", error);
    }

    [Fact]
    public void An_unknown_hud_component_is_rejected()
    {
        string config = JsonSerializer.Serialize(
            new RenderConfig { Hud = new List<string> { "definitely-not-a-component" } },
            JsonOptions);

        Assert.NotNull(PresetGuard.Validate(config));
    }
}
