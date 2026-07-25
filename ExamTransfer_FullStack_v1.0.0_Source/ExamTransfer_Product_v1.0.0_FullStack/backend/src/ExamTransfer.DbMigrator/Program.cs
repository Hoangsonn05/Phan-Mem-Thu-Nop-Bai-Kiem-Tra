using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true).AddEnvironmentVariables("EXAMTRANSFER_");
builder.Services.AddExamTransferInfrastructure(builder.Configuration);
using var host = builder.Build();
using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
var paths = scope.ServiceProvider.GetRequiredService<ExamTransfer.Application.IStoragePaths>();
await DbInitializer.InitializeAsync(db, paths);
if (args.Contains("--seed-lan-discovery-fixture", StringComparer.Ordinal))
{
    if (!string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Testing", StringComparison.Ordinal)
        || !string.Equals(Environment.GetEnvironmentVariable("EXAMTRANSFER_ALLOW_TEST_FIXTURE"), "1", StringComparison.Ordinal))
        throw new InvalidOperationException("LAN discovery fixture seeding is allowed only in the explicit Testing environment.");

    var teacher = new User
    {
        Username = $"discovery-{Guid.NewGuid():N}",
        DisplayName = "Docker discovery test teacher",
        Role = UserRole.Teacher,
        IsActive = true
    };
    var classroom = new ClassRoom
    {
        Name = "Docker discovery fixture",
        Code = $"DISC-{Guid.NewGuid():N}"[..20],
        SchoolYear = "2026-2027",
        Status = ClassStatus.Active
    };
    var exam = new Exam
    {
        Class = classroom,
        Title = "Docker discovery fixture exam",
        Subject = "Infrastructure",
        DurationMinutes = 30,
        Status = ExamStatus.Published,
        CreatedBy = teacher.Id
    };
    var session = new ExamSession
    {
        Exam = exam,
        ClassId = classroom.Id,
        RoomCode = "DOCKERDISC",
        Status = SessionStatus.Waiting,
        AcceptingParticipants = true,
        AccessMode = SessionAccessMode.LanOnly,
        PlannedStartUtc = DateTimeOffset.UtcNow.AddMinutes(5)
    };
    db.AddRange(teacher, classroom, exam, session);
    await db.SaveChangesAsync();
    Console.WriteLine($"LAN discovery fixture ready: session={session.Id}; room={session.RoomCode}");
}
Console.WriteLine($"Database ready: {paths.DatabasePath}; schema={DbInitializer.SchemaVersion}");
