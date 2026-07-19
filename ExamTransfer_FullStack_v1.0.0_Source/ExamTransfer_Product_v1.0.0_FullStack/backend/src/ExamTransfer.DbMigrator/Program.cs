using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
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
Console.WriteLine($"Database ready: {paths.DatabasePath}; schema={DbInitializer.SchemaVersion}");
