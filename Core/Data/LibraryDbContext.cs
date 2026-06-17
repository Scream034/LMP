using LMP.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LMP.Core.Data;

public sealed class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : DbContext(options)
{
    public DbSet<TrackEntity> Tracks => Set<TrackEntity>();
    public DbSet<PlaylistEntity> Playlists => Set<PlaylistEntity>();
    public DbSet<PlaylistTrackEntity> PlaylistTracks => Set<PlaylistTrackEntity>();
    public DbSet<RecentlyPlayedEntity> RecentlyPlayed => Set<RecentlyPlayedEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<LikedTrackEntity> LikedTracks => Set<LikedTrackEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // === Track ===
        modelBuilder.Entity<TrackEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.Title).HasMaxLength(500);
            entity.Property(e => e.Author).HasMaxLength(256);
            entity.Property(e => e.Url).HasMaxLength(512);
            entity.Property(e => e.ThumbnailUrl).HasMaxLength(512);

            entity.HasIndex(e => e.IsDownloaded);
            entity.HasIndex(e => e.UpdatedAt);
            entity.HasIndex(e => new { e.Title, e.Author });
        });

        // === Liked Tracks ===
        modelBuilder.Entity<LikedTrackEntity>(entity =>
        {
            entity.HasKey(e => new { e.OwnerId, e.TrackId });
            entity.Property(e => e.OwnerId).HasMaxLength(128);
            entity.Property(e => e.TrackId).HasMaxLength(64);

            entity.HasOne(e => e.Track)
                .WithMany()
                .HasForeignKey(e => e.TrackId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.OwnerId);
        });

        // === Playlist ===
        modelBuilder.Entity<PlaylistEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.Name).HasMaxLength(256);
            entity.Property(e => e.CustomColor).HasMaxLength(16);
            entity.Property(e => e.ComputedColor).HasMaxLength(16);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.OwnerId).HasMaxLength(128).HasDefaultValue("");

            entity.HasIndex(e => e.OwnerId);
        });

        // === PlaylistTrack (Junction Table) ===
        modelBuilder.Entity<PlaylistTrackEntity>(entity =>
        {
            entity.HasKey(e => new { e.PlaylistId, e.TrackId });

            entity.HasOne(e => e.Playlist)
                .WithMany(p => p.PlaylistTracks)
                .HasForeignKey(e => e.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Track)
                .WithMany(t => t.PlaylistTracks)
                .HasForeignKey(e => e.TrackId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.PlaylistId, e.Position });

            entity.Property(e => e.SetVideoId).HasMaxLength(128);
        });

        // === RecentlyPlayed ===
        modelBuilder.Entity<RecentlyPlayedEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.OwnerId).HasMaxLength(128).HasDefaultValue("");

            entity.HasIndex(e => e.TrackId);
            entity.HasIndex(e => e.PlayedAt);
            entity.HasIndex(e => e.OwnerId);
        });

        // === Settings ===
        modelBuilder.Entity<SettingEntity>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(128);
        });

        // === Notifications ===
        modelBuilder.Entity<NotificationEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(36);
            entity.Property(e => e.TitleKey).HasMaxLength(128);
            entity.Property(e => e.TitleRaw).HasMaxLength(500);
            entity.Property(e => e.MessageKey).HasMaxLength(128);
            entity.Property(e => e.MessageRaw).HasMaxLength(2000);
            entity.Property(e => e.RecommendationKey).HasMaxLength(128);
            entity.Property(e => e.TrackId).HasMaxLength(64);
            entity.Property(e => e.TrackTitle).HasMaxLength(500);

            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.IsRead);
        });
    }
}