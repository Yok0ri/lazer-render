using System.Text.Json.Serialization;

namespace LazerRender.Contracts;

/// <summary>
/// One machine-readable JSON line emitted by LazerRender on stdout.
/// Example: {"type":"progress","phase":"RENDERING_FRAMES","frame":60,"total":450,"fps":123.4}
/// </summary>
public sealed record RenderProgress
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "progress";

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "";

    [JsonPropertyName("frame")]
    public long Frame { get; init; }

    [JsonPropertyName("total")]
    public long? Total { get; init; }

    [JsonPropertyName("fps")]
    public double Fps { get; init; }

    [JsonIgnore]
    public RenderPhase ParsedPhase => RenderPhaseHelpers.Parse(Phase);

    [JsonIgnore]
    public bool IsDone => ParsedPhase == RenderPhase.Done;
}
