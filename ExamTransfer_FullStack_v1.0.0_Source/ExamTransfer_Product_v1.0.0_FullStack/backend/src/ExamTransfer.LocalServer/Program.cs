using System.Text.Json.Serialization;
using ExamTransfer.Application;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Backup;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.LocalServer.Auth;
using ExamTransfer.LocalServer.Discovery;
using ExamTransfer.LocalServer.Hubs;
using ExamTransfer.LocalServer.Middleware;
using ExamTransfer.LocalServer.Realtime;
using ExamTransfer.LocalServer.Workers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddExamTransferRuntimeSettings();
builder.Configuration.AddEnvironmentVariables();
if (args.Length > 0)
    builder.Configuration.AddCommandLine(args);
builder.Logging.AddJsonConsole();

var port = builder.Configuration.GetValue<int?>("Server:Port") ?? 5048;
var useHttps = builder.Configuration.GetValue<bool?>("Server:UseHttps") ?? false;
var scheme = useHttps ? "https" : "http";
builder.WebHost.UseUrls($"{scheme}://0.0.0.0:{port}");

var rootConfigured = builder.Configuration["Storage:RootPath"] ?? "%ProgramData%/ExamTransfer";
var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
if (string.IsNullOrWhiteSpace(programData)) programData = AppContext.BaseDirectory;
var bootstrapRoot = Path.GetFullPath(rootConfigured.Replace("%ProgramData%", programData, StringComparison.OrdinalIgnoreCase).Replace('/', Path.DirectorySeparatorChar));
Directory.CreateDirectory(bootstrapRoot);
RestoreBootstrap.ApplyPendingRestore(bootstrapRoot);

builder.Services.AddExamTransferInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IRealtimePublisher, SignalRRealtimePublisher>();
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "ExamTransfer LocalServer API", Version = "v1", Description = "Local-first LAN backend for Teacher/Student desktop applications." });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "ExamTransferToken", In = ParameterLocation.Header });
    o.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

builder.Services.AddAuthentication(ExamTransferAuthSchemes.Account)
    .AddScheme<AuthenticationSchemeOptions, AccountAuthHandler>(ExamTransferAuthSchemes.Account, _ => { })
    .AddScheme<AuthenticationSchemeOptions, ExamParticipantAuthHandler>(ExamTransferAuthSchemes.ExamParticipant, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("TeacherOrAdmin", p =>
    {
        p.AuthenticationSchemes.Add(ExamTransferAuthSchemes.Account);
        p.RequireRole("Teacher", "Admin");
    });
    options.AddPolicy("Student", p =>
    {
        p.AuthenticationSchemes.Add(ExamTransferAuthSchemes.Account);
        p.RequireRole("Student");
        p.RequireClaim("password_change_required", "false");
    });
    options.AddPolicy("StudentParticipant", p =>
    {
        p.AuthenticationSchemes.Add(ExamTransferAuthSchemes.ExamParticipant);
        p.RequireRole("Student");
    });
    options.AddPolicy("StudentWithParticipant", p =>
    {
        p.AuthenticationSchemes.Add(ExamTransferAuthSchemes.Account);
        p.AuthenticationSchemes.Add(ExamTransferAuthSchemes.ExamParticipant);
        p.RequireAssertion(context => StudentParticipantScope.IsValid(context.User));
    });
});

builder.Services.AddHostedService<HeartbeatMonitorWorker>();
builder.Services.AddHostedService<TransferCleanupWorker>();
builder.Services.AddHostedService<CloudSyncWorker>();
builder.Services.AddHostedService<CloudAuthRefreshWorker>();
builder.Services.AddHostedService<ExportWorker>();
builder.Services.AddHostedService<UdpDiscoveryService>();

var app = builder.Build();
app.UseMiddleware<TraceIdMiddleware>();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseMiddleware<PasswordChangeGateMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", serverNowUtc = DateTimeOffset.UtcNow, schemaVersion = ExamTransfer.Shared.Contracts.ContractInfo.SchemaVersion }));
app.MapControllers();
app.MapHub<ExamHub>(ExamTransfer.Shared.Contracts.ContractInfo.HubPath);

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var paths = scope.ServiceProvider.GetRequiredService<IStoragePaths>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("StorageDiagnostics");
    logger.LogInformation(
        "ExamTransfer storage initialized. Storage.RootPath={StorageRootPath}; DatabasePath={DatabasePath}",
        paths.RootPath,
        paths.DatabasePath);
    await DbInitializer.InitializeAsync(db, paths);
}

app.Run();

public partial class Program { }
