namespace LazerRender.Contracts;

/// <summary>
/// Renderer progress phases, matching the stdout contract in WEB_GUI_GUIDE.md:
/// PARSING → RENDERING_FRAMES → RESULTS_TAIL → FINALIZING → DONE.
/// </summary>
public enum RenderPhase
{
    Unknown = 0,
    Parsing = 1,
    RenderingFrames = 2,
    ResultsTail = 3,
    Finalizing = 4,
    Done = 5,
}

public static class RenderPhaseHelpers
{
    public static RenderPhase Parse(string? value) => value switch
    {
        "PARSING" => RenderPhase.Parsing,
        "RENDERING_FRAMES" => RenderPhase.RenderingFrames,
        "RESULTS_TAIL" => RenderPhase.ResultsTail,
        "FINALIZING" => RenderPhase.Finalizing,
        "DONE" => RenderPhase.Done,
        _ => RenderPhase.Unknown,
    };

    public static string ToWire(this RenderPhase phase) => phase switch
    {
        RenderPhase.Parsing => "PARSING",
        RenderPhase.RenderingFrames => "RENDERING_FRAMES",
        RenderPhase.ResultsTail => "RESULTS_TAIL",
        RenderPhase.Finalizing => "FINALIZING",
        RenderPhase.Done => "DONE",
        _ => "UNKNOWN",
    };
}
