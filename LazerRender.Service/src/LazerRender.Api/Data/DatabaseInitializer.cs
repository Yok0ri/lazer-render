using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LazerRender.Api.Data;

/// <summary>
/// Creates the schema when the database is fresh, adds columns introduced after the initial
/// schema, and creates any tables that <c>EnsureCreated</c> would not add to an existing
/// database. This is a stopgap until proper EF Core migrations replace it (see DEPLOYMENT.md).
/// </summary>
public static class DatabaseInitializer
{
    private static readonly (string Table, string Column, string Ddl)[] ColumnPatches =
    {
        ("jobs", "DisplayNumber", "INTEGER NOT NULL DEFAULT 0"),
        ("users", "IsAllowed", "INTEGER NOT NULL DEFAULT 0"),
        ("jobs", "MapTitle", "TEXT NULL"),
        ("jobs", "MapArtist", "TEXT NULL"),
        ("jobs", "MapCreator", "TEXT NULL"),
        ("jobs", "MapVersion", "TEXT NULL"),
        ("jobs", "MapStars", "REAL NULL"),
        ("jobs", "SongLength", "REAL NULL"),
        ("jobs", "Mods", "TEXT NULL"),
        ("jobs", "Accuracy", "REAL NULL"),
        ("beatmap_cache", "Title", "TEXT NULL"),
        ("beatmap_cache", "Artist", "TEXT NULL"),
        ("beatmap_cache", "Creator", "TEXT NULL"),
        ("beatmap_cache", "Version", "TEXT NULL"),
        ("beatmap_cache", "Stars", "REAL NULL"),
    };

    public static IReadOnlyList<string> Initialize(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Database.EnsureCreated();

        foreach (var (table, column, ddl) in ColumnPatches)
            EnsureColumn(db, table, column, ddl);

        EnsurePresetsTable(db);

        var warnings = new List<string>();
        EnsureDisplayNumberIndex(db, warnings);

        return warnings;
    }

    /// <summary>
    /// Makes display numbers unique, which is the database-level half of the fix for two concurrent
    /// creations taking the same value. Best effort: an existing database may already hold duplicates,
    /// and refusing to start would be worse than leaving a cosmetic constraint off, so a failure is
    /// reported as a warning instead.
    /// </summary>
    private static void EnsureDisplayNumberIndex(AppDbContext db, List<string> warnings)
    {
        try
        {
            // Identifier is a compile-time constant.
#pragma warning disable EF1002
            db.Database.ExecuteSqlRaw(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_jobs_DisplayNumber\" ON \"jobs\" (\"DisplayNumber\");");
#pragma warning restore EF1002
        }
        catch (DbException)
        {
            warnings.Add(
                "Could not create the unique index on jobs.DisplayNumber because duplicate values already "
                + "exist. Display numbers may collide until the duplicates are resolved.");
        }
    }

    private static void EnsurePresetsTable(AppDbContext db)
    {
#pragma warning disable EF1002 // identifiers are compile-time constants
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"presets\" (" +
            "\"Id\" TEXT NOT NULL CONSTRAINT \"PK_presets\" PRIMARY KEY, " +
            "\"OwnerUserId\" TEXT NOT NULL, " +
            "\"Name\" TEXT NOT NULL, " +
            "\"ConfigJson\" TEXT NOT NULL, " +
            "\"CreatedAt\" INTEGER NOT NULL, " +
            "\"UpdatedAt\" INTEGER NOT NULL, " +
            "CONSTRAINT \"FK_presets_users_OwnerUserId\" FOREIGN KEY (\"OwnerUserId\") REFERENCES \"users\" (\"Id\") ON DELETE CASCADE);");

        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_presets_OwnerUserId_Name\" ON \"presets\" (\"OwnerUserId\", \"Name\");");
#pragma warning restore EF1002
    }

    private static void EnsureColumn(AppDbContext db, string table, string column, string ddl)
    {
        if (ColumnExists(db, table, column))
            return;

        // Identifiers come from the compile-time ColumnPatches table above, never from user input.
#pragma warning disable EF1002
        db.Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {ddl};");
#pragma warning restore EF1002
    }

    private static bool ColumnExists(AppDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        db.Database.OpenConnection();

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\");";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
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
