using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.LocalServer.Workers;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests.Workers;

public sealed class PublicCloudPullWorkerTests
{
    private sealed class PullMockAdapter : ICloudAdapter
    {
        public bool Enabled => true;
        public bool Configured => true;
        public bool Authenticated => true;
        public bool CanSynchronize => true;
        public CloudLoginResult? CurrentSession => new CloudLoginResult("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "user", "teacher@example.com", "org-id", "Teacher");

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public List<CloudPullRecord> ReturnRecords { get; set; } = new();

        public Task<CloudPullPage> PullAsync(
            string entityName,
            CloudPullCursorValue cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            var matched = ReturnRecords.Where(x => x.EntityName == entityName && x.CloudVersion > cursor.CloudVersion).OrderBy(x => x.CloudVersion).Take(limit).ToList();
            return Task.FromResult(new CloudPullPage(matched, false)); 
        }

        public Task<CloudPreflightResult> PreflightAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<CloudPushResult> PushAsync(SyncQueueItem item, Func<CancellationToken, Task>? checkpoint, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<CloudLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<CloudLoginResult?> RefreshSessionAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task LogoutAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task DownloadObjectAsync(string cloudObjectPath, string destinationPath, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private static async Task<PublicCloudPullWorker> CreateWorkerAsync(string dbPath, ICloudAdapter adapter)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder => builder.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton(adapter);
        services.AddSingleton<IOptions<ExamTransferOptions>>(Options.Create(new ExamTransferOptions { Cloud = new CloudOptions { OrganizationId = "org-id", AccessMode = "PublicCloud" } }));
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new PublicCloudPullWorker(scopeFactory, NullLogger<PublicCloudPullWorker>.Instance);
    }

    [Fact]
    public async Task OrphanClassMember_DoesNotRollbackPage()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var classRoom = new ClassRoom { Id = Guid.NewGuid(), Name = "Valid Class", Status = ClassStatus.Active };
        database.Context.ClassesSet.Add(classRoom);
        await database.Context.SaveChangesAsync();

        var adapter = new PullMockAdapter();
        adapter.ReturnRecords.Add(new CloudPullRecord(
            "class_members", 
            Guid.NewGuid().ToString(), 
            1, 
            DateTimeOffset.UtcNow, 
            JsonSerializer.Serialize(new { class_id = Guid.NewGuid(), student_code = "ORPHAN", display_name = "Orphan" })));
        
        adapter.ReturnRecords.Add(new CloudPullRecord(
            "class_members", 
            Guid.NewGuid().ToString(), 
            2, 
            DateTimeOffset.UtcNow, 
            JsonSerializer.Serialize(new { class_id = classRoom.Id, student_code = "VALID", display_name = "Valid" })));

        var worker = await CreateWorkerAsync(database.Path, adapter);
        await worker.PullOnceAsync(CancellationToken.None);

        var members = await database.Context.ClassMembersSet.ToListAsync();
        Assert.Single(members);
        Assert.Equal("VALID", members[0].StudentCode);

        var cursor = await database.Context.PublicCloudPullCursorsSet.SingleAsync(x => x.EntityName == "class_members");
        Assert.Equal(2, cursor.LastCloudVersion);
    }

    [Fact]
    public async Task OrphanParticipant_DoesNotBlockValidCurrentSessionParticipant()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var validSession = new ExamSession { Id = Guid.NewGuid(), Status = SessionStatus.InProgress, AccessMode = SessionAccessMode.PublicCloud, ExamId = Guid.NewGuid() };
        var exam = new Exam { Id = validSession.ExamId, Title = "E", DurationMinutes = 10, Status = ExamStatus.Published };
        database.Context.ExamsSet.Add(exam);
        database.Context.ExamSessionsSet.Add(validSession);
        await database.Context.SaveChangesAsync();

        var adapter = new PullMockAdapter();
        adapter.ReturnRecords.Add(new CloudPullRecord(
            "session_participants", 
            Guid.NewGuid().ToString(), 
            1, 
            DateTimeOffset.UtcNow, 
            JsonSerializer.Serialize(new { session_id = Guid.NewGuid(), student_code = "ORPHAN", display_name = "Orphan", status = "PendingApproval" })));
        
        var validParticipantId = Guid.NewGuid();
        adapter.ReturnRecords.Add(new CloudPullRecord(
            "session_participants", 
            validParticipantId.ToString(), 
            2, 
            DateTimeOffset.UtcNow, 
            JsonSerializer.Serialize(new { session_id = validSession.Id, student_code = "VALID", display_name = "Valid", status = "PendingApproval" })));

        var worker = await CreateWorkerAsync(database.Path, adapter);
        await worker.PullOnceAsync(CancellationToken.None);

        var participants = await database.Context.SessionParticipantsSet.ToListAsync();
        Assert.Single(participants);
        Assert.Equal("VALID", participants[0].StudentCode);

        var cursor = await database.Context.PublicCloudPullCursorsSet.SingleAsync(x => x.EntityName == "session_participants");
        Assert.Equal(2, cursor.LastCloudVersion);
    }

    [Fact]
    public async Task MissingParent_DoesNotCreatePlaceholder()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var adapter = new PullMockAdapter();
        adapter.ReturnRecords.Add(new CloudPullRecord(
            "class_members", 
            Guid.NewGuid().ToString(), 
            1, 
            DateTimeOffset.UtcNow, 
            JsonSerializer.Serialize(new { class_id = Guid.NewGuid(), student_code = "ORPHAN", display_name = "Orphan" })));
        adapter.ReturnRecords.Add(new CloudPullRecord(
            "session_participants", 
            Guid.NewGuid().ToString(), 
            1, 
            DateTimeOffset.UtcNow, 
            JsonSerializer.Serialize(new { session_id = Guid.NewGuid(), student_code = "ORPHAN", display_name = "Orphan", status = "PendingApproval" })));

        var worker = await CreateWorkerAsync(database.Path, adapter);
        await worker.PullOnceAsync(CancellationToken.None);

        Assert.Empty(await database.Context.ClassesSet.ToListAsync());
        Assert.Empty(await database.Context.ExamSessionsSet.ToListAsync());
    }

    [Fact]
    public async Task UnexpectedDbFailure_RollsBackAndKeepsCursor()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var classRoom = new ClassRoom { Id = Guid.NewGuid(), Name = "Valid Class", Status = ClassStatus.Active };
        database.Context.ClassesSet.Add(classRoom);
        await database.Context.SaveChangesAsync();

        var adapter = new PullMockAdapter();
        // Insert a valid one first
        adapter.ReturnRecords.Add(new CloudPullRecord(
            "class_members", 
            Guid.NewGuid().ToString(), 
            1, 
            DateTimeOffset.UtcNow, 
            JsonSerializer.Serialize(new { class_id = classRoom.Id, student_code = "VALID1", display_name = "Valid" })));
        // Insert an invalid one that will trigger a DbUpdateException (duplicate student_code in same class)
        // Wait, does SQLite enforce this? In EF Core, unique index on ClassId + StudentCode causes exception.
        var dupId = Guid.NewGuid().ToString();
        adapter.ReturnRecords.Add(new CloudPullRecord(
            "class_members", 
            dupId, 
            2, 
            DateTimeOffset.UtcNow, 
            JsonSerializer.Serialize(new { class_id = classRoom.Id, student_code = "VALID1", display_name = "Valid" }))); // Duplicate, should throw

        var worker = await CreateWorkerAsync(database.Path, adapter);
        await worker.PullOnceAsync(CancellationToken.None);

        var cursor = await database.Context.PublicCloudPullCursorsSet.SingleOrDefaultAsync(x => x.EntityName == "class_members");
        Assert.True(cursor == null || cursor.LastCloudVersion == 0); // Should be rolled back or 0
        
        var failure = await database.Context.PublicCloudPullFailuresSet.FirstOrDefaultAsync(x => x.EntityName == "class_members");
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task ExistingValidProjectionRegression()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var validSession = new ExamSession { Id = Guid.NewGuid(), Status = SessionStatus.InProgress, AccessMode = SessionAccessMode.PublicCloud, ExamId = Guid.NewGuid() };
        var exam = new Exam { Id = validSession.ExamId, Title = "E", DurationMinutes = 10, Status = ExamStatus.Published };
        database.Context.ExamsSet.Add(exam);
        database.Context.ExamSessionsSet.Add(validSession);
        await database.Context.SaveChangesAsync();

        var adapter = new PullMockAdapter();
        var participantId = Guid.NewGuid();
        adapter.ReturnRecords.Add(new CloudPullRecord(
            "session_participants", 
            participantId.ToString(), 
            1, 
            DateTimeOffset.UtcNow, 
            JsonSerializer.Serialize(new { session_id = validSession.Id, student_code = "VALID", display_name = "Valid", status = "PendingApproval" })));

        var worker = await CreateWorkerAsync(database.Path, adapter);
        await worker.PullOnceAsync(CancellationToken.None);

        var participant = await database.Context.SessionParticipantsSet.SingleAsync();
        Assert.Equal(participantId, participant.Id);
        Assert.Equal(1, participant.CloudVersion);

        adapter.ReturnRecords.Add(new CloudPullRecord(
            "session_participants", 
            participantId.ToString(), 
            2, 
            DateTimeOffset.UtcNow, 
            JsonSerializer.Serialize(new { session_id = validSession.Id, student_code = "VALID_UPDATED", display_name = "Valid", status = "Approved" })));
        
        await worker.PullOnceAsync(CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        participant = await database.Context.SessionParticipantsSet.SingleAsync();
        Assert.Equal("VALID_UPDATED", participant.StudentCode);
        Assert.Equal(ParticipantStatus.Approved, participant.Status);
        Assert.Equal(2, participant.CloudVersion);
    }

    [Fact]
    public async Task CompletedSubmissionProjection_PersistsParticipantSubmissionAndFile()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            Status = SessionStatus.Collecting,
            AccessMode = SessionAccessMode.PublicCloud,
            ExamId = Guid.NewGuid()
        };
        database.Context.ExamsSet.Add(new Exam
        {
            Id = session.ExamId,
            Title = "PublicCloud submission",
            DurationMinutes = 10,
            Status = ExamStatus.Published
        });
        database.Context.ExamSessionsSet.Add(session);
        await database.Context.SaveChangesAsync();

        var participantId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var adapter = new PullMockAdapter
        {
            ReturnRecords =
            [
                new CloudPullRecord(
                    "session_participants",
                    participantId.ToString(),
                    218,
                    now,
                    JsonSerializer.Serialize(new
                    {
                        session_id = session.Id,
                        student_code = "PC001",
                        display_name = "PublicCloud Student",
                        status = "Approved",
                        submission_status = "Submitted"
                    })),
                new CloudPullRecord(
                    "submissions",
                    submissionId.ToString(),
                    216,
                    now,
                    JsonSerializer.Serialize(new
                    {
                        session_id = session.Id,
                        participant_id = participantId,
                        attempt_number = 1,
                        status = "Submitted",
                        client_submitted_at = now,
                        server_received_at = now,
                        deadline_at = now.AddMinutes(10),
                        is_late = false,
                        is_official = true,
                        receipt_code = "RECEIPT",
                        receipt_signature = "SIGNATURE"
                    })),
                new CloudPullRecord(
                    "submission_files",
                    fileId.ToString(),
                    214,
                    now,
                    JsonSerializer.Serialize(new
                    {
                        submission_id = submissionId,
                        name = "answer.rar",
                        size_bytes = 460,
                        sha256 = new string('a', 64),
                        transfer_status = "Completed",
                        sync_status = "Synced",
                        cloud_object_path = $"public-submissions/{submissionId:N}/{fileId:N}/answer.rar"
                    }))
            ]
        };

        var worker = await CreateWorkerAsync(database.Path, adapter);
        await worker.PullOnceAsync(CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var participant = await database.Context.SessionParticipantsSet.SingleAsync();
        var submission = await database.Context.SubmissionsSet
            .Include(x => x.Files)
            .SingleAsync();

        Assert.Equal(SubmissionStatus.Submitted, participant.SubmissionStatus);
        Assert.Equal("RECEIPT", submission.ReceiptCode);
        Assert.True(submission.IsOfficial);
        var file = Assert.Single(submission.Files);
        Assert.Equal(TransferStatus.Completed, file.TransferStatus);
        Assert.Equal(new string('a', 64), file.Sha256);
    }

    [Fact]
    public async Task OrphanSubmissionRows_DoNotBlockValidCurrentSessionProjection()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            Status = SessionStatus.Collecting,
            AccessMode = SessionAccessMode.PublicCloud,
            ExamId = Guid.NewGuid()
        };
        database.Context.ExamsSet.Add(new Exam
        {
            Id = session.ExamId,
            Title = "PublicCloud submission visibility",
            DurationMinutes = 10,
            Status = ExamStatus.Published
        });
        database.Context.ExamSessionsSet.Add(session);
        await database.Context.SaveChangesAsync();

        var participantId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var orphanSubmissionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var adapter = new PullMockAdapter
        {
            ReturnRecords =
            [
                new CloudPullRecord(
                    "session_participants",
                    participantId.ToString(),
                    233,
                    now,
                    JsonSerializer.Serialize(new
                    {
                        session_id = session.Id,
                        student_code = "PC001",
                        display_name = "PublicCloud Student",
                        status = "Approved",
                        submission_status = "Submitted"
                    })),
                new CloudPullRecord(
                    "submissions",
                    orphanSubmissionId.ToString(),
                    199,
                    now.AddMinutes(-2),
                    JsonSerializer.Serialize(new
                    {
                        session_id = Guid.NewGuid(),
                        participant_id = Guid.NewGuid(),
                        attempt_number = 1,
                        status = "Submitted",
                        client_submitted_at = now.AddMinutes(-2),
                        server_received_at = now.AddMinutes(-2),
                        deadline_at = now,
                        is_late = false,
                        is_official = true
                    })),
                new CloudPullRecord(
                    "submissions",
                    submissionId.ToString(),
                    231,
                    now,
                    JsonSerializer.Serialize(new
                    {
                        session_id = session.Id,
                        participant_id = participantId,
                        attempt_number = 1,
                        status = "Submitted",
                        client_submitted_at = now,
                        server_received_at = now,
                        deadline_at = now.AddMinutes(10),
                        is_late = false,
                        is_official = true,
                        receipt_code = "RECEIPT",
                        receipt_signature = "SIGNATURE"
                    })),
                new CloudPullRecord(
                    "submission_files",
                    Guid.NewGuid().ToString(),
                    203,
                    now.AddMinutes(-2),
                    JsonSerializer.Serialize(new
                    {
                        submission_id = orphanSubmissionId,
                        name = "orphan.rar",
                        size_bytes = 460,
                        sha256 = new string('b', 64),
                        transfer_status = "Completed",
                        sync_status = "Synced"
                    })),
                new CloudPullRecord(
                    "submission_files",
                    fileId.ToString(),
                    229,
                    now,
                    JsonSerializer.Serialize(new
                    {
                        submission_id = submissionId,
                        name = "answer.rar",
                        size_bytes = 460,
                        sha256 = new string('a', 64),
                        transfer_status = "Completed",
                        sync_status = "Synced",
                        cloud_object_path = $"public-submissions/{submissionId:N}/{fileId:N}/answer.rar"
                    }))
            ]
        };

        var worker = await CreateWorkerAsync(database.Path, adapter);
        await worker.PullOnceAsync(CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var submission = await database.Context.SubmissionsSet
            .Include(x => x.Files)
            .SingleAsync();
        Assert.Equal(session.Id, submission.SessionId);
        Assert.Equal(participantId, submission.ParticipantId);
        Assert.Equal(fileId, Assert.Single(submission.Files).Id);

        var service = CreateSubmissionService(database.Context);
        var summary = Assert.Single((await service.ListForSessionAsync(
            session.Id,
            null,
            1,
            100,
            CancellationToken.None)).Items);
        Assert.Equal(submissionId, summary.Id);
        Assert.Equal(session.Id, summary.SessionId);
        Assert.Equal(fileId, Assert.Single(summary.Files).Id);

        Assert.Equal(231, (await database.Context.PublicCloudPullCursorsSet
            .SingleAsync(x => x.EntityName == "submissions")).LastCloudVersion);
        Assert.Equal(229, (await database.Context.PublicCloudPullCursorsSet
            .SingleAsync(x => x.EntityName == "submission_files")).LastCloudVersion);
    }

    [Fact]
    public void OwnershipRegression_EntityOrderDoesNotContainLocalOwned()
    {
        var entityOrderField = typeof(PublicCloudPullWorker).GetField("EntityOrder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var entityOrder = (string[])entityOrderField!.GetValue(null)!;
        
        Assert.DoesNotContain("classes", entityOrder);
        Assert.DoesNotContain("exams", entityOrder);
        Assert.DoesNotContain("exam_sessions", entityOrder);
    }

    private static SubmissionService CreateSubmissionService(AppDbContext db)
        => new(
            db,
            null!,
            new ChunkStorage(),
            null!,
            null!,
            null!,
            null!,
            Options.Create(new ExamTransferOptions()),
            null!);
}
