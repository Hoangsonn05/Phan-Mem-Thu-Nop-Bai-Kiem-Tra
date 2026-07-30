using System.Text.Json;
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

if (args.Contains("--seed-onlylan-e2e-fixture", StringComparer.Ordinal))
{
    EnsureTestingFixtureAllowed("OnlyLAN E2E");

    var handoffFile = GetRequiredArgument(args, "--handoff-file");
    var teacher = new User
    {
        Username = $"onlylan-teacher-{Guid.NewGuid():N}",
        DisplayName = "OnlyLAN E2E Teacher",
        Role = UserRole.Teacher,
        IsActive = true,
        MustChangePassword = false
    };
    var student = new User
    {
        Username = $"onlylan-student-{Guid.NewGuid():N}",
        DisplayName = "OnlyLAN E2E Student",
        StudentCode = "LANE2E001",
        DateOfBirth = new DateOnly(2010, 1, 1),
        Role = UserRole.Student,
        IsActive = true,
        MustChangePassword = false
    };
    var exam = new Exam
    {
        Title = "OnlyLAN published E2E exam",
        Subject = "OnlyLAN",
        DurationMinutes = 30,
        Status = ExamStatus.Published,
        DeliveryType = ExamDeliveryType.MultipleChoice,
        SupervisionMode = SupervisionMode.Standard,
        QuizResultPolicy = QuizResultPolicy.ShowAfterSubmission,
        Version = 1,
        CreatedBy = teacher.Id
    };
    var question1 = CreateQuizQuestion(exam, 1, "2 + 2 = ?", "3", "4");
    var question2 = CreateQuizQuestion(exam, 2, "5 + 5 = ?", "9", "10");
    var session = new ExamSession
    {
        Exam = exam,
        ExamId = exam.Id,
        ClassId = null,
        AdmissionMode = SessionAdmissionMode.OpenRequest,
        RoomCode = "LANE2E1",
        Status = SessionStatus.Waiting,
        AcceptingParticipants = true,
        AutoApprove = false,
        AccessMode = SessionAccessMode.LanOnly,
        PlannedStartUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        DeliveryTypeSnapshot = ExamDeliveryType.MultipleChoice,
        SupervisionModeSnapshot = SupervisionMode.Standard,
        QuizResultPolicySnapshot = QuizResultPolicy.ShowAfterSubmission,
        ExamVersionSnapshot = exam.Version,
        SettingsJson = "{}",
        Capacity = 40,
        HostDeviceId = "onlylan-e2e-host"
    };

    db.AddRange(teacher, student, exam, question1, question2, session);
    await db.SaveChangesAsync();

    var accountSessions = scope.ServiceProvider.GetRequiredService<ExamTransfer.Application.IAccountSessionService>();
    var accountTokens = scope.ServiceProvider.GetRequiredService<ExamTransfer.Application.IAccountTokenService>();
    async Task<string> IssueAccountTokenAsync(User user, string deviceId)
    {
        var loginSession = await accountSessions.ClaimAsync(
            user,
            deviceId,
            Environment.MachineName,
            "127.0.0.1",
            null,
            default);
        var issued = accountTokens.IssueAccountToken(
            user.Id,
            loginSession.Id,
            user.Role,
            user.OrganizationId,
            loginSession.DeviceId,
            TimeSpan.FromMinutes(60));
        await accountSessions.StoreTokenHashAsync(
            loginSession.Id,
            accountTokens.HashToken(issued.Token),
            default);
        return issued.Token;
    }

    var teacherToken = await IssueAccountTokenAsync(teacher, "onlylan-e2e-teacher-device");
    var studentToken = await IssueAccountTokenAsync(student, "onlylan-e2e-student-device");
    var handoff = new
    {
        teacherAccountToken = teacherToken,
        studentAccountToken = studentToken,
        sessionId = session.Id,
        examId = exam.Id,
        roomCode = session.RoomCode,
        studentCode = student.StudentCode,
        studentDisplayName = student.DisplayName,
        studentDeviceId = "onlylan-e2e-student-device",
        correctAnswers = new Dictionary<Guid, Guid>
        {
            [question1.Id] = question1.Choices.Single(x => x.IsCorrect).Id,
            [question2.Id] = question2.Choices.Single(x => x.IsCorrect).Id
        }
    };
    var handoffPath = Path.GetFullPath(handoffFile);
    Directory.CreateDirectory(Path.GetDirectoryName(handoffPath)!);
    await File.WriteAllTextAsync(
        handoffPath,
        JsonSerializer.Serialize(handoff, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    Console.WriteLine($"OnlyLAN E2E fixture ready: session={session.Id}; room={session.RoomCode}");
}
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
    var student = new User
    {
        Username = "smoke-student",
        DisplayName = "Published smoke student",
        StudentCode = "SMOKE001",
        DateOfBirth = new DateOnly(2010, 1, 1),
        Role = UserRole.Student,
        IsActive = true,
        MustChangePassword = false
    };
    var session = new ExamSession
    {
        Exam = exam,
        ClassId = null,
        AdmissionMode = SessionAdmissionMode.OpenRequest,
        RoomCode = "DOCKERDISC",
        Status = SessionStatus.Waiting,
        AcceptingParticipants = true,
        AccessMode = SessionAccessMode.LanOnly,
        PlannedStartUtc = DateTimeOffset.UtcNow.AddMinutes(5)
    };
    db.AddRange(teacher, classroom, exam, student, session);
    await db.SaveChangesAsync();
    var tokenFileIndex = Array.FindIndex(
        args,
        x => x.Equals("--account-token-file", StringComparison.Ordinal));
    if (tokenFileIndex >= 0)
    {
        if (tokenFileIndex + 1 >= args.Length)
            throw new ArgumentException("--account-token-file requires a path.");
        var tokenFile = Path.GetFullPath(args[tokenFileIndex + 1]);
        var accountSessions = scope.ServiceProvider.GetRequiredService<ExamTransfer.Application.IAccountSessionService>();
        var accountTokens = scope.ServiceProvider.GetRequiredService<ExamTransfer.Application.IAccountTokenService>();
        var loginSession = await accountSessions.ClaimAsync(
            student,
            "published-smoke-device",
            Environment.MachineName,
            "127.0.0.1",
            null,
            default);
        var issued = accountTokens.IssueAccountToken(
            student.Id,
            loginSession.Id,
            UserRole.Student,
            student.OrganizationId,
            loginSession.DeviceId,
            TimeSpan.FromMinutes(30));
        await accountSessions.StoreTokenHashAsync(
            loginSession.Id,
            accountTokens.HashToken(issued.Token),
            default);
        Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
        await File.WriteAllTextAsync(tokenFile, issued.Token);
    }
    Console.WriteLine($"LAN discovery fixture ready: session={session.Id}; room={session.RoomCode}");
}
Console.WriteLine($"Database ready: {paths.DatabasePath}; schema={DbInitializer.SchemaVersion}");

static void EnsureTestingFixtureAllowed(string fixtureName)
{
    if (!string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Testing", StringComparison.Ordinal)
        || !string.Equals(Environment.GetEnvironmentVariable("EXAMTRANSFER_ALLOW_TEST_FIXTURE"), "1", StringComparison.Ordinal))
        throw new InvalidOperationException($"{fixtureName} fixture seeding is allowed only in the explicit Testing environment.");
}

static string GetRequiredArgument(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, value => value.Equals(name, StringComparison.Ordinal));
    if (index < 0 || index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
        throw new ArgumentException($"{name} requires a path.");
    return arguments[index + 1];
}

static QuizQuestion CreateQuizQuestion(
    Exam exam,
    int order,
    string text,
    string incorrectChoice,
    string correctChoice)
{
    var question = new QuizQuestion
    {
        Exam = exam,
        ExamId = exam.Id,
        Version = exam.Version,
        Order = order,
        Text = text,
        Points = 5.00m,
        Multiple = false
    };
    question.Choices.Add(new QuizChoice
    {
        Question = question,
        QuestionId = question.Id,
        Order = 1,
        Text = incorrectChoice,
        IsCorrect = false
    });
    question.Choices.Add(new QuizChoice
    {
        Question = question,
        QuestionId = question.Id,
        Order = 2,
        Text = correctChoice,
        IsCorrect = true
    });
    return question;
}
