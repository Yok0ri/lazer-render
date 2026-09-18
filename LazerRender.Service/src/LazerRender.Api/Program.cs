using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LazerRender.Api.Configuration;
using LazerRender.Api.Data;
using LazerRender.Api.Hubs;
using LazerRender.Api.Services;
using LazerRender.Api.Services.Logging;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
#if DEBUG
using Microsoft.OpenApi.Models;
#endif

var builder = WebApplication.CreateBuilder(args);

// Allow large skin (.osk) uploads. The per-endpoint skin cap is 200 MB, so accept a little more
// to cover multipart framing overhead.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 220L * 1024 * 1024;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 220L * 1024 * 1024;
});

// --- Reverse proxy / forwarded headers ---
// The shipped deployment terminates TLS at Cloudflare/NGinx/Caddy and speaks plain HTTP over loopback.
// Without this the app cannot tell HTTPS from HTTP, so cookies lose `Secure` and the HTTPS redirect is
// inert; and every request appears to come from the proxy, so the rate limiter collapses to a single
// shared bucket. See ProxyConfiguration for the trust rules.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    ProxyConfiguration.TrustList trust = ProxyConfiguration.Parse(
        builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>(),
        builder.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>());

    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // The framework ships its own defaults (loopback plus a private range). Clear them and trust
    // exactly what the operator configured.
    options.KnownProxies.Clear();
    options.KnownNetworks.Clear();

    foreach (System.Net.IPAddress proxy in trust.Proxies)
        options.KnownProxies.Add(proxy);

    foreach (ProxyConfiguration.NetworkPrefix network in trust.Networks)
    {
        options.KnownNetworks.Add(
            new Microsoft.AspNetCore.HttpOverrides.IPNetwork(network.Prefix, network.PrefixLength));
    }
});

// --- JSON serialization ---
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    });

// --- Options ---
builder.Services.AddOptions<StorageOptions>().Bind(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddOptions<RendererOptions>().Bind(builder.Configuration.GetSection(RendererOptions.SectionName));
builder.Services.AddOptions<QuotaOptions>().Bind(builder.Configuration.GetSection(QuotaOptions.SectionName));
builder.Services.AddOptions<AdminOptions>().Bind(builder.Configuration.GetSection(AdminOptions.SectionName));
builder.Services.AddOptions<ObservabilityOptions>().Bind(builder.Configuration.GetSection(ObservabilityOptions.SectionName));

// --- Storage (SQLite) ---
// The data directory is resolved the same way StorageService resolves it, so the database file
// and the service filesystem layout always agree on one root.
var configuredDataDir = builder.Configuration[$"{StorageOptions.SectionName}:DataDirectory"];
var dataDirectory = string.IsNullOrWhiteSpace(configuredDataDir)
    ? Path.Combine(builder.Environment.ContentRootPath, "data")
    : Path.GetFullPath(Path.IsPathRooted(configuredDataDir)
        ? configuredDataDir
        : Path.Combine(builder.Environment.ContentRootPath, configuredDataDir));
Directory.CreateDirectory(dataDirectory);

var configuredConnection = builder.Configuration.GetConnectionString("Default");
var connectionString = string.IsNullOrWhiteSpace(configuredConnection)
    ? $"Data Source={Path.Combine(dataDirectory, "lazerrender.db")}"
    : configuredConnection;
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<RenderSizeEstimator>();
builder.Services.AddScoped<QuotaService>();

// --- Data Protection (refresh-token encryption key ring) ---
// The ring is the master key for every stored osu! refresh token, so it is created owner-only (0700)
// instead of inheriting a world-readable umask, and it can be encrypted at rest with a certificate
// supplied out of band.
var keysDirectory = Path.Combine(builder.Environment.ContentRootPath, "keys");
FilePermissions.RestrictKeyRing(keysDirectory);

IDataProtectionBuilder dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
    .SetApplicationName("LazerRender.Api");

string? certificatePath = builder.Configuration["DataProtection:CertificatePath"];
bool keyRingEncrypted = !string.IsNullOrWhiteSpace(certificatePath);

if (keyRingEncrypted)
{
    string certificateKeyPath = builder.Configuration["DataProtection:CertificateKeyPath"] ?? "";
    string? certificatePassword = builder.Configuration["DataProtection:CertificatePassword"];

    // A PEM pair (cert + key) or a single PFX/PKCS#12 file.
    var certificate = string.IsNullOrWhiteSpace(certificateKeyPath)
        ? new System.Security.Cryptography.X509Certificates.X509Certificate2(certificatePath!, certificatePassword)
        : System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certificatePath!, certificateKeyPath);

    dataProtection.ProtectKeysWithCertificate(certificate);
}

// --- Authentication (session cookie) ---
// The session has an absolute lifetime: a stolen cookie cannot be kept alive indefinitely by use.
var absoluteSessionLifetime = TimeSpan.FromDays(builder.Configuration.GetValue("Auth:SessionLifetimeDays", 7));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "lazerrender.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // TLS terminates at the proxy, so in production the cookie is always Secure even though the app
        // itself sees plain HTTP. Development follows the request scheme so a local http://localhost
        // run still works.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = absoluteSessionLifetime;
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        // Belt-and-braces for the absolute lifetime: even if sliding expiration is ever re-enabled, a
        // session older than the limit is rejected rather than reissued.
        options.Events.OnValidatePrincipal = context =>
        {
            string? authTime = context.Principal?.FindFirst("auth_time")?.Value;

            if (authTime is not null
                && long.TryParse(authTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out long issuedUnix)
                && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(issuedUnix) > absoluteSessionLifetime)
            {
                context.RejectPrincipal();
            }

            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

// --- Rate limiting (simple, per-client-IP) ---
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Job creation is far heavier than a read (multipart upload, staging, metadata lookup), so it gets
    // its own, much tighter partition on top of the per-user quota.
    options.AddPolicy("jobs", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Sign-in endpoints are unauthenticated and contact osu! on every attempt, so they get the
    // tightest partition.
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// --- osu! OAuth v2 ---
builder.Services.AddOptions<OsuOAuthOptions>()
    .Bind(builder.Configuration.GetSection(OsuOAuthOptions.SectionName));
builder.Services.AddHttpClient<OsuOAuthService>();
builder.Services.AddScoped<AuthService>();

// --- Observability (Phase 8.2 logging pipeline) ---
// One redactor and two bounded ring buffers. The redactor knows the credentials this process holds,
// so service logs are redacted too; the provider feeds the service buffer from ILogger, and the render
// runner feeds the engine buffer from the child process's stdout/stderr. Nothing is persisted.
builder.Services.AddSingleton(_ => LogRedactor.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<ServiceLogRingBuffer>();
builder.Services.AddSingleton<EngineLogRingBuffer>();
builder.Services.AddSingleton<ILoggerProvider, RingBufferLoggerProvider>();

// --- Admin observability (Phase 8.3) ---
// The Render PC summary is collected once at startup by a hosted service; the log streams are polled
// by the panel while it is open, with an idle sweeper so nothing is retained after it closes.
builder.Services.AddSingleton<SystemInfoService>();
builder.Services.AddSingleton<LogStreamService>();
builder.Services.AddHostedService<SystemInfoWarmupService>();
builder.Services.AddHostedService<LogRetentionService>();

// --- Realtime progress + render worker ---
builder.Services.AddSignalR();
builder.Services.AddSingleton<JobCancellationService>();
builder.Services.AddSingleton<RenderLockService>();
builder.Services.AddSingleton<JobCreationGate>();
builder.Services.AddSingleton<EncoderResolver>();
builder.Services.AddSingleton<RendererProcessRunner>();
builder.Services.AddSingleton<AssetImportRunner>();
builder.Services.AddSingleton<MapMetadataService>();
builder.Services.AddSingleton<OsuBotAuthService>();
builder.Services.AddSingleton<UserOsuTokenService>();
builder.Services.AddHostedService<RenderWorker>();
builder.Services.AddHostedService<RetentionSweeper>();

#if DEBUG
// Swagger is a development tool, so it is compiled out of Release builds entirely: the published
// bundle then does not carry the Swashbuckle dependency at all.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "LazerRender API", Version = "v1" });
});
#endif

builder.Services.AddHealthChecks();

var app = builder.Build();

// Ensure the SQLite schema exists and add any columns introduced after the initial schema.
// This is a stopgap until EF Core migrations replace EnsureCreated (see DEPLOYMENT.md).
foreach (string warning in DatabaseInitializer.Initialize(app.Services))
    app.Logger.LogWarning("{Warning}", warning);

// Make the observability pipeline's effective shape visible at startup: an operator debugging a
// missing console stream should not have to guess whether capture is on and at which level.
{
    var observability = app.Services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
    var serviceBuffer = app.Services.GetRequiredService<ServiceLogRingBuffer>();
    var engineBuffer = app.Services.GetRequiredService<EngineLogRingBuffer>();

    app.Logger.LogInformation(
        "Log pipeline: service buffer {ServiceSize} entries at {ServiceLevel}+, engine buffer {EngineSize} entries at {EngineLevel}+; debug instrumentation {DebugState}.",
        serviceBuffer.Capacity, serviceBuffer.MinimumSeverity,
        engineBuffer.Capacity, engineBuffer.MinimumSeverity,
        DebugMode.Enabled ? "enabled" : "disabled");
}

// Leaderboards silently stay offline when the requested osu! scopes are too narrow, and users who
// signed in before `public` was added keep their old grant — so make the effective scopes visible.
{
    var oauth = app.Services.GetRequiredService<IOptions<OsuOAuthOptions>>().Value;

    if (string.IsNullOrWhiteSpace(oauth.ClientId))
    {
        app.Logger.LogWarning("osu! OAuth is not configured (Osu:OAuth:ClientId is empty); sign-in is disabled.");
    }
    else
    {
        // The shipped default used to be a localhost URL, which only works on a developer machine and
        // fails at the callback anywhere else. Require an explicit value rather than guessing.
        if (string.IsNullOrWhiteSpace(oauth.RedirectUri))
        {
            throw new InvalidOperationException(
                "Osu:OAuth:RedirectUri must be set when Osu:OAuth:ClientId is configured "
                + "(e.g. https://render.example.com/auth/callback).");
        }

        if (!oauth.Scopes.Contains("public", StringComparison.OrdinalIgnoreCase))
            app.Logger.LogWarning(
                "osu! OAuth scopes are \"{Scopes}\" but do not include `public`: online beatmap leaderboards will fail. "
                + "Add it and have existing users sign in again.",
                oauth.Scopes);
        else
            app.Logger.LogInformation("osu! OAuth scopes: {Scopes}.", oauth.Scopes);
    }
}

// The key ring is only as strong as its storage: say which mode is in force.
if (keyRingEncrypted)
{
    app.Logger.LogInformation("Data Protection key ring is encrypted with the configured certificate.");
}
else
{
    app.Logger.LogWarning(
        "Data Protection key ring is stored unencrypted. It is owner-only (0700) on the host, but anyone "
        + "who can read it can decrypt every stored osu! credential. Set DataProtection:CertificatePath to "
        + "encrypt it at rest, and never bake keys/ into a container image layer.");
}

// Warn until an admin exists, so an operator knows which way in applies before exposing the instance.
{
    var admin = app.Services.GetRequiredService<IOptions<AdminOptions>>().Value;

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    if (!db.Users.Any(u => u.Role == "admin"))
    {
        if (string.IsNullOrWhiteSpace(admin.BootstrapToken) && string.IsNullOrWhiteSpace(admin.OsuUserIds))
        {
            app.Logger.LogWarning(
                "No admin account exists and neither Admin:OsuUserIds nor Admin:BootstrapToken is "
                + "configured, so there is no way to become an admin. Set one before exposing this instance.");
        }
        else
        {
            app.Logger.LogWarning(
                "No admin account exists yet. Claim it before exposing this instance: set "
                + "Admin:BootstrapToken and visit /auth/login?bootstrap=<token> once, or list your osu! id "
                + "in Admin:OsuUserIds.");
        }
    }
}

// The host boundary is only meaningful if it is configured; a wildcard accepts any Host header.
{
    string? allowedHosts = app.Configuration["AllowedHosts"];

    if (!app.Environment.IsDevelopment()
        && (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Trim() == "*"))
    {
        app.Logger.LogWarning(
            "AllowedHosts is not restricted, so any Host header is accepted. Set AllowedHosts to the "
            + "deployment hostname (or restrict it at the proxy).");
    }
}

#if DEBUG
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
#endif

if (!app.Environment.IsDevelopment())
{
    // HSTS only makes sense once the app knows it is behind TLS (see the forwarded-headers setup above)
    // and only in production, so a local http run is never pinned to HTTPS by its own browser.
    app.UseHsts();
}

// Security headers on every response, including redirects and errors.
app.Use(async (context, next) =>
{
    SecurityHeaders.Apply(context.Response.Headers);
    await next();
});

// Must run before anything that reads the request scheme or the client address: HTTPS redirection,
// the rate limiter and the cookie policy all depend on it. A request from an address that is not a
// configured proxy keeps its real socket address and plain HTTP scheme.
app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// CSRF: state-changing requests must carry the custom header, which a cross-site page cannot set
// without a CORS preflight (and no CORS policy is configured). See RequestGuards.
app.Use(async (context, next) =>
{
    if (RequestGuards.RequiresHeader(context.Request.Method) && !RequestGuards.HasHeader(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "csrf_header_required" });
        return;
    }

    await next();
});

app.MapControllers();
app.MapHub<JobsHub>("/hubs/jobs");
app.MapHealthChecks("/health");

app.Run();
