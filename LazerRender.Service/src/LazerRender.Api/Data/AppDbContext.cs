using LazerRender.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LazerRender.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<OAuthTokenEntity> OAuthTokens => Set<OAuthTokenEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<SkinEntity> Skins => Set<SkinEntity>();
    public DbSet<BeatmapCacheEntity> BeatmapCache => Set<BeatmapCacheEntity>();
    public DbSet<PresetEntity> Presets => Set<PresetEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToUnixTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OsuUserId).IsUnique();
            entity.Property(e => e.Username).HasMaxLength(64).IsRequired();
            entity.Property(e => e.AvatarUrl).HasMaxLength(512);
            entity.Property(e => e.CountryCode).HasMaxLength(2);
            entity.Property(e => e.Role).HasMaxLength(16).IsRequired();
        });

        modelBuilder.Entity<OAuthTokenEntity>(entity =>
        {
            entity.ToTable("oauth_tokens");
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.RefreshTokenEncrypted).IsRequired();
            entity.Property(e => e.Scopes).HasMaxLength(256);
            entity.HasOne(e => e.User)
                .WithOne(u => u.OAuthToken)
                .HasForeignKey<OAuthTokenEntity>(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobEntity>(entity =>
        {
            entity.ToTable("jobs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OwnerUserId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.Encoder).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.ReplayPath).HasMaxLength(512);
            entity.Property(e => e.ReplayMd5).HasMaxLength(32);
            entity.Property(e => e.PlayerUsername).HasMaxLength(64);
            entity.Property(e => e.SkinName).HasMaxLength(128);
            entity.Property(e => e.Phase).HasMaxLength(32);
            entity.Property(e => e.ErrorMessage).HasMaxLength(1024);
            entity.Property(e => e.OutputPath).HasMaxLength(512);
            entity.HasOne(e => e.Owner)
                .WithMany(u => u.Jobs)
                .HasForeignKey(e => e.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SkinEntity>(entity =>
        {
            entity.ToTable("skins");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name);
            entity.Property(e => e.Name).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ArchiveHash).HasMaxLength(64);
            entity.Property(e => e.StoragePath).HasMaxLength(512);
        });

        modelBuilder.Entity<BeatmapCacheEntity>(entity =>
        {
            entity.ToTable("beatmap_cache");
            entity.HasKey(e => e.Md5);
            entity.Property(e => e.Md5).HasMaxLength(32);
            entity.Property(e => e.Title).HasMaxLength(256);
            entity.Property(e => e.Artist).HasMaxLength(256);
            entity.Property(e => e.Creator).HasMaxLength(128);
            entity.Property(e => e.Version).HasMaxLength(128);
        });

        modelBuilder.Entity<PresetEntity>(entity =>
        {
            entity.ToTable("presets");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OwnerUserId, e.Name }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ConfigJson).IsRequired();
            entity.HasOne(e => e.Owner)
                .WithMany()
                .HasForeignKey(e => e.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
