using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1")]
[Authorize]
public sealed class ControlController(IControlService service) : ApiControllerBase
{
    [HttpGet("sessions/{id:guid}/control-policy")]
    public async Task<ActionResult<ApiResponse<ControlPolicyDto?>>> Policy(Guid id, CancellationToken ct)
    {
        if (IsStudent) EnsureStudentScope(id, RequiredGuidClaim("participant_id"));
        return Data(await service.GetPolicyAsync(id, ct));
    }

    [HttpPut("sessions/{id:guid}/control-policy")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<ControlPolicyDto>>> Save(Guid id, SaveControlPolicyRequest request, CancellationToken ct) => Data(await service.SavePolicyAsync(id, request, ct));
    [HttpPost("sessions/{id:guid}/control-policy/apply")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Apply(Guid id, ApplyControlPolicyRequest request, CancellationToken ct) { await service.ApplyPolicyAsync(id, request, ct); return EmptyData(); }
    [HttpGet("sessions/{id:guid}/devices/control-status")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<DeviceControlStatusDto>>>> Status(Guid id, CancellationToken ct) => Data(await service.GetDeviceStatusAsync(id, ct));
    [HttpGet("sessions/{id:guid}/violations")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<PagedResult<ViolationDto>>>> Violations(Guid id, [FromQuery] ViolationSeverity? severity, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default) => Data(await service.GetViolationsAsync(id, severity, page, pageSize, ct));
    [HttpPost("sessions/{id:guid}/participants/{participantId:guid}/violations")][Authorize(Policy = "Student")]
    public async Task<ActionResult<ApiResponse<ViolationDto>>> Report(Guid id, Guid participantId, ViolationReportRequest request, CancellationToken ct)
    {
        EnsureStudentScope(id, participantId);
        return Data(await service.ReportViolationAsync(id, participantId, request, ct));
    }
    [HttpPost("violations/{id:guid}/acknowledge")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Acknowledge(Guid id, CancellationToken ct) { await service.AcknowledgeAsync(id, null, ct); return EmptyData(); }
    [HttpPost("participants/{participantId:guid}/control-actions")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Action(Guid participantId, ControlActionRequest request, CancellationToken ct) { await service.ControlActionAsync(participantId, request, ct); return EmptyData(); }
}
