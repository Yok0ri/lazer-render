using LazerRender.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LazerRender.Worker.Tests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public void Initialize_adds_missing_columns_to_existing_schema()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();

        // Create the full schema, then simulate an older database by dropping the columns
        // that were added after the initial schema.
        DatabaseInitializer.Initialize(provider);

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // SQLite refuses to drop a column that is indexed, and DisplayNumber now carries a unique
            // index, so drop that first to simulate the older schema.
            db.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS \"IX_jobs_DisplayNumber\";");
            db.Database.ExecuteSqlRaw("ALTER TABLE \"jobs\" DROP COLUMN \"DisplayNumber\";");
            db.Database.ExecuteSqlRaw("ALTER TABLE \"users\" DROP COLUMN \"IsAllowed\";");
        }

        // Re-running the initializer must patch the missing columns back in.
        DatabaseInitializer.Initialize(provider);

        using var verifyScope = provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(ColumnExists(verifyDb, "jobs", "DisplayNumber"));
        Assert.True(ColumnExists(verifyDb, "users", "IsAllowed"));
        Assert.True(IndexExists(verifyDb, "IX_jobs_DisplayNumber"));
    }

    /// <summary>
    /// M-10 (audit): two concurrent creations could take the same display number; this is the
    /// database-level half of the fix (the creation gate is the other).
    /// </summary>
    [Fact]
    public void Initialize_creates_a_unique_index_on_display_number()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();

        DatabaseInitializer.Initialize(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(IndexExists(db, "IX_jobs_DisplayNumber"));
    }

    private static bool IndexExists(AppDbContext db, string name)
    {
        db.Database.OpenConnection();

        try
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = name;
            command.Parameters.Add(parameter);

            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    private static bool ColumnExists(AppDbContext db, string table, string column)
    {
        db.Database.OpenConnection();
        try
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\");";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }
}
