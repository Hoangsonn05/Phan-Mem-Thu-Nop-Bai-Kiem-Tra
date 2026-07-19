namespace ExamTransfer.Desktop.ViewModels;

public sealed class WelcomeViewModel
{
    public string Title => "ExamTransfer";
    public string Description => "Thu, gửi và quản lý bài kiểm tra an toàn trong mạng LAN.";

    public IReadOnlyList<WelcomeBenefit> Benefits { get; } =
        new WelcomeBenefit[]
        {
            new("\uE774", "Hoạt động local-first", "Phòng thi tiếp tục vận hành khi Internet gián đoạn.", "primary"),
            new("\uE898", "Truyền file tin cậy", "Resume, kiểm tra SHA-256 và biên nhận từ máy chủ.", "accent"),
            new("\uE72E", "Kiểm soát rõ ràng", "Theo dõi realtime, audit và quyền theo vai trò.", "success")
        };
}

public sealed record WelcomeBenefit(string Glyph, string Title, string Description, string Tone);
