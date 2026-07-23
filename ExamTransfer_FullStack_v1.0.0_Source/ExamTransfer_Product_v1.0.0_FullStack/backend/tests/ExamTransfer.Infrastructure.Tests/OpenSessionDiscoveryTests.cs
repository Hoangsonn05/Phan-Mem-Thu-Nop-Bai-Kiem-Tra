using System.Net;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.LocalServer.Controllers;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class OpenSessionDiscoveryTests
{
    [Fact]
    public async Task OpenSessions_ReturnsOnlyOpenWaitingLanRooms()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var classroom = new ClassRoom { Name = "10A", Code = "10A", SchoolYear = "2026-2027" };
        var teacher = new User { Username = "teacher", DisplayName = "Giáo viên", Role = UserRole.Teacher };
        var exam = new Exam { Class = classroom, Title = "Kiểm tra", Subject = "Tin", DurationMinutes = 45, Status = ExamStatus.Published, CreatedBy = teacher.Id };
        db.AddRange(classroom, teacher, exam);
        db.ExamSessionsSet.AddRange(
            new ExamSession { Exam = exam, ClassId = classroom.Id, RoomCode = "OPEN1", Status = SessionStatus.Waiting, AcceptingParticipants = true, AccessMode = SessionAccessMode.LanOnly },
            new ExamSession { Exam = exam, ClassId = classroom.Id, RoomCode = "DRAFT1", Status = SessionStatus.Draft, AcceptingParticipants = true, AccessMode = SessionAccessMode.LanOnly },
            new ExamSession { Exam = exam, ClassId = classroom.Id, RoomCode = "CLOSED1", Status = SessionStatus.Waiting, AcceptingParticipants = false, AccessMode = SessionAccessMode.LanOnly },
            new ExamSession { Exam = exam, ClassId = classroom.Id, RoomCode = "PUBLIC1", Status = SessionStatus.Waiting, AcceptingParticipants = true, AccessMode = SessionAccessMode.PublicCloud });
        await db.SaveChangesAsync();

        var options = new ExamTransferOptions();
        options.Server.PreferredIp = "192.168.10.2";
        var controller = new DiscoveryController(db, new AllowLanPolicy(), Options.Create(options))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.10.3");
        controller.HttpContext.TraceIdentifier = "discovery-test";

        var action = await controller.OpenSessions(default);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<ApiResponse<IReadOnlyList<OpenSessionDiscoveryDto>>>(ok.Value);
        var room = Assert.Single(response.Data!);
        Assert.Equal("OPEN1", room.RoomCode);
        Assert.Equal(SessionAccessMode.LanOnly, room.AccessMode);
    }

    [Fact]
    public async Task OpenSessions_RejectsClientOutsideAllowedLan()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var controller = new DiscoveryController(db, new DenyLanPolicy(), Options.Create(new ExamTransferOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");

        var error = await Assert.ThrowsAsync<ApiException>(() => controller.OpenSessions(default));
        Assert.Equal(ErrorCodes.LanAccessDenied, error.Code);
        Assert.Equal(403, error.StatusCode);
    }

    private sealed class AllowLanPolicy : ILanAccessPolicy { public bool IsAllowed(string? remoteAddress) => true; }
    private sealed class DenyLanPolicy : ILanAccessPolicy { public bool IsAllowed(string? remoteAddress) => false; }
}
