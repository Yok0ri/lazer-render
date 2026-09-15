using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LazerRender.Api.Configuration;
using LazerRender.Api.Data;
using LazerRender.Api.Hubs;
using LazerRender.Api.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

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
var keysDirectory = Path.Combine(builder.Environment.ContentRootPath, "keys");
Directory.CreateDirectory(keysDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
    .SetApplicationName("LazerRender.Api");

// --- Authentication (session cookie) ---
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "lazerrender.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
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
});

// --- osu! OAuth v2 ---
builder.Services.AddOptions<OsuOAuthOptions>()
    .Bind(builder.Configuration.GetSection(OsuOAuthOptions.SectionName));
builder.Services.AddHttpClient<OsuOAuthService>();
builder.Services.AddScoped<AuthService>();

// --- Realtime progress + render worker ---
builder.Services.AddSignalR();
builder.Services.AddSingleton<JobCancellationService>();
builder.Services.AddSingleton<RenderLockService>();
builder.Services.AddSingleton<EncoderResolver>();
builder.Services.AddSingleton<RendererProcessRunner>();
builder.Services.AddSingleton<AssetImportRunner>();
builder.Services.AddSingleton<MapMetadataService>();
builder.Services.AddSingleton<OsuBotAuthService>();
builder.Services.AddSingleton<UserOsuTokenService>();
builder.Services.AddHostedService<RenderWorker>();
builder.Services.AddHostedService<RetentionSweeper>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "LazerRender API", Version = "v1" });
});

builder.Services.AddHealthChecks();

var app = builder.Build();

// Ensure the SQLite schema exists and add any columns introduced after the initial schema.
// This is a stopgap until EF Core migrations replace EnsureCreated (see DEPLOYMENT.md).
DatabaseInitializer.Initialize(app.Services);

// Leaderboards silently stay offline when the requested osu! scopes are too narrow, and users who
// signed in before `public` was added keep their old grant — so make the effective scopes visible.
{
    var oauth = app.Services.GetRequiredService<IOptions<OsuOAuthOptions>>().Value;

    if (string.IsNullOrWhiteSpace(oauth.ClientId))
        app.Logger.LogWarning("osu! OAuth is not configured (Osu:OAuth:ClientId is empty); sign-in is disabled.");
    else if (!oauth.Scopes.Contains("public", StringComparison.OrdinalIgnoreCase))
        app.Logger.LogWarning(
            "osu! OAuth scopes are \"{Scopes}\" but do not include `public`: online beatmap leaderboards will fail. "
            + "Add it and have existing users sign in again.",
            oauth.Scopes);
    else
        app.Logger.LogInformation("osu! OAuth scopes: {Scopes}.", oauth.Scopes);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<JobsHub>("/hubs/jobs");
app.MapHealthChecks("/health");

app.Run();
