using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Workers;

public sealed class ExportWorker(IServiceScopeFactory scopeFactory, ILogger<ExportWorker> logger) : BackgroundService
{
    internal const int CandidateLimit = 256;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var paths = scope.ServiceProvider.GetRequiredService<IStoragePaths>();
                var realtime = scope.ServiceProvider.GetRequiredService<IRealtimePublisher>();
                var outbox = scope.ServiceProvider.GetRequiredService<IOutboxService>();
                var job = await ClaimNextQueuedJobAsync(db, stoppingToken);
                if (job is null) continue;
                try
                {
                    var request = JsonSerializer.Deserialize<CreateExportRequest>(job.OptionsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Export options invalid.");
                    if (!string.Equals(request.Format, "zip", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Định dạng export hiện được hỗ trợ là zip.");
                    var session = await db.ExamSessionsSet.AsNoTracking().Include(x => x.Exam).FirstAsync(x => x.Id == job.SessionId, stoppingToken);
                    var className = session.ClassId.HasValue
                        ? await db.ClassesSet.AsNoTracking().Where(x => x.Id == session.ClassId.Value).Select(x => x.Code).FirstOrDefaultAsync(stoppingToken) ?? "class"
                        : "class";
                    var submissions = await db.SubmissionsSet.AsNoTracking().Include(x => x.Participant).Include(x => x.Files).Where(x => x.SessionId == job.SessionId && x.IsOfficial).OrderBy(x => x.Participant.StudentCode).ToListAsync(stoppingToken);
                    var name = $"{StoragePaths.SanitizeSegment(session.Exam.Title)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip"; var output = Path.Combine(paths.ExportRoot, name); Directory.CreateDirectory(paths.ExportRoot);
                    await using var file = new FileStream(output + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true);
                    using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                    {
                        var manifest = new List<object>();
                        for (var i = 0; i < submissions.Count; i++)
                        {
                            await db.Entry(job).ReloadAsync(stoppingToken);
                            if (job.Status == ExportStatus.Cancelled) break;
                            var s = submissions[i];
                            var folder = "submissions/" + BuildSubmissionFolder(
                                request.NamingPattern,
                                className,
                                s.Participant.StudentCode,
                                s.Participant.DisplayName,
                                s.AttemptNumber);
                            if (request.IncludeFiles)
                            {
                                foreach (var sf in s.Files.Where(x => x.TransferStatus == TransferStatus.Completed))
                                {
                                    var full = Path.Combine(paths.RootPath, sf.RelativePath); if (!File.Exists(full)) continue;
                                    var entry = zip.CreateEntry($"{folder}/{StoragePaths.SanitizeSegment(sf.OriginalName)}", CompressionLevel.Fastest);
                                    await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true); await using var outputStream = entry.Open(); await input.CopyToAsync(outputStream, stoppingToken);
                                }
                            }
                            if (request.IncludeReceipts && !string.IsNullOrWhiteSpace(s.ReceiptCode))
                            {
                                var physicalReceipt = Path.Combine(paths.ReceiptRoot(job.SessionId), s.Id.ToString("N") + ".json");
                                var receiptEntryName = $"receipts/{StoragePaths.SanitizeSegment(s.Participant.StudentCode + "_" + s.Participant.DisplayName)}-attempt-{s.AttemptNumber}.json";
                                var receiptEntry = zip.CreateEntry(receiptEntryName, CompressionLevel.Fastest);
                                await using var receiptOutput = receiptEntry.Open();
                                if (File.Exists(physicalReceipt))
                                {
                                    await using var receiptInput = new FileStream(physicalReceipt, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                                    await receiptInput.CopyToAsync(receiptOutput, stoppingToken);
                                }
                                else
                                {
                                    await JsonSerializer.SerializeAsync(receiptOutput, new
                                    {
                                        submissionId = s.Id,
                                        s.ReceiptCode,
                                        s.ReceiptSignature,
                                        s.ServerReceivedAtUtc,
                                        s.IsLate,
                                        files = s.Files.Select(x => new { x.OriginalName, x.SizeBytes, x.Sha256 })
                                    }, new JsonSerializerOptions { WriteIndented = true }, stoppingToken);
                                }
                            }
                            manifest.Add(new { submissionId = s.Id, s.Participant.StudentCode, s.Participant.DisplayName, s.AttemptNumber, status = s.Status.ToString(), s.IsLate, s.ReceiptCode, files = s.Files.Select(x => new { x.OriginalName, x.SizeBytes, x.Sha256 }) });
                            job.Progress = submissions.Count == 0 ? 80 : 5 + 80d * (i + 1) / submissions.Count; await db.SaveChangesAsync(stoppingToken);
                            await realtime.PublishSessionAsync(job.SessionId, RealtimeEvents.ExportProgressChanged, session.Sequence, new ExportProgressEvent(job.Id, job.Progress, job.Status), stoppingToken);
                        }
                        if (request.IncludeManifest)
                        {
                            var entry = zip.CreateEntry("manifest.json"); await using var stream = entry.Open(); await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions { WriteIndented = true }, stoppingToken);
                        }
                        if (request.IncludeAudit)
                        {
                            var audits = await db.AuditLogsSet.AsNoTracking().Where(x => x.SessionId == job.SessionId).ToListAsync(stoppingToken);
                            audits.Sort((left, right) => left.CreatedAtUtc.CompareTo(right.CreatedAtUtc));
                            var entry = zip.CreateEntry("audit.csv"); await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(true)); await writer.WriteLineAsync("time,actor,action,entityType,entityId,traceId"); foreach (var a in audits) await writer.WriteLineAsync($"{a.CreatedAtUtc:O},{Escape(a.ActorId)},{Escape(a.Action)},{Escape(a.EntityType)},{Escape(a.EntityId)},{Escape(a.TraceId)}");
                        }
                    }
                    await file.FlushAsync(stoppingToken); file.Close();
                    if (job.Status == ExportStatus.Cancelled) { File.Delete(output + ".tmp"); continue; }
                    File.Move(output + ".tmp", output, true);
                    job.OutputPath = Path.GetRelativePath(paths.RootPath, output);
                    job.Status = ExportStatus.Completed;
                    job.Progress = 100;
                    job.CompletedAtUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                    await outbox.EnqueueAsync(
                        "export_jobs",
                        job.Id.ToString(),
                        "upsert",
                        new
                        {
                            id = job.Id,
                            session_id = job.SessionId,
                            status = job.Status.ToString(),
                            progress = job.Progress,
                            output_file_name = Path.GetFileName(output),
                            completed_at = job.CompletedAtUtc,
                            created_at = job.CreatedAtUtc,
                            updated_at = job.UpdatedAtUtc
                        },
                        output,
                        stoppingToken);
                    await realtime.PublishSessionAsync(job.SessionId, RealtimeEvents.ExportProgressChanged, session.Sequence, new ExportProgressEvent(job.Id, 100, ExportStatus.Completed), stoppingToken);
                }
                catch (Exception ex)
                {
                    job.Status = ExportStatus.Failed; job.Error = ex.Message; await db.SaveChangesAsync(stoppingToken); logger.LogError(ex, "Export job {JobId} failed", job.Id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Export worker failed"); }
        }
    }

    internal static async Task<ExportJob?> ClaimNextQueuedJobAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        Guid? oldestJobId = null;
        DateTimeOffset? oldestCreatedAtUtc = null;
        var offset = 0;
        while (true)
        {
            var candidates = await db.ExportJobsSet
                .AsNoTracking()
                .Where(x => x.Status == ExportStatus.Queued)
                .OrderBy(x => x.Id)
                .Skip(offset)
                .Take(CandidateLimit)
                .Select(x => new { x.Id, x.CreatedAtUtc })
                .ToListAsync(cancellationToken);
            foreach (var candidate in candidates)
            {
                if (oldestCreatedAtUtc is null || candidate.CreatedAtUtc < oldestCreatedAtUtc.Value)
                {
                    oldestJobId = candidate.Id;
                    oldestCreatedAtUtc = candidate.CreatedAtUtc;
                }
            }

            if (candidates.Count < CandidateLimit) break;
            offset += candidates.Count;
        }

        if (oldestJobId is null) return null;
        var job = await db.ExportJobsSet.FirstOrDefaultAsync(
            x => x.Id == oldestJobId.Value && x.Status == ExportStatus.Queued,
            cancellationToken);
        if (job is null) return null;

        job.Status = ExportStatus.Running;
        job.Progress = 1;
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private static string BuildSubmissionFolder(
        string? pattern,
        string className,
        string studentCode,
        string studentName,
        int attemptNumber)
    {
        var resolved = string.IsNullOrWhiteSpace(pattern)
            ? "{class}/{studentCode}_{studentName}/attempt-{attempt}"
            : pattern;
        resolved = resolved
            .Replace("{class}", className, StringComparison.OrdinalIgnoreCase)
            .Replace("{studentCode}", studentCode, StringComparison.OrdinalIgnoreCase)
            .Replace("{studentName}", studentName, StringComparison.OrdinalIgnoreCase)
            .Replace("{attempt}", attemptNumber.ToString(), StringComparison.OrdinalIgnoreCase);

        var segments = resolved
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x is not "." and not "..")
            .Select(StoragePaths.SanitizeSegment)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(8)
            .ToArray();

        if (segments.Length == 0)
            segments = [StoragePaths.SanitizeSegment(studentCode + "_" + studentName), $"attempt-{attemptNumber}"];
        return string.Join('/', segments);
    }

    private static string Escape(string? value) { value ??= string.Empty; return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value; }
}
