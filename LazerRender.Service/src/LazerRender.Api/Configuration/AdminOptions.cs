namespace LazerRender.Api.Configuration;

/// <summary>
/// Administrative bootstrap. OsuUserIds is a comma-separated list of osu! user ids that are
/// granted the <c>admin</c> role (and implicitly allowed) at login. Other users must be
/// explicitly allowed via the admin user-management endpoints.
/// </summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    public string OsuUserIds { get; set; } = "";

    /// <summary>When true, the first user to log in (when no accounts exist yet) is granted admin and allowed.</summary>
    public bool AllowFirstUser { get; set; } = true;
}
