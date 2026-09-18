using System.Text.RegularExpressions;

namespace LazerRender.Api.Services.Logging;

/// <summary>
/// The single redaction point for the Phase 8.2 pipeline. Security audit finding M-7/H-4 requires that
/// redaction happen <em>before</em> anything reaches a browser: because every sink is fed through this
/// class, a record can never enter a ring buffer in unredacted form, regardless of which producer or
/// sink is involved.
///
/// Two classes of secret are masked:
/// <list type="bullet">
/// <item>known values: configuration credentials (OAuth client secret, bot token, avatar key, bootstrap
/// token) plus the per-render credentials handed to <see cref="EngineLogForwarder"/>; and</item>
/// <item>shapes: anything that looks like <c>Bearer <token></c>, which catches a credential we
/// were never told about.</item>
/// </list>
/// </summary>
public sealed class LogRedactor
{
    private const string Marker = "[redacted]";

    /// <summary>Catches a credential in a shape we were not handed explicitly.</summary>
    private static readonly Regex BearerPattern = new(
        @"\bBearer\s+[^\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Longer secrets are replaced first so a secret that contains another is fully masked.</summary>
    private readonly string[] knownSecrets;

    public LogRedactor(IEnumerable<string?> knownSecrets)
    {
        this.knownSecrets = knownSecrets
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    /// <summary>A redactor with no known values; still masks bearer-token shapes.</summary>
    public static LogRedactor Empty { get; } = new(Array.Empty<string>());

    /// <summary>
    /// Builds a redactor from the credentials the service holds in configuration. Called once at
    /// startup so service-side logs (not just engine logs) are covered.
    /// </summary>
    public static LogRedactor FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Every place the service stores a credential. Keep this list in step with the audit's
        // credential inventory when a new secret is introduced.
        string?[] keys =
        {
            configuration["Osu:OAuth:ClientSecret"],
            configuration["Renderer:AvatarApiKey"],
            configuration["Renderer:OsuBotToken"],
            configuration["Renderer:OsuBotRefreshToken"],
            configuration["Admin:BootstrapToken"],
            configuration["DataProtection:CertificatePassword"],
            Environment.GetEnvironmentVariable("OSU_API_KEY"),
        };

        return new LogRedactor(keys);
    }

    /// <summary>
    /// Masks the known values plus any per-call values (typically the credentials minted for one
    /// render), then normalises bearer-token shapes.
    /// </summary>
    public string Redact(string line, IReadOnlyList<string>? extraSecrets = null)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        foreach (string secret in knownSecrets)
            line = line.Replace(secret, Marker, StringComparison.Ordinal);

        if (extraSecrets is not null)
        {
            foreach (string secret in extraSecrets)
            {
                if (!string.IsNullOrEmpty(secret))
                    line = line.Replace(secret, Marker, StringComparison.Ordinal);
            }
        }

        return BearerPattern.Replace(line, $"Bearer {Marker}");
    }

    /// <summary>
    /// Static convenience for callers that only have a per-render secret list (the engine log bridge).
    /// Equivalent to <see cref="Empty"/> plus the supplied values.
    /// </summary>
    public static string RedactStatic(string line, IReadOnlyList<string>? secrets) =>
        Empty.Redact(line, secrets);
}