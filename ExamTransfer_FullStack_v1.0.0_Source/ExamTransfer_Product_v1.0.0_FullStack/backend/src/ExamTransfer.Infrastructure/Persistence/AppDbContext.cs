using ExamTransfer.Application;
using ExamTransfer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ExamTransfer.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<User> UsersSet => Set<User>();
    public DbSet<UserLoginSession> UserLoginSessionsSet => Set<UserLoginSession>();
    public DbSet<ClassRoom> ClassesSet => Set<ClassRoom>();
    public DbSet<ClassMember> ClassMembersSet => Set<ClassMember>();
    public DbSet<Exam> ExamsSet => Set<Exam>();
    public DbSet<ExamFile> ExamFilesSet => Set<ExamFile>();
    public DbSet<ExamSession> ExamSessionsSet => Set<ExamSession>();
    public DbSet<SessionParticipant> SessionParticipantsSet => Set<SessionParticipant>();
    public DbSet<ParticipantExtraTime> ParticipantExtraTimesSet => Set<ParticipantExtraTime>();
    public DbSet<Message> MessagesSet => Set<Message>();
    public DbSet<Submission> SubmissionsSet => Set<Submission>();
    public DbSet<SubmissionFile> SubmissionFilesSet => Set<SubmissionFile>();
    public DbSet<Grade> GradesSet => Set<Grade>();
    public DbSet<RubricScore> RubricScoresSet => Set<RubricScore>();
    public DbSet<GradedAttachment> GradedAttachmentsSet => Set<GradedAttachment>();
    public DbSet<ControlPolicy> ControlPoliciesSet => Set<ControlPolicy>();
    public DbSet<DevicePolicyStatus> DevicePolicyStatusesSet => Set<DevicePolicyStatus>();
    public DbSet<Violation> ViolationsSet => Set<Violation>();
    public DbSet<AuditLog> AuditLogsSet => Set<AuditLog>();
    public DbSet<ExportJob> ExportJobsSet => Set<ExportJob>();
    public DbSet<BackupRecord> BackupsSet => Set<BackupRecord>();
    public DbSet<SyncQueueItem> SyncQueueSet => Set<SyncQueueItem>();
    public DbSet<AppSetting> AppSettingsSet => Set<AppSetting>();

    IQueryable<User> IAppDbContext.Users => UsersSet;
    IQueryable<UserLoginSession> IAppDbContext.UserLoginSessions => UserLoginSessionsSet;
    IQueryable<ClassRoom> IAppDbContext.Classes => ClassesSet;
    IQueryable<ClassMember> IAppDbContext.ClassMembers => ClassMembersSet;
    IQueryable<Exam> IAppDbContext.Exams => ExamsSet;
    IQueryable<ExamFile> IAppDbContext.ExamFiles => ExamFilesSet;
    IQueryable<ExamSession> IAppDbContext.ExamSessions => ExamSessionsSet;
    IQueryable<SessionParticipant> IAppDbContext.SessionParticipants => SessionParticipantsSet;
    IQueryable<ParticipantExtraTime> IAppDbContext.ParticipantExtraTimes => ParticipantExtraTimesSet;
    IQueryable<Message> IAppDbContext.Messages => MessagesSet;
    IQueryable<Submission> IAppDbContext.Submissions => SubmissionsSet;
    IQueryable<SubmissionFile> IAppDbContext.SubmissionFiles => SubmissionFilesSet;
    IQueryable<Grade> IAppDbContext.Grades => GradesSet;
    IQueryable<RubricScore> IAppDbContext.RubricScores => RubricScoresSet;
    IQueryable<GradedAttachment> IAppDbContext.GradedAttachments => GradedAttachmentsSet;
    IQueryable<ControlPolicy> IAppDbContext.ControlPolicies => ControlPoliciesSet;
    IQueryable<DevicePolicyStatus> IAppDbContext.DevicePolicyStatuses => DevicePolicyStatusesSet;
    IQueryable<Violation> IAppDbContext.Violations => ViolationsSet;
    IQueryable<AuditLog> IAppDbContext.AuditLogs => AuditLogsSet;
    IQueryable<ExportJob> IAppDbContext.ExportJobs => ExportJobsSet;
    IQueryable<BackupRecord> IAppDbContext.Backups => BackupsSet;
    IQueryable<SyncQueueItem> IAppDbContext.SyncQueue => SyncQueueSet;
    IQueryable<AppSetting> IAppDbContext.AppSettings => AppSettingsSet;

    void IAppDbContext.Add<TEntity>(TEntity entity) => Set<TEntity>().Add(entity);
    void IAppDbContext.AddRange<TEntity>(IEnumerable<TEntity> entities) => Set<TEntity>().AddRange(entities);
    void IAppDbContext.Remove<TEntity>(TEntity entity) => Set<TEntity>().Remove(entity);

    public async Task<IAppTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        new EfTransaction(await Database.BeginTransactionAsync(cancellationToken));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(x => x.Username).IsUnique();
            entity.HasIndex(x => x.Email).IsUnique();
            entity.HasIndex(x => x.StudentCode).IsUnique();
            entity.HasIndex(x => x.SupabaseAuthUserId).IsUnique();
            entity.HasIndex(x => x.OrganizationId);
        });
        modelBuilder.Entity<UserLoginSession>(entity =>
        {
            entity.ToTable("user_login_sessions");
            entity.HasIndex(x => x.SessionTokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.RevokedAtUtc });
            entity.HasIndex(x => new { x.OrganizationId, x.UserId });
            entity.HasOne(x => x.User).WithMany(x => x.LoginSessions).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        base.OnModelCreating(modelBuilder);

        ConfigureEntity<User>(modelBuilder, "users");
        ConfigureEntity<UserLoginSession>(modelBuilder, "user_login_sessions");
        ConfigureEntity<ClassRoom>(modelBuilder, "classes");
        ConfigureEntity<ClassMember>(modelBuilder, "class_members");
        ConfigureEntity<Exam>(modelBuilder, "exams");
        ConfigureEntity<ExamFile>(modelBuilder, "exam_files");
        ConfigureEntity<ExamSession>(modelBuilder, "exam_sessions");
        ConfigureEntity<SessionParticipant>(modelBuilder, "session_participants");
        ConfigureEntity<ParticipantExtraTime>(modelBuilder, "participant_extra_time");
        ConfigureEntity<Message>(modelBuilder, "messages");
        ConfigureEntity<Submission>(modelBuilder, "submissions");
        ConfigureEntity<SubmissionFile>(modelBuilder, "submission_files");
        ConfigureEntity<Grade>(modelBuilder, "grades");
        ConfigureEntity<RubricScore>(modelBuilder, "rubric_scores");
        ConfigureEntity<GradedAttachment>(modelBuilder, "graded_attachments");
        ConfigureEntity<ControlPolicy>(modelBuilder, "control_policies");
        ConfigureEntity<DevicePolicyStatus>(modelBuilder, "device_policy_status");
        ConfigureEntity<Violation>(modelBuilder, "violations");
        ConfigureEntity<AuditLog>(modelBuilder, "audit_logs");
        ConfigureEntity<ExportJob>(modelBuilder, "export_jobs");
        ConfigureEntity<BackupRecord>(modelBuilder, "backups");
        ConfigureEntity<SyncQueueItem>(modelBuilder, "sync_queue");

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("app_settings");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.RowVersion).IsConcurrencyToken();
        });

        modelBuilder.Entity<User>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<User>().HasIndex(x => x.CloudId);
        modelBuilder.Entity<ClassRoom>().HasIndex(x => new { x.Code, x.SchoolYear }).IsUnique();
        modelBuilder.Entity<ClassMember>().HasIndex(x => new { x.ClassId, x.StudentCode }).IsUnique();
        modelBuilder.Entity<Exam>().HasIndex(x => new { x.Status, x.ClassId });
        modelBuilder.Entity<ExamFile>().HasIndex(x => new { x.ExamId, x.Version, x.StoredName }).IsUnique();
        modelBuilder.Entity<ExamSession>().HasIndex(x => x.RoomCode);
        modelBuilder.Entity<SessionParticipant>().HasIndex(x => new { x.SessionId, x.StudentCode }).IsUnique();
        modelBuilder.Entity<SessionParticipant>().HasIndex(x => x.LastSeenUtc);
        modelBuilder.Entity<Submission>().HasIndex(x => new { x.ParticipantId, x.AttemptNumber }).IsUnique();
        modelBuilder.Entity<Submission>().HasIndex(x => new { x.ParticipantId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<SubmissionFile>().HasIndex(x => x.SubmissionId);
        modelBuilder.Entity<Grade>().HasIndex(x => x.SubmissionId).IsUnique();
        modelBuilder.Entity<RubricScore>().HasIndex(x => new { x.GradeId, x.CriterionKey }).IsUnique();
        modelBuilder.Entity<ControlPolicy>().HasIndex(x => new { x.SessionId, x.Version }).IsUnique();
        modelBuilder.Entity<DevicePolicyStatus>().HasIndex(x => new { x.ParticipantId, x.PolicyVersion }).IsUnique();
        modelBuilder.Entity<Violation>().HasIndex(x => new { x.SessionId, x.ParticipantId, x.OccurredAtUtc });
        modelBuilder.Entity<AuditLog>().HasIndex(x => new { x.CreatedAtUtc, x.EntityType, x.SessionId });
        modelBuilder.Entity<ExportJob>().HasIndex(x => new { x.Status, x.CreatedAtUtc });
        modelBuilder.Entity<BackupRecord>().HasIndex(x => x.CreatedAtUtc);
        modelBuilder.Entity<SyncQueueItem>().HasIndex(x => new { x.Status, x.NextRetryAtUtc });

        modelBuilder.Entity<ClassMember>().HasOne(x => x.Class).WithMany(x => x.Members).HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Exam>().HasOne(x => x.Class).WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ExamFile>().HasOne(x => x.Exam).WithMany(x => x.Files).HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ExamSession>().HasOne(x => x.Exam).WithMany().HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SessionParticipant>().HasOne(x => x.Session).WithMany(x => x.Participants).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ParticipantExtraTime>().HasOne(x => x.Participant).WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Message>().HasOne(x => x.Session).WithMany(x => x.Messages).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Submission>().HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Submission>().HasOne(x => x.Participant).WithMany(x => x.Submissions).HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SubmissionFile>().HasOne(x => x.Submission).WithMany(x => x.Files).HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Grade>().HasOne(x => x.Submission).WithOne().HasForeignKey<Grade>(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RubricScore>().HasOne(x => x.Grade).WithMany(x => x.RubricScores).HasForeignKey(x => x.GradeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GradedAttachment>().HasOne(x => x.Grade).WithMany(x => x.Attachments).HasForeignKey(x => x.GradeId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureEntity<TEntity>(ModelBuilder modelBuilder, string tableName) where TEntity : EntityBase
    {
        modelBuilder.Entity<TEntity>(entity =>
        {
            entity.ToTable(tableName);
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RowVersion).IsConcurrencyToken();
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<EntityBase>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAtUtc == default) entry.Entity.CreatedAtUtc = now;
                entry.Entity.UpdatedAtUtc = now;
                entry.Entity.RowVersion = Guid.NewGuid().ToString("N");
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
                entry.Entity.RowVersion = Guid.NewGuid().ToString("N");
            }
        }

        foreach (var entry in ChangeTracker.Entries<AppSetting>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.UpdatedAtUtc = now;
            entry.Entity.RowVersion = Guid.NewGuid().ToString("N");
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    private sealed class EfTransaction(IDbContextTransaction transaction) : IAppTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
