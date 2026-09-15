using System.Text.Json;
using LazerRender.Contracts;

namespace LazerRender.Api.Services;

/// <summary>
/// Validates a preset's config document before it is stored.
///
/// Presets were stored verbatim with no length or schema check, so an authenticated user could persist
/// up to the 220 MB body limit per preset — and every <c>GET /presets</c> returns the document — into
/// the SQLite file. A preset is a small settings document, so it is validated the same way a job's
/// config is, and capped.
/// </summary>
internal static class PresetGuard
{
    public const int MaxConfigJsonBytes = 16 * 1024;

    /// <summary>Keeps one account from filling the database with preset rows. A constant on purpose:
    /// this is an arbitrary ceiling, not something a deployment needs to tune.</summary>
    public const int MaxPresetsPerUser = 50;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Returns a rejection reason, or <c>null</c> when the document is acceptable.</summary>
    public static string? Validate(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return "configJson is required.";

        if (configJson.Length > MaxConfigJsonBytes)
            return $"configJson exceeds the {MaxConfigJsonBytes / 1024} KB limit.";

        RenderConfig? config;

        try
        {
            config = JsonSerializer.Deserialize<RenderConfig>(configJson, JsonOptions);
        }
        catch (JsonException e)
        {
            return $"configJson is not valid JSON: {e.Message}";
        }

        if (config is null)
            return "configJson is not a render configuration document.";

        IReadOnlyList<string> errors = RenderConfigValidator.Validate(config);
        return errors.Count > 0 ? string.Join(' ', errors) : null;
    }
}
