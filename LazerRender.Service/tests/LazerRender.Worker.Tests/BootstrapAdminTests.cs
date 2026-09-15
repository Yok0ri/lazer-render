using LazerRender.Api.Data;
using LazerRender.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// H-5 (audit): the first admin account must not be claimable by whoever logs in first. Promotion now
/// requires an explicitly configured bootstrap token, and is only possible while no admin exists.
/// </summary>
public sealed class BootstrapAdminTests
{
    [Fact]
    public async Task Matching_token_on_a_fresh_instance_may_claim_admin()
    {
        await using var db = CreateDb();

        Assert.True(await AuthService.MayClaimBootstrapAdminAsync(db, "s3cret", "s3cret", CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_token_cannot_claim_admin()
    {
        await using var db = CreateDb();

        Assert.False(await AuthService.MayClaimBootstrapAdminAsync(db, "s3cret", "wrong", CancellationToken.None));
    }

    [Fact]
    public async Task Absent_presented_token_cannot_claim_admin()
    {
        await using var db = CreateDb();

        Assert.False(await AuthService.MayClaimBootstrapAdminAsync(db, "s3cret", null, CancellationToken.None));
        Assert.False(await AuthService.MayClaimBootstrapAdminAsync(db, "s3cret", "", CancellationToken.None));
    }

    /// <summary>
    /// With no token configured there is no bootstrap path at all — this is what stops an exposed
    /// instance from being seized by the first visitor.
    /// </summary>
    [Fact]
    public async Task No_configured_token_disables_bootstrap_entirely()
    {
        await using var db = CreateDb();

        Assert.False(await AuthService.MayClaimBootstrapAdminAsync(db, "", "", CancellationToken.None));
        Assert.False(await AuthService.MayClaimBootstrapAdminAsync(db, "", "anything", CancellationToken.None));
    }

    [Fact]
    public async Task Bootstrap_is_refused_once_an_admin_exists()
    {
        await using var db = CreateDb();
        db.Users.Add(new UserEntity { Id = "u1", OsuUserId = 1, Username = "admin", Role = "admin" });
        await db.SaveChangesAsync();

        Assert.False(await AuthService.MayClaimBootstrapAdminAsync(db, "s3cret", "s3cret", CancellationToken.None));
    }

    [Fact]
    public async Task A_non_admin_user_does_not_block_bootstrap()
    {
        await using var db = CreateDb();
        db.Users.Add(new UserEntity { Id = "u1", OsuUserId = 1, Username = "someone", Role = "user" });
        await db.SaveChangesAsync();

        Assert.True(await AuthService.MayClaimBootstrapAdminAsync(db, "s3cret", "s3cret", CancellationToken.None));
    }

    private static AppDbContext CreateDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
