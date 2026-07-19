using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Backup;
using ExamTransfer.Infrastructure.Cloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExamTransfer.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddExamTransferInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ExamTransferOptions>(configuration);
        services.AddSingleton<IStoragePaths, StoragePaths>();
        services.AddSingleton<IChunkStorage, ChunkStorage>();
        services.AddSingleton<IReceiptSigner, ReceiptSigner>();
        services.AddSingleton<ISessionTokenService, SessionTokenService>();
        services.AddSingleton<IAccountTokenService, AccountTokenService>();
        services.AddSingleton<ILoginChallengeService, LoginChallengeService>();
        services.AddSingleton<IBackupEngine, SqliteBackupEngine>();
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        var commonData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonData))
            commonData = AppContext.BaseDirectory;
        var keyDirectory = Path.Combine(
            commonData,
            "ExamTransfer",
            "keys");
        Directory.CreateDirectory(keyDirectory);
        var dataProtection = services
            .AddDataProtection()
            .SetApplicationName("ExamTransfer")
            .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        if (OperatingSystem.IsWindows())
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

        services.AddSingleton<CloudSessionState>();
        services.AddHttpClient<ICloudAdapter, SupabaseCloudAdapter>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ExamTransfer-Backend/1.2");
        });
        services.AddHttpClient<IExternalIdentityProvider, SupabaseIdentityClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ExamTransfer-Auth/1.2");
        });

        services.AddDbContext<AppDbContext>((sp, builder) =>
        {
            var paths = sp.GetRequiredService<IStoragePaths>(); paths.EnsureCreated();
            builder.UseSqlite($"Data Source={paths.DatabasePath};Cache=Shared");
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IOutboxService, OutboxService>();
        services.AddScoped<IAccountSessionService, AccountSessionService>();
        services.AddScoped<IAccountAuthenticationService, AccountAuthenticationService>();
        services.AddScoped<IClassService, ClassService>();
        services.AddScoped<IExamService, ExamService>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<ISubmissionService, SubmissionService>();
        services.AddScoped<IGradeService, GradeService>();
        services.AddScoped<IControlService, ControlService>();
        services.AddScoped<IExportService, ExportService>();
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<ISystemService, SystemService>();
        return services;
    }
}
