using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

internal static class ApiGuard
{
    public static T Require<T>(ApiResponse<T>? response)
    {
        if (response?.Success == true && response.Data is not null)
        {
            return response.Data;
        }

        throw new InvalidOperationException(response?.Error?.Message ?? "Máy chủ không trả về dữ liệu hợp lệ.");
    }
}
