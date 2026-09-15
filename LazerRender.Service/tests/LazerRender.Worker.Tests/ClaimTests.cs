using LazerRender.Api.Data;
using LazerRender.Api.Services;
using LazerRender.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LazerRender.Worker.Tests;

public sealed class ClaimTests
{
    [Fact]
    public async Task ClaimNextAsync_claims_oldest_queued_job_and_increments_attempts()
    {
        await using var db = CreateDb();

        db.Users.Add(new UserEntity { Id = "u1", OsuUserId = 1, Username = "testuser" });

        var older = new JobEntity
        {
            OwnerUserId = "u1",
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var newer = new JobEntity
        {
            OwnerUserId = "u1",
            Status = JobStatus.Queued,
        };

        db.Jobs.AddRange(older, newer);
        await db.SaveChangesAsync();

        var claimedId = await RenderWorker.ClaimNextAsync(db, CancellationToken.None);

        Assert.NotNull(claimedId);
        Assert.Equal(older.Id, claimedId);

        var claimed = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == claimedId);
        Assert.Equal(JobStatus.Claimed, claimed.Status);
        Assert.Equal(1, claimed.Attempts);
        Assert.NotNull(claimed.ClaimedAt);

        // The second claim must pick up the other queued job, never the same one.
        var secondId = await RenderWorker.ClaimNextAsync(db, CancellationToken.None);
        Assert.NotNull(secondId);
        Assert.NotEqual(claimedId, secondId);
    }

    [Fact]
    public async Task ClaimNextAsync_returns_null_when_queue_is_empty()
    {
        await using var db = CreateDb();

        var result = await RenderWorker.ClaimNextAsync(db, CancellationToken.None);

        Assert.Null(result);
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
