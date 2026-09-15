namespace LazerRender.Api.Configuration;

/// <summary>
/// Administrative bootstrap.
///
/// <see cref="OsuUserIds"/> is a comma-separated list of osu! user ids granted the <c>admin</c> role
/// (and implicitly allowed) at login. It ships empty and is meant to be supplied per deployment, for
/// example through the <c>Admin__OsuUserIds</c> environment variable.
///
/// <see cref="BootstrapToken"/> is the one-shot alternative for a fresh instance: while no admin
/// exists, the first sign-in that also presents this token is promoted. With no token configured there
/// is no bootstrap path at all, so an unauthenticated visitor cannot win a race to the first admin
/// account (the previous <c>AllowFirstUser</c> behaviour).
/// </summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    public string OsuUserIds { get; set; } = "";

    /// <summary>
    /// Secret that must accompany the first sign-in on a fresh database to claim the initial admin
    /// account (<c>/auth/login?bootstrap=&lt;token&gt;</c>). Empty disables the bootstrap path.
    /// </summary>
    public string BootstrapToken { get; set; } = "";
}
