using Microsoft.EntityFrameworkCore;

namespace SyncCore;

public class SyncBusContext : DbContext
{
    public SyncBusContext(DbContextOptions<SyncBusContext> options) : base(options)
    {
    }

    public DbSet<SyncCommand> SyncCommands { get; set; }
    public DbSet<SyncStatus> SyncStatus { get; set; }
    public DbSet<SyncHistoryEntry> SyncHistory { get; set; }
    public DbSet<ReportTask> ReportTasks { get; set; }
    public DbSet<ReportTaskEvent> ReportTaskEvents { get; set; }
    public DbSet<ReportTaskFile> ReportTaskFiles { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SyncCommand>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CommandType).HasMaxLength(20);
            entity.Property(e => e.Payload).HasColumnType("text");
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.Result).HasColumnType("text");
        });

        modelBuilder.Entity<SyncStatus>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.IsRunning).HasDefaultValue(false);
        });

        modelBuilder.Entity<SyncHistoryEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<ReportTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ReportType).HasMaxLength(50);
            entity.Property(e => e.Payload).HasColumnType("text");
            entity.Property(e => e.ProviderId).HasMaxLength(50);
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.FilePath).HasMaxLength(500);
            entity.Property(e => e.OwnerInstanceId).HasMaxLength(36);
            entity.Property(e => e.Result).HasColumnType("text");
        });

        modelBuilder.Entity<ReportTaskEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.Message).HasColumnType("text");
            entity.HasIndex(e => e.TaskId);
        });

        modelBuilder.Entity<ReportTaskFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContentType).HasMaxLength(100);
            entity.HasIndex(e => e.TaskId).IsUnique();
            entity.HasOne(e => e.Task)
                .WithMany()
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}