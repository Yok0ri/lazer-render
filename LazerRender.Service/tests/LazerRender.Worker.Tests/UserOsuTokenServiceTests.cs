using System.Net;
using System.Text;
using LazerRender.Api.Data;
using LazerRender.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// Covers the "use the queuing player's own osu! credential" path: the service must hand the engine
/// a valid access token, persist the rotated refresh token, and stay inert when there is nothing to
/// refresh (so the worker can fall back to the bot credential).
/// </summary>
public sealed class UserOsuTokenServiceTests
{
    private const string user_id = "u1";

    [Fact]
    public async Task Returns_null_when_the_user_has_no_stored_credential()
    {
        await using var harness = await Harness.CreateAsync(storedRefreshToken: null);

        Assert.Null(await harness.Service.GetTokenAsync(user_id, CancellationToken.None));
        Assert.Equal(0, harness.Handler.Requests);
    }

    [Fact]
    public async Task Returns_null_for_an_unknown_user()
    {
        await using var harness = await Harness.CreateAsync("rt-1");

        Assert.Null(await harness.Service.GetTokenAsync("someone-else", CancellationToken.None));
        Assert.Equal(0, harness.Handler.Requests);
    }

    [Fact]
    public async Task Returns_null_for_a_blank_user_id()
    {
        await using var harness = await Harness.CreateAsync("rt-1");

        Assert.Null(await harness.Service.GetTokenAsync("", CancellationToken.None));
        Assert.Equal(0, harness.Handler.Requests);
    }

    [Fact]
    public async Task Refreshes_the_stored_token_and_persists_the_rotated_value()
    {
        await using var harness = await Harness.CreateAsync("rt-1");

        OsuAccessToken? token = await harness.Service.GetTokenAsync(user_id, CancellationToken.None);

        Assert.NotNull(token);
        Assert.Equal("at-1", token!.AccessToken);
        Assert.Equal(86400L, token.ExpiresIn);
        Assert.Equal(1, harness.Handler.Requests);

        // osu! rotates refresh tokens; the rotated value must be written back, readable with the same
        // Data Protection purpose AuthService uses.
        await using var scope = harness.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await db.OAuthTokens.SingleAsync(t => t.UserId == user_id);
        Assert.Equal("rt-2", harness.Unprotect(stored.RefreshTokenEncrypted));
    }

    [Fact]
    public async Task Caches_the_access_token_so_repeated_renders_do_not_re_refresh()
    {
        await using var harness = await Harness.CreateAsync("rt-1");

        await harness.Service.GetTokenAsync(user_id, CancellationToken.None);
        await harness.Service.GetTokenAsync(user_id, CancellationToken.None);

        Assert.Equal(1, harness.Handler.Requests);
    }

    [Fact]
    public async Task Invalidate_forces_a_new_refresh()
    {
        await using var harness = await Harness.CreateAsync("rt-1");

        await harness.Service.GetTokenAsync(user_id, CancellationToken.None);
        harness.Service.Invalidate(user_id);
        await harness.Service.GetTokenAsync(user_id, CancellationToken.None);

        Assert.Equal(2, harness.Handler.Requests);
    }

    [Fact]
    public async Task Returns_null_when_the_refresh_fails_so_the_caller_can_fall_back()
    {
        await using var harness = await Harness.CreateAsync("rt-1", fail: true);

        Assert.Null(await harness.Service.GetTokenAsync(user_id, CancellationToken.None));
    }

    [Fact]
    public async Task Skips_the_refresh_when_oauth_is_not_configured()
    {
        await using var harness = await Harness.CreateAsync("rt-1", oauthConfigured: false);

        Assert.Null(await harness.Service.GetTokenAsync(user_id, CancellationToken.None));
        Assert.Equal(0, harness.Handler.Requests);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly EphemeralDataProtectionProvider protection;

        public ServiceProvider Provider { get; }
        public StubHandler Handler { get; }
        public UserOsuTokenService Service { get; }

        private Harness(
            SqliteConnection connection,
            EphemeralDataProtectionProvider protection,
            ServiceProvider provider,
            StubHandler handler,
            UserOsuTokenService service)
        {
            this.connection = connection;
            this.protection = protection;
            Provider = provider;
            Handler = handler;
            Service = service;
        }

        public string Unprotect(string value) =>
            protection.CreateProtector("OsuOAuth.RefreshToken").Unprotect(value);

        public static async Task<Harness> CreateAsync(
            string? storedRefreshToken, bool fail = false, bool oauthConfigured = true)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
            ServiceProvider provider = services.BuildServiceProvider();

            var protection = new EphemeralDataProtectionProvider();

            if (storedRefreshToken is not null)
            {
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();

                db.Users.Add(new UserEntity { Id = user_id, OsuUserId = 424242, Username = "player" });
                db.OAuthTokens.Add(new OAuthTokenEntity
                {
                    UserId = user_id,
                    RefreshTokenEncrypted = protection
                        .CreateProtector("OsuOAuth.RefreshToken")
                        .Protect(storedRefreshToken),
                });

                await db.SaveChangesAsync();
            }

            var handler = new StubHandler(fail);
            var osu = new OsuOAuthService(
                Microsoft.Extensions.Options.Options.Create(new Api.Configuration.OsuOAuthOptions
                {
                    ClientId = oauthConfigured ? "client-id" : "",
                    ClientSecret = oauthConfigured ? "client-secret" : "",
                }),
                new HttpClient(handler));

            var service = new UserOsuTokenService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                osu,
                protection,
                NullLogger<UserOsuTokenService>.Instance);

            return new Harness(connection, protection, provider, handler, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly bool fail;

        public int Requests { get; private set; }

        public StubHandler(bool fail) => this.fail = fail;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            if (fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json"),
                });
            }

            const string body = """
                {"access_token":"at-1","refresh_token":"rt-2","expires_in":86400,"token_type":"Bearer"}
                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
