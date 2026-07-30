using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Services;

internal static class BulkArchiveValidation
{
    public const int MaxItems = 200;

    public static IReadOnlyList<Guid> Validate(BulkArchiveRequest request)
    {
        if (request.Ids is null || request.Ids.Count == 0)
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Phải chọn ít nhất một mục để lưu trữ.");

        if (request.Ids.Count > MaxItems)
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                $"Chỉ được lưu trữ tối đa {MaxItems} mục trong một lần.");

        if (request.Ids.Any(id => id == Guid.Empty))
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Danh sách lưu trữ chứa ID không hợp lệ.");

        var ids = request.Ids.Distinct().ToList();
        if (ids.Count != request.Ids.Count)
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Danh sách lưu trữ không được chứa ID trùng lặp.");

        return ids;
    }
}
