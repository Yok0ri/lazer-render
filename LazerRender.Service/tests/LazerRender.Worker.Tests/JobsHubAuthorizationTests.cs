using LazerRender.Api.Data;
using LazerRender.Api.Hubs;
using LazerRender.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// H-1 (audit): the progress hub must apply the same owner scoping as the REST job endpoints. Before
/// this, any authenticated user could subscribe to another user's job id and watch its live progress.
/// </summary>
public sealed class JobsHubAuthorizationTests
{
    [Fact]
    public async Task Owner_may_subscribe_to_their_own_job()
    {
        await using var db = CreateDb();
        string jobId = await SeedJobAsync(db, "user-a");

        Assert.True(await JobsHub.IsOwnedByAsync(db, jobId, "user-a"));
    }

    [Fact]
    public async Task Another_user_may_not_subscribe_to_the_job()
    {
        await using var db = CreateDb();
        string jobId = await SeedJobAsync(db, "user-a");

        Assert.False(await JobsHub.IsOwnedByAsync(db, jobId, "user-b"));
    }

    [Fact]
    public async Task Unknown_job_id_is_rejected()
    {
        await using var db = CreateDb();
        await SeedJobAsync(db, "user-a");

        Assert.False(await JobsHub.IsOwnedByAsync(db, Guid.NewGuid().ToString("N"), "user-a"));
    }

    /// <summary>
    /// A caller must not be able to create unbounded arbitrary groups by passing junk as the group name.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-job-id")]
    [InlineData("0000000000000000000000000000000")] // 31 chars
    [InlineData("000000000000000000000000000000000")] // 33 chars
    [InlineData("0000000000000000000000000000000g")] // 32 chars, not hex
    public async Task Malformed_job_ids_are_rejected(string? jobId)
    {
        await using var db = CreateDb();

        Assert.False(await JobsHub.IsOwnedByAsync(db, jobId, "user-a"));
    }

    private static async Task<string> SeedJobAsync(AppDbContext db, string ownerUserId)
    {
        db.Users.Add(new UserEntity { Id = ownerUserId, OsuUserId = 1, Username = ownerUserId });

        var job = new JobEntity { OwnerUserId = ownerUserId, Status = JobStatus.Queued };
        db.Jobs.Add(job);

        await db.SaveChangesAsync();
        return job.Id;
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
