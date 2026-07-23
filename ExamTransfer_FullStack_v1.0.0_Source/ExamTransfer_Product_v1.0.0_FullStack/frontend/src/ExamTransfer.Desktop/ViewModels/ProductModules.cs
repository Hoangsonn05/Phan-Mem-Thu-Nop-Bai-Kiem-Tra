using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class ClassManagementViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private ClassSummaryDto? selectedClass;
    private StudentDto? selectedStudent;
    private string currentClassRowVersion = "1";
    private string name = string.Empty;
    private string code = string.Empty;
    private string schoolYear = $"{DateTime.Today.Year}-{DateTime.Today.Year + 1}";
    private string description = string.Empty;
    private string studentCode = string.Empty;
    private string studentName = string.Empty;
    private string studentEmail = string.Empty;
    private ClassAccessMode accessMode = ClassAccessMode.Private;

    public ClassManagementViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy && SelectedClass is not null);
        AddStudentCommand = new AsyncRelayCommand(AddStudentAsync, () => !IsBusy && SelectedClass is not null);
        SaveClassCommand = new AsyncRelayCommand(SaveClassAsync, () => !IsBusy && SelectedClass is not null);
        UpdateStudentCommand = new AsyncRelayCommand(UpdateStudentAsync, () => !IsBusy && SelectedClass is not null && SelectedStudent is not null);
        RemoveStudentCommand = new AsyncRelayCommand(RemoveStudentAsync, () => !IsBusy && SelectedClass is not null && SelectedStudent is not null);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy && SelectedClass is not null);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy && SelectedClass is not null);
        ArchiveCommand = new AsyncRelayCommand(ArchiveAsync, () => !IsBusy && SelectedClass is not null);
    }

    public ObservableCollection<ClassSummaryDto> Classes { get; } = new();
    public ObservableCollection<StudentDto> Students { get; } = new();
    public ClassSummaryDto? SelectedClass { get => selectedClass; set { if (Set(ref selectedClass, value)) RaiseCommands(); } }
    public StudentDto? SelectedStudent
    {
        get => selectedStudent;
        set
        {
            if (Set(ref selectedStudent, value))
            {
                if (value is not null)
                {
                    StudentCode = value.StudentCode;
                    StudentName = value.DisplayName;
                    StudentEmail = value.Email ?? string.Empty;
                }
                RaiseCommands();
            }
        }
    }
    public string Name { get => name; set => Set(ref name, value); }
    public string Code { get => code; set => Set(ref code, value); }
    public string SchoolYear { get => schoolYear; set => Set(ref schoolYear, value); }
    public string Description { get => description; set => Set(ref description, value); }
    public string StudentCode { get => studentCode; set => Set(ref studentCode, value); }
    public string StudentName { get => studentName; set => Set(ref studentName, value); }
    public string StudentEmail { get => studentEmail; set => Set(ref studentEmail, value); }
    public IReadOnlyList<ClassAccessMode> AccessModes { get; } = Enum.GetValues<ClassAccessMode>();
    public ClassAccessMode AccessMode { get => accessMode; set => Set(ref accessMode, value); }
    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand AddStudentCommand { get; }
    public ICommand SaveClassCommand { get; }
    public ICommand UpdateStudentCommand { get; }
    public ICommand RemoveStudentCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ArchiveCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải danh sách lớp", "Danh sách lớp đã được cập nhật", async token =>
        {
            await RefreshClassesCoreAsync(SelectedClass?.Id, token);
        });
    }

    private async Task RefreshClassesCoreAsync(Guid? selectedId, CancellationToken ct)
    {
        var data = ApiGuard.Require(await api.GetClassesAsync(ct));
        Classes.ReplaceWith(data.Items);
        SelectedClass = selectedId.HasValue
            ? Classes.FirstOrDefault(x => x.Id == selectedId.Value) ?? Classes.FirstOrDefault()
            : Classes.FirstOrDefault();
        if (SelectedClass is not null)
            await LoadDetailAsync(ct);
        else
            Students.Clear();
    }

    private Task OpenAsync() => RunAsync("Đang mở lớp", "Đã tải chi tiết lớp", LoadDetailAsync);

    private async Task LoadDetailAsync(CancellationToken ct)
    {
        if (SelectedClass is null) return;
        var detail = ApiGuard.Require(await api.GetAsync<ClassDetailDto>($"api/v1/classes/{SelectedClass.Id}", ct));
        Students.ReplaceWith(detail.Students);
        Name = detail.Name;
        Code = detail.Code;
        SchoolYear = detail.SchoolYear;
        Description = detail.Description ?? string.Empty;
        AccessMode = detail.AccessMode;
        currentClassRowVersion = detail.RowVersion;
        SelectedStudent = Students.FirstOrDefault();
    }

    private Task CreateAsync() => RunAsync("Đang tạo lớp", "Lớp học đã được tạo", async ct =>
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Code)) throw new InvalidOperationException("Tên lớp và mã lớp là bắt buộc.");
        var created = ApiGuard.Require(await api.PostAsync<CreateClassRequest, ClassDetailDto>("api/v1/classes", new(Name.Trim(), Code.Trim(), SchoolYear.Trim(), Description.Trim(), AccessMode), ct));
        await RefreshClassesCoreAsync(created.Id, ct);
    });


    private Task SaveClassAsync() => RunAsync("Đang lưu lớp", "Thông tin lớp đã được cập nhật", async ct =>
    {
        if (SelectedClass is null) return;
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Code)) throw new InvalidOperationException("Tên lớp và mã lớp là bắt buộc.");
        var updated = ApiGuard.Require(await api.PutAsync<UpdateClassRequest, ClassDetailDto>($"api/v1/classes/{SelectedClass.Id}", new(Name.Trim(), Code.Trim(), SchoolYear.Trim(), Description.Trim(), currentClassRowVersion, AccessMode), ct));
        await RefreshClassesCoreAsync(updated.Id, ct);
    });

    private Task UpdateStudentAsync() => RunAsync("Đang cập nhật học sinh", "Thông tin học sinh đã được cập nhật", async ct =>
    {
        if (SelectedClass is null || SelectedStudent is null) return;
        var updated = ApiGuard.Require(await api.PutAsync<UpdateStudentRequest, StudentDto>($"api/v1/classes/{SelectedClass.Id}/students/{SelectedStudent.Id}", new(StudentCode.Trim(), StudentName.Trim(), string.IsNullOrWhiteSpace(StudentEmail) ? null : StudentEmail.Trim(), SelectedStudent.MetadataJson), ct));
        var index = Students.IndexOf(SelectedStudent);
        if (index >= 0) Students[index] = updated;
        SelectedStudent = updated;
    });

    private Task RemoveStudentAsync() => RunAsync("Đang xóa học sinh khỏi lớp", "Học sinh đã được xóa khỏi lớp", async ct =>
    {
        if (SelectedClass is null || SelectedStudent is null || !AppServices.Dialogs.Confirm("Xóa học sinh", $"Xóa {SelectedStudent.DisplayName} khỏi lớp?")) return;
        var classId = SelectedClass.Id;
        _ = await api.DeleteAsync<object>($"api/v1/classes/{SelectedClass.Id}/students/{SelectedStudent.Id}", ct);
        await RefreshClassesCoreAsync(classId, ct);
    });

    private Task ExportAsync() => RunAsync("Đang xuất danh sách lớp", "Danh sách lớp đã được xuất", async ct =>
    {
        if (SelectedClass is null) return;
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        await api.DownloadFileAsync($"api/v1/classes/{SelectedClass.Id}/export", Path.Combine(folder, $"{SelectedClass.Code}-students.csv"), null, ct);
    });

    private Task AddStudentAsync() => RunAsync("Đang thêm học sinh", "Đã thêm học sinh vào lớp", async ct =>
    {
        if (SelectedClass is null) return;
        if (string.IsNullOrWhiteSpace(StudentCode) || string.IsNullOrWhiteSpace(StudentName)) throw new InvalidOperationException("Mã và họ tên học sinh là bắt buộc.");
        var student = ApiGuard.Require(await api.PostAsync<CreateStudentRequest, StudentDto>($"api/v1/classes/{SelectedClass.Id}/students", new(StudentCode.Trim(), StudentName.Trim(), string.IsNullOrWhiteSpace(StudentEmail) ? null : StudentEmail.Trim(), null), ct));
        await RefreshClassesCoreAsync(SelectedClass.Id, ct);
        StudentCode = StudentName = StudentEmail = string.Empty;
    });

    private Task ImportAsync() => RunAsync("Đang kiểm tra file import", "Danh sách học sinh đã được import", async ct =>
    {
        if (SelectedClass is null) return;
        var file = AppServices.Files.PickFile("Danh sách CSV|*.csv|Tất cả file|*.*");
        if (file is null) return;
        var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(file, ct));
        var preview = ApiGuard.Require(await api.PostAsync<ImportPreviewRequest, ImportPreviewDto>($"api/v1/classes/{SelectedClass.Id}/imports/preview", new(Path.GetFileName(file), base64, null), ct));
        if (!AppServices.Dialogs.Confirm("Xác nhận import", $"Có {preview.ValidRows} dòng hợp lệ và {preview.InvalidRows} dòng lỗi. Tiếp tục import?")) return;
        _ = ApiGuard.Require(await api.PostAsync<ImportCommitRequest, ImportCommitResultDto>($"api/v1/classes/{SelectedClass.Id}/imports/commit", new(preview.PreviewToken, true), ct));
        await RefreshClassesCoreAsync(SelectedClass.Id, ct);
    });

    private Task ArchiveAsync() => RunAsync("Đang lưu trữ lớp", "Lớp đã được lưu trữ", async ct =>
    {
        if (SelectedClass is null || !AppServices.Dialogs.Confirm("Lưu trữ lớp", $"Lưu trữ lớp {SelectedClass.Name}?")) return;
        _ = await api.DeleteAsync<object>($"api/v1/classes/{SelectedClass.Id}", ct);
        Classes.Remove(SelectedClass);
        SelectedClass = Classes.FirstOrDefault();
        Students.Clear();
    });

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, OpenCommand, AddStudentCommand, SaveClassCommand, UpdateStudentCommand, RemoveStudentCommand, ExportCommand, ImportCommand, ArchiveCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class ExamManagementViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private CancellationTokenSource? detailLoadCts;
    private long detailLoadGeneration;
    private ExamSummaryDto? selectedExam;
    private ClassSummaryDto? selectedClass;
    private FileDescriptorDto? selectedFile;
    private string currentExamRowVersion = "1";
    private bool currentAutoZip;
    private bool currentRequireAtLeastOneFile = true;
    private string title = string.Empty;
    private string subject = string.Empty;
    private string description = string.Empty;
    private string duration = "60";
    private string allowedExtensions = ".pdf,.docx,.zip,.cs,.java,.py";

    public ExamManagementViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy);
        PublishCommand = new AsyncRelayCommand(PublishAsync, () => !IsBusy && CanPublish);
        CloneCommand = new AsyncRelayCommand(CloneAsync, () => !IsBusy && SelectedExam is not null);
        ArchiveCommand = new AsyncRelayCommand(ArchiveAsync, () => !IsBusy && SelectedExam is not null);
        UploadCommand = new AsyncRelayCommand(UploadFileAsync, () => !IsBusy && SelectedExam is not null);
        ImportQuizCommand = new AsyncRelayCommand(ImportQuizAsync, () => !IsBusy && SelectedExam?.Status == ExamStatus.Draft);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && SelectedExam is not null);
        DeleteFileCommand = new AsyncRelayCommand(DeleteFileAsync, () => !IsBusy && SelectedExam is not null && SelectedFile is not null);
        DownloadFileCommand = new AsyncRelayCommand(DownloadFileAsync, () => !IsBusy && SelectedExam is not null && SelectedFile is not null);
    }

    public ObservableCollection<ExamSummaryDto> Exams { get; } = new();
    public ObservableCollection<ClassSummaryDto> Classes { get; } = new();
    public ObservableCollection<FileDescriptorDto> Files { get; } = new();
    public ExamSummaryDto? SelectedExam { get => selectedExam; set { if (Set(ref selectedExam, value)) { Raise(nameof(PublishHint)); RaiseCommands(); } } }
    public ClassSummaryDto? SelectedClass { get => selectedClass; set => Set(ref selectedClass, value); }
    public FileDescriptorDto? SelectedFile { get => selectedFile; set { if (Set(ref selectedFile, value)) RaiseCommands(); } }
    public string Title { get => title; set => Set(ref title, value); }
    public string Subject { get => subject; set => Set(ref subject, value); }
    public string Description { get => description; set => Set(ref description, value); }
    public string Duration { get => duration; set => Set(ref duration, value); }
    public string AllowedExtensions { get => allowedExtensions; set => Set(ref allowedExtensions, value); }
    public bool CanPublish => SelectedExam is not null
        && SelectedExam.Status is not (ExamStatus.Archived or ExamStatus.Cancelled)
        && (SelectedExam.DeliveryType == ExamDeliveryType.MultipleChoice || !currentRequireAtLeastOneFile || Files.Count > 0);
    public string PublishHint => SelectedExam?.DeliveryType == ExamDeliveryType.MultipleChoice
        ? "Đề trắc nghiệm sẽ được kiểm tra câu hỏi và đáp án trên máy chủ khi phát hành."
        : currentRequireAtLeastOneFile && Files.Count == 0
        ? "Cần tải lên và hoàn tất ít nhất một file đề trước khi phát hành."
        : "Bài kiểm tra đã đáp ứng quy tắc file để phát hành.";
    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand PublishCommand { get; }
    public ICommand CloneCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand UploadCommand { get; }
    public ICommand ImportQuizCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteFileCommand { get; }
    public ICommand DownloadFileCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải bài kiểm tra", "Danh sách bài kiểm tra đã được cập nhật", async token =>
        {
            await RefreshExamsCoreAsync(SelectedExam?.Id, token);
        });
    }

    public async Task LoadSelectedExamAsync()
    {
        if (IsDisposed) return;
        try
        {
            Status = "Đang tải chi tiết bài kiểm tra";
            StatusTone = "primary";
            await LoadSelectedAsync(DisposeToken);
            if (!IsDisposed)
            {
                Status = "Đã tải chi tiết bài kiểm tra";
                StatusTone = "success";
            }
        }
        catch (OperationCanceledException) when (DisposeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private async Task RefreshExamsCoreAsync(Guid? selectedId, CancellationToken ct)
    {
        var selectedClassId = SelectedClass?.Id;
        var classes = ApiGuard.Require(await api.GetClassesAsync(ct));
        var exams = ApiGuard.Require(await api.GetExamsAsync(ct));
        Classes.ReplaceWith(classes.Items.Where(x => x.Status == ClassStatus.Active));
        Exams.ReplaceWith(exams.Items);
        SelectedExam = selectedId.HasValue
            ? Exams.FirstOrDefault(x => x.Id == selectedId.Value) ?? Exams.FirstOrDefault()
            : Exams.FirstOrDefault();
        SelectedClass = selectedClassId.HasValue
            ? Classes.FirstOrDefault(x => x.Id == selectedClassId.Value) ?? Classes.FirstOrDefault()
            : Classes.FirstOrDefault();
        if (SelectedExam is not null)
            await LoadSelectedAsync(ct);
        else
        {
            Files.Clear();
            Raise(nameof(PublishHint));
            RaiseCommands();
        }
    }

    private async Task LoadSelectedAsync(CancellationToken ct)
    {
        var target = SelectedExam;
        if (target is null) return;
        var generation = Interlocked.Increment(ref detailLoadGeneration);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, DisposeToken);
        var previous = Interlocked.Exchange(ref detailLoadCts, linked);
        previous?.Cancel();
        previous?.Dispose();
        ExamDetailDto detail;
        try
        {
            detail = ApiGuard.Require(await api.GetAsync<ExamDetailDto>($"api/v1/exams/{target.Id}", linked.Token));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (Interlocked.CompareExchange(ref detailLoadCts, null, linked) == linked)
                linked.Dispose();
        }
        if (generation != Interlocked.Read(ref detailLoadGeneration) || SelectedExam?.Id != target.Id)
            return;
        Title = detail.Title;
        Subject = detail.Subject;
        Description = detail.Description ?? string.Empty;
        Duration = detail.DurationMinutes.ToString();
        AllowedExtensions = string.Join(',', detail.FileRule.AllowedExtensions);
        currentAutoZip = detail.FileRule.AutoZip;
        currentRequireAtLeastOneFile = detail.FileRule.RequireAtLeastOneFile;
        SelectedClass = detail.ClassId.HasValue ? Classes.FirstOrDefault(x => x.Id == detail.ClassId.Value) : null;
        Files.ReplaceWith(detail.Files);
        SelectedFile = Files.FirstOrDefault();
        currentExamRowVersion = detail.RowVersion;
        Raise(nameof(CanPublish));
        Raise(nameof(PublishHint));
        RaiseCommands();
    }

    private Task SaveAsync() => RunAsync("Đang lưu bài kiểm tra", "Bài kiểm tra đã được cập nhật", async ct =>
    {
        if (SelectedExam is null) return;
        if (!int.TryParse(Duration, out var minutes) || minutes <= 0) throw new InvalidOperationException("Thời lượng phải là số phút lớn hơn 0.");
        var rule = new FileRuleDto(AllowedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), 100L * 1024 * 1024, 500L * 1024 * 1024, 20, currentAutoZip, currentRequireAtLeastOneFile);
        var updated = ApiGuard.Require(await api.PutAsync<UpdateExamRequest, ExamDetailDto>($"api/v1/exams/{SelectedExam.Id}", new(SelectedClass?.Id, Title.Trim(), Subject.Trim(), Description.Trim(), minutes, rule, currentExamRowVersion), ct));
        await RefreshExamsCoreAsync(updated.Id, ct);
    });

    private Task DeleteFileAsync() => RunAsync("Đang xóa file đề", "File đề đã được xóa", async ct =>
    {
        if (SelectedExam is null || SelectedFile is null || !AppServices.Dialogs.Confirm("Xóa file đề", $"Xóa {SelectedFile.Name}?")) return;
        var examId = SelectedExam.Id;
        _ = await api.DeleteAsync<object>($"api/v1/exams/{SelectedExam.Id}/files/{SelectedFile.Id}", ct);
        await RefreshExamsCoreAsync(examId, ct);
    });

    private Task DownloadFileAsync() => RunAsync("Đang tải file đề", "File đề đã được lưu", async ct =>
    {
        if (SelectedExam is null || SelectedFile is null) return;
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        await api.DownloadFileAsync($"api/v1/exams/{SelectedExam.Id}/files/{SelectedFile.Id}/content", Path.Combine(folder, SelectedFile.Name), null, ct);
    });

    private Task CreateAsync() => RunAsync("Đang tạo bài kiểm tra", "Bài kiểm tra đã được tạo ở trạng thái nháp", async ct =>
    {
        if (!int.TryParse(Duration, out var minutes) || minutes <= 0) throw new InvalidOperationException("Thời lượng phải là số phút lớn hơn 0.");
        if (string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(Subject)) throw new InvalidOperationException("Tiêu đề và môn học là bắt buộc.");
        var rule = new FileRuleDto(AllowedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), 100L * 1024 * 1024, 500L * 1024 * 1024, 20, false, true);
        var exam = ApiGuard.Require(await api.PostAsync<CreateExamRequest, ExamDetailDto>("api/v1/exams", new(SelectedClass?.Id, Title.Trim(), Subject.Trim(), Description.Trim(), minutes, rule), ct));
        await RefreshExamsCoreAsync(exam.Id, ct);
    });

    private Task PublishAsync() => RunAsync("Đang phát hành đề", "Bài kiểm tra đã được phát hành", async ct =>
    {
        if (SelectedExam is null || !AppServices.Dialogs.Confirm("Phát hành bài kiểm tra", "Sau khi phát hành, thay file đề sẽ tạo phiên bản mới. Tiếp tục?")) return;
        var detail = ApiGuard.Require(await api.PostAsync<object, ExamDetailDto>($"api/v1/exams/{SelectedExam.Id}/publish", new { }, ct));
        await RefreshExamsCoreAsync(detail.Id, ct);
    });

    private Task CloneAsync() => RunAsync("Đang nhân bản", "Đã tạo bản sao bài kiểm tra", async ct =>
    {
        if (SelectedExam is null) return;
        var detail = ApiGuard.Require(await api.PostAsync<object, ExamDetailDto>($"api/v1/exams/{SelectedExam.Id}/clone", new { }, ct));
        await RefreshExamsCoreAsync(detail.Id, ct);
    });

    private Task ArchiveAsync() => RunAsync("Đang lưu trữ", "Bài kiểm tra đã được lưu trữ", async ct =>
    {
        if (SelectedExam is null || !AppServices.Dialogs.Confirm("Lưu trữ bài kiểm tra", $"Lưu trữ {SelectedExam.Title}?")) return;
        _ = await api.PostAsync<object, object>($"api/v1/exams/{SelectedExam.Id}/archive", new { }, ct);
        await RefreshExamsCoreAsync(null, ct);
    });

    private Task UploadFileAsync() => RunAsync("Đang tải file đề", "File đề đã được tải và xác minh", async ct =>
    {
        if (SelectedExam is null) return;
        var file = AppServices.Files.PickFile("Tài liệu|*.pdf;*.docx;*.xlsx;*.pptx;*.zip;*.txt|Tất cả file|*.*");
        if (file is null) return;
        var info = new FileInfo(file);
        var sha = await ComputeShaAsync(file, ct);
        var init = ApiGuard.Require(await api.PostAsync<InitFileUploadRequest, InitFileUploadResponse>($"api/v1/exams/{SelectedExam.Id}/files/init", new(info.Name, info.Length, sha, "application/octet-stream", null), ct));
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, init.ChunkSizeBytes, true);
        var buffer = new byte[init.ChunkSizeBytes];
        for (var index = 0; index < init.TotalChunks; index++)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            await using var chunk = new MemoryStream(buffer, 0, read, false, true);
            _ = await api.UploadChunkAsync($"api/v1/exams/{SelectedExam.Id}/files/{init.FileId}/chunks/{index}", chunk, read, null, ct);
            Status = $"Đang tải file đề: {index + 1}/{init.TotalChunks} phần";
        }
        var descriptor = ApiGuard.Require(await api.PostAsync<FinalizeFileUploadRequest, FileDescriptorDto>($"api/v1/exams/{SelectedExam.Id}/files/{init.FileId}/finalize", new(sha), ct));
        await RefreshExamsCoreAsync(SelectedExam.Id, ct);
    });

    private Task ImportQuizAsync() => RunAsync("Đang nhập đề trắc nghiệm", "Đề trắc nghiệm đã được kiểm tra và lưu", async ct =>
    {
        if (SelectedExam is null) return;
        var path = AppServices.Files.PickFile("Đề trắc nghiệm có cấu trúc|*.json;*.csv;*.xlsx");
        if (path is null) return;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var result = ApiGuard.Require(await api.PostAsync<QuizImportFileRequest, QuizImportResultDto>(
            $"api/v1/exams/{SelectedExam.Id}/quiz/import",
            new(Path.GetFileName(path), Convert.ToBase64String(bytes)), ct));
        Status = $"Đã nhập {result.QuestionCount} câu · tổng {result.MaxScore:0.##} điểm";
        await RefreshExamsCoreAsync(SelectedExam.Id, ct);
    });

    private static async Task<string> ComputeShaAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, PublishCommand, CloneCommand, ArchiveCommand, UploadCommand, ImportQuizCommand, SaveCommand, DeleteFileCommand, DownloadFileCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }

    public override void Dispose()
    {
        detailLoadCts?.Cancel();
        detailLoadCts?.Dispose();
        detailLoadCts = null;
        base.Dispose();
    }
}

public sealed class SessionManagementViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private ExamSummaryDto? selectedExam;
    private SessionSummaryDto? selectedSession;
    private string roomCode = string.Empty;
    private string capacity = "36";
    private bool autoApprove;
    private SessionAccessMode accessMode = SessionAccessMode.LanOnly;

    public SessionManagementViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy && SelectedExam is not null);
        OpenCommand = new AsyncRelayCommand(() => TransitionAsync("open", "Phòng thi đã mở và sẵn sàng nhận học sinh"), () => !IsBusy && SelectedSession?.Status == SessionStatus.Draft);
        DistributeCommand = new AsyncRelayCommand(() => TransitionAsync("distribute", "Đề thi đã được phân phối"), () => !IsBusy && SelectedSession?.Status == SessionStatus.Waiting);
        StartCommand = new AsyncRelayCommand(() => TransitionAsync("start", "Phiên thi đã bắt đầu"), () => !IsBusy && (SelectedSession?.Status is SessionStatus.Waiting or SessionStatus.Distributing));
        PauseCommand = new AsyncRelayCommand(() => TransitionAsync("pause", "Phiên thi đã tạm dừng"), () => !IsBusy && SelectedSession?.Status == SessionStatus.InProgress);
        ResumeCommand = new AsyncRelayCommand(() => TransitionAsync("resume", "Phiên thi đã tiếp tục"), () => !IsBusy && SelectedSession?.Status == SessionStatus.Paused);
        CollectCommand = new AsyncRelayCommand(() => TransitionAsync("collect", "Hệ thống đang thu bài"), () => !IsBusy && (SelectedSession?.Status is SessionStatus.InProgress or SessionStatus.Paused));
        EndCommand = new AsyncRelayCommand(EndAsync, () => !IsBusy && (SelectedSession?.Status is SessionStatus.InProgress or SessionStatus.Paused or SessionStatus.Collecting));
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => !IsBusy && SelectedSession?.Status == SessionStatus.Draft);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !IsBusy && SelectedSession?.Status is SessionStatus.Draft or SessionStatus.Waiting);
    }

    public ObservableCollection<ExamSummaryDto> Exams { get; } = new();
    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ExamSummaryDto? SelectedExam { get => selectedExam; set { if (Set(ref selectedExam, value)) RaiseCommands(); } }
    public SessionSummaryDto? SelectedSession
    {
        get => selectedSession;
        set
        {
            if (!Set(ref selectedSession, value)) return;
            if (value is not null)
            {
                AutoApprove = value.AutoApprove;
                AccessMode = value.AccessMode;
            }
            RaiseCommands();
        }
    }
    public string RoomCode { get => roomCode; set => Set(ref roomCode, value); }
    public string Capacity { get => capacity; set => Set(ref capacity, value); }
    public bool AutoApprove { get => autoApprove; set => Set(ref autoApprove, value); }
    public IReadOnlyList<SessionAccessMode> AccessModes { get; } = Enum.GetValues<SessionAccessMode>();
    public SessionAccessMode AccessMode { get => accessMode; set => Set(ref accessMode, value); }
    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand DistributeCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CollectCommand { get; }
    public ICommand EndCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SaveSettingsCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải phòng thi", "Danh sách phòng thi đã được cập nhật", async token =>
        {
            await RefreshSessionsCoreAsync(SelectedExam?.Id, SelectedSession?.Id, token);
        });
    }

    private async Task RefreshSessionsCoreAsync(Guid? examId, Guid? sessionId, CancellationToken ct)
    {
        var exams = ApiGuard.Require(await api.GetExamsAsync(ct));
        var sessions = ApiGuard.Require(await api.GetSessionsAsync(ct));
        Exams.ReplaceWith(exams.Items.Where(x => x.Status == ExamStatus.Published));
        Sessions.ReplaceWith(sessions.Items);
        SelectedExam = examId.HasValue
            ? Exams.FirstOrDefault(x => x.Id == examId.Value) ?? Exams.FirstOrDefault()
            : Exams.FirstOrDefault();
        SelectedSession = sessionId.HasValue
            ? Sessions.FirstOrDefault(x => x.Id == sessionId.Value) ?? Sessions.FirstOrDefault()
            : Sessions.FirstOrDefault();
    }

    private Task CreateAsync() => RunAsync("Đang tạo phòng thi", "Phòng thi đã được tạo ở trạng thái nháp", async ct =>
    {
        if (SelectedExam is null) return;
        if (!int.TryParse(Capacity, out var cap) || cap <= 0) throw new InvalidOperationException("Sức chứa phải lớn hơn 0.");
        var detail = ApiGuard.Require(await api.PostAsync<CreateSessionRequest, SessionDetailDto>("api/v1/sessions", new(SelectedExam.Id, SelectedExam.ClassId, DateTimeOffset.UtcNow.AddMinutes(5), $"{{\"autoApprove\":{AutoApprove.ToString().ToLowerInvariant()}}}", AutoApprove, cap, string.IsNullOrWhiteSpace(RoomCode) ? null : RoomCode.Trim(), AccessMode), ct));
        await RefreshSessionsCoreAsync(SelectedExam.Id, detail.Summary.Id, ct);
    });

    private Task TransitionAsync(string action, string success) => RunAsync("Đang cập nhật trạng thái phòng", success, async ct =>
    {
        if (SelectedSession is null) return;
        var detail = ApiGuard.Require(await api.PostAsync<object, SessionDetailDto>($"api/v1/sessions/{SelectedSession.Id}/{action}", new { }, ct));
        ReplaceSelected(detail.Summary);
    });

    private Task SaveSettingsAsync() => RunAsync("Đang lưu chế độ duyệt", "Chế độ duyệt học sinh đã được cập nhật", async ct =>
    {
        if (SelectedSession is null) return;
        var detail = ApiGuard.Require(await api.GetSessionAsync(SelectedSession.Id, ct));
        var approvePending = false;
        if (!detail.Summary.AutoApprove && AutoApprove && detail.Summary.Counts.Pending > 0)
        {
            approvePending = AppServices.Dialogs.Confirm(
                "Duyệt các yêu cầu đang chờ",
                $"Có {detail.Summary.Counts.Pending} học sinh đang chờ. Bạn có muốn duyệt toàn bộ khi bật tự động duyệt không?");
            if (!approvePending)
            {
                AutoApprove = false;
                Status = "Chưa thay đổi chế độ; các yêu cầu đang chờ được giữ nguyên";
                return;
            }
        }
        var settings = $"{{\"autoApprove\":{AutoApprove.ToString().ToLowerInvariant()}}}";
        var updated = ApiGuard.Require(await api.PutAsync<UpdateSessionRequest, SessionDetailDto>(
            $"api/v1/sessions/{detail.Summary.Id}",
            new(detail.PlannedStartUtc, settings, AutoApprove, detail.Capacity, detail.Summary.RowVersion, approvePending), ct));
        ReplaceSelected(updated.Summary);
    });

    private Task EndAsync() => RunAsync("Đang kết thúc phiên", "Phiên thi đã kết thúc và được khóa nghiệp vụ", async ct =>
    {
        if (SelectedSession is null || !AppServices.Dialogs.Confirm("Kết thúc phiên", "Hệ thống sẽ kiểm tra các bài đang tải lên. Tiếp tục kết thúc?")) return;
        var detail = ApiGuard.Require(await api.PostAsync<EndSessionRequest, SessionDetailDto>($"api/v1/sessions/{SelectedSession.Id}/end", new(true, "Giáo viên xác nhận kết thúc."), ct));
        ReplaceSelected(detail.Summary);
    });

    private Task CancelAsync() => RunAsync("Đang hủy phòng", "Phòng thi đã được hủy", async ct =>
    {
        if (SelectedSession is null || !AppServices.Dialogs.Confirm("Hủy phòng thi", "Hủy phòng thi đang ở trạng thái nháp?")) return;
        var detail = ApiGuard.Require(await api.PostAsync<EndSessionRequest, SessionDetailDto>($"api/v1/sessions/{SelectedSession.Id}/cancel", new(false, "Giáo viên hủy phòng nháp."), ct));
        ReplaceSelected(detail.Summary);
    });

    private void ReplaceSelected(SessionSummaryDto summary)
    {
        if (SelectedSession is null) return;
        var index = Sessions.IndexOf(SelectedSession);
        if (index >= 0) Sessions[index] = summary;
        SelectedSession = summary;
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, OpenCommand, DistributeCommand, StartCommand, PauseCommand, ResumeCommand, CollectCommand, EndCommand, CancelCommand, SaveSettingsCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class LobbyViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private SessionSummaryDto? selectedSession;
    private ParticipantDto? selectedParticipant;
    private string message = "Kỳ thi sẽ bắt đầu trong 5 phút. Vui lòng kiểm tra thiết bị.";

    public LobbyViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        LoadSessionCommand = new AsyncRelayCommand(LoadSessionAsync, () => !IsBusy && SelectedSession is not null);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => !IsBusy && SelectedParticipant is not null);
        RejectCommand = new AsyncRelayCommand(RejectAsync, () => !IsBusy && SelectedParticipant is not null);
        BulkApproveCommand = new AsyncRelayCommand(BulkApproveAsync, () => !IsBusy && Participants.Count > 0);
        MessageCommand = new AsyncRelayCommand(SendMessageAsync, () => !IsBusy && SelectedSession is not null);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && SelectedSession is not null);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<ParticipantDto> Participants { get; } = new();
    public SessionSummaryDto? SelectedSession { get => selectedSession; set { if (Set(ref selectedSession, value)) RaiseCommands(); } }
    public ParticipantDto? SelectedParticipant { get => selectedParticipant; set { if (Set(ref selectedParticipant, value)) RaiseCommands(); } }
    public string Message { get => message; set => Set(ref message, value); }
    public ICommand RefreshCommand { get; }
    public ICommand LoadSessionCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand BulkApproveCommand { get; }
    public ICommand MessageCommand { get; }
    public ICommand StartCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải phòng chờ", "Phòng chờ đã được cập nhật", async token =>
        {
            var data = ApiGuard.Require(await api.GetSessionsAsync(token));
            Sessions.ReplaceWith(data.Items.Where(x => x.Status is SessionStatus.Waiting or SessionStatus.Draft or SessionStatus.Distributing));
            SelectedSession ??= Sessions.FirstOrDefault();
            if (SelectedSession is not null) await LoadSessionCoreAsync(token);
        });
    }

    private Task LoadSessionAsync() => RunAsync("Đang tải học sinh", "Danh sách học sinh đã được cập nhật", LoadSessionCoreAsync);
    private async Task LoadSessionCoreAsync(CancellationToken ct)
    {
        if (SelectedSession is null) return;
        var detail = ApiGuard.Require(await api.GetSessionAsync(SelectedSession.Id, ct));
        Participants.ReplaceWith(detail.Participants);
        SelectedParticipant = Participants.FirstOrDefault();
    }

    private Task ApproveAsync() => RunAsync("Đang duyệt học sinh", "Học sinh đã được duyệt", async ct =>
    {
        if (SelectedSession is null || SelectedParticipant is null) return;
        var updated = ApiGuard.Require(await api.PostAsync<object, ParticipantDto>($"api/v1/sessions/{SelectedSession.Id}/participants/{SelectedParticipant.Id}/approve", new { }, ct));
        ReplaceParticipant(updated);
    });

    private Task RejectAsync() => RunAsync("Đang từ chối yêu cầu", "Yêu cầu tham gia đã bị từ chối", async ct =>
    {
        if (SelectedSession is null || SelectedParticipant is null || !AppServices.Dialogs.Confirm("Từ chối học sinh", $"Từ chối {SelectedParticipant.DisplayName}?")) return;
        _ = await api.PostAsync<object, object>($"api/v1/sessions/{SelectedSession.Id}/participants/{SelectedParticipant.Id}/reject", new { reason = "Thông tin tham gia chưa hợp lệ." }, ct);
        Participants.Remove(SelectedParticipant);
        SelectedParticipant = Participants.FirstOrDefault();
    });

    private Task BulkApproveAsync() => RunAsync("Đang duyệt hàng loạt", "Đã duyệt các học sinh đang chờ", async ct =>
    {
        if (SelectedSession is null) return;
        var ids = Participants.Where(x => x.Status == ParticipantStatus.PendingApproval).Select(x => x.Id).ToArray();
        var updated = ApiGuard.Require(await api.PostAsync<BulkApproveRequest, IReadOnlyList<ParticipantDto>>($"api/v1/sessions/{SelectedSession.Id}/participants/bulk-approve", new(ids), ct));
        Participants.ReplaceWith(updated);
    });

    private Task SendMessageAsync() => RunAsync("Đang gửi thông báo", "Thông báo đã được gửi tới phòng chờ", async ct =>
    {
        if (SelectedSession is null || string.IsNullOrWhiteSpace(Message)) return;
        _ = ApiGuard.Require(await api.PostAsync<SendMessageRequest, MessageDto>($"api/v1/sessions/{SelectedSession.Id}/messages", new(null, MessageType.Information, Message.Trim()), ct));
    });

    private Task StartAsync() => RunAsync("Đang bắt đầu phiên", "Phiên thi đã bắt đầu", async ct =>
    {
        if (SelectedSession is null) return;
        var pending = Participants.Count(x => x.Status == ParticipantStatus.PendingApproval);
        if (pending > 0 && !AppServices.Dialogs.Confirm("Bắt đầu phiên", $"Còn {pending} học sinh chưa được duyệt. Vẫn bắt đầu?")) return;
        _ = ApiGuard.Require(await api.PostAsync<object, SessionDetailDto>($"api/v1/sessions/{SelectedSession.Id}/start", new { }, ct));
    });

    private void ReplaceParticipant(ParticipantDto updated)
    {
        var existing = Participants.FirstOrDefault(x => x.Id == updated.Id);
        if (existing is null) return;
        var index = Participants.IndexOf(existing);
        Participants[index] = updated;
        SelectedParticipant = updated;
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, LoadSessionCommand, ApproveCommand, RejectCommand, BulkApproveCommand, MessageCommand, StartCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class SubmissionCenterViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private SessionSummaryDto? selectedSession;
    private SubmissionSummaryDto? selectedSubmission;
    private string reason = "File nộp chưa đúng quy định.";

    public SubmissionCenterViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        LoadCommand = new AsyncRelayCommand(LoadSubmissionsAsync, () => !IsBusy && SelectedSession is not null);
        RejectCommand = new AsyncRelayCommand(RejectAsync, () => !IsBusy && SelectedSubmission is not null);
        ResubmitCommand = new AsyncRelayCommand(ResubmitAsync, () => !IsBusy && SelectedSubmission is not null);
        CopyReceiptCommand = new RelayCommand(CopyReceipt);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && SelectedSubmission?.Files.Count > 0);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<SubmissionSummaryDto> Submissions { get; } = new();
    public SessionSummaryDto? SelectedSession { get => selectedSession; set { if (Set(ref selectedSession, value)) RaiseCommands(); } }
    public SubmissionSummaryDto? SelectedSubmission { get => selectedSubmission; set { if (Set(ref selectedSubmission, value)) RaiseCommands(); } }
    public string Reason { get => reason; set => Set(ref reason, value); }
    public ICommand RefreshCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand ResubmitCommand { get; }
    public ICommand CopyReceiptCommand { get; }
    public ICommand DownloadCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải dữ liệu thu bài", "Trung tâm thu bài đã được cập nhật", async token =>
        {
            var sessions = ApiGuard.Require(await api.GetSessionsAsync(token));
            Sessions.ReplaceWith(sessions.Items);
            SelectedSession ??= Sessions.FirstOrDefault();
            if (SelectedSession is not null) await LoadSubmissionsCoreAsync(token);
        });
    }

    private Task LoadSubmissionsAsync() => RunAsync("Đang tải bài nộp", "Danh sách bài nộp đã được cập nhật", LoadSubmissionsCoreAsync);
    private async Task LoadSubmissionsCoreAsync(CancellationToken ct)
    {
        if (SelectedSession is null) return;
        var data = ApiGuard.Require(await api.GetSubmissionsAsync(SelectedSession.Id, ct));
        Submissions.ReplaceWith(data.Items);
        SelectedSubmission = Submissions.FirstOrDefault();
    }

    private Task RejectAsync() => RunAsync("Đang từ chối bài", "Bài nộp đã bị từ chối và vẫn được lưu lịch sử", async ct =>
    {
        if (SelectedSubmission is null || !AppServices.Dialogs.Confirm("Từ chối bài nộp", $"Từ chối attempt {SelectedSubmission.AttemptNumber} của {SelectedSubmission.DisplayName}?")) return;
        _ = await api.PostAsync<RejectSubmissionRequest, object>($"api/v1/submissions/{SelectedSubmission.Id}/reject", new(Reason), ct);
        await LoadSubmissionsCoreAsync(ct);
    });

    private Task ResubmitAsync() => RunAsync("Đang cấp quyền nộp lại", "Học sinh đã được phép tạo attempt mới", async ct =>
    {
        if (SelectedSubmission is null) return;
        _ = await api.PostAsync<AllowResubmitRequest, object>($"api/v1/participants/{SelectedSubmission.ParticipantId}/allow-resubmit", new(Reason), ct);
    });

    private async Task DownloadAsync()
    {
        await RunAsync("Đang tải bài nộp", "File bài nộp đã được lưu", async ct =>
        {
            if (SelectedSubmission?.Files.FirstOrDefault() is not { } file) return;
            var folder = AppServices.Folders.PickFolder();
            if (folder is null) return;
            await api.DownloadFileAsync($"api/v1/submissions/{SelectedSubmission.Id}/files/{file.Id}/content", Path.Combine(folder, file.Name), null, ct);
        });
    }

    private void CopyReceipt()
    {
        if (!string.IsNullOrWhiteSpace(SelectedSubmission?.ReceiptCode))
        {
            AppServices.Clipboard.SetText(SelectedSubmission.ReceiptCode);
            Status = "Mã biên nhận đã được sao chép";
            StatusTone = "success";
        }
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, LoadCommand, RejectCommand, ResubmitCommand, DownloadCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class ExportCenterViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private SessionSummaryDto? selectedSession;
    private ExportJobDto? selectedJob;
    private string namingPattern = "{class}/{studentCode}_{studentName}";
    private bool includeFiles = true;
    private bool includeManifest = true;
    private bool includeReceipts = true;
    private bool includeAudit;

    public ExportCenterViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy && SelectedSession is not null);
        PollCommand = new AsyncRelayCommand(PollAsync, () => !IsBusy && SelectedJob is not null);
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => !IsBusy && SelectedJob is not null);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && SelectedJob?.Status == ExportStatus.Completed);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<ExportJobDto> Jobs { get; } = new();
    public SessionSummaryDto? SelectedSession { get => selectedSession; set { if (Set(ref selectedSession, value)) RaiseCommands(); } }
    public ExportJobDto? SelectedJob { get => selectedJob; set { if (Set(ref selectedJob, value)) RaiseCommands(); } }
    public string NamingPattern { get => namingPattern; set => Set(ref namingPattern, value); }
    public bool IncludeFiles { get => includeFiles; set => Set(ref includeFiles, value); }
    public bool IncludeManifest { get => includeManifest; set => Set(ref includeManifest, value); }
    public bool IncludeReceipts { get => includeReceipts; set => Set(ref includeReceipts, value); }
    public bool IncludeAudit { get => includeAudit; set => Set(ref includeAudit, value); }
    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand PollCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DownloadCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải phiên có thể xuất", "Trung tâm xuất dữ liệu đã sẵn sàng", async token =>
        {
            var sessions = ApiGuard.Require(await api.GetSessionsAsync(token));
            Sessions.ReplaceWith(sessions.Items);
            SelectedSession ??= Sessions.FirstOrDefault();
        });
    }

    private Task CreateAsync() => RunAsync("Đang tạo export job", "Export job đã được tạo", async ct =>
    {
        if (SelectedSession is null) return;
        var job = ApiGuard.Require(await api.PostAsync<CreateExportRequest, ExportJobDto>("api/v1/exports", new(SelectedSession.Id, IncludeFiles, IncludeManifest, IncludeReceipts, IncludeAudit, "zip", NamingPattern), ct));
        Jobs.Insert(0, job);
        SelectedJob = job;
    });

    private Task PollAsync() => RunAsync("Đang cập nhật tiến trình", "Tiến trình export đã được cập nhật", async ct =>
    {
        if (SelectedJob is null) return;
        var job = ApiGuard.Require(await api.GetAsync<ExportJobDto>($"api/v1/exports/{SelectedJob.Id}", ct));
        ReplaceJob(job);
    });

    private Task CancelAsync() => RunAsync("Đang hủy export", "Export job đã được hủy", async ct =>
    {
        if (SelectedJob is null) return;
        _ = await api.PostAsync<object, object>($"api/v1/exports/{SelectedJob.Id}/cancel", new { }, ct);
        ReplaceJob(SelectedJob with { Status = ExportStatus.Cancelled });
    });

    private Task DownloadAsync() => RunAsync("Đang tải file export", "File export đã được lưu", async ct =>
    {
        if (SelectedJob is null) return;
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        await api.DownloadFileAsync($"api/v1/exports/{SelectedJob.Id}/download", Path.Combine(folder, SelectedJob.OutputFileName ?? "ExamTransfer-export.zip"), null, ct);
    });

    private void ReplaceJob(ExportJobDto job)
    {
        var old = Jobs.FirstOrDefault(x => x.Id == job.Id);
        if (old is not null) Jobs[Jobs.IndexOf(old)] = job;
        SelectedJob = job;
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, PollCommand, CancelCommand, DownloadCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class GradingCenterViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private SubmissionSummaryDto? selectedSubmission;
    private GradeDto? grade;
    private string score = "8.5";
    private string maxScore = "10";
    private string comment = string.Empty;

    public GradingCenterViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy && SelectedSubmission is not null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && SelectedSubmission is not null);
        ReturnCommand = new AsyncRelayCommand(ReturnAsync, () => !IsBusy && SelectedSubmission is not null);
        ReopenCommand = new AsyncRelayCommand(ReopenAsync, () => !IsBusy && SelectedSubmission is not null);
    }

    public ObservableCollection<SubmissionSummaryDto> Queue { get; } = new();
    public ObservableCollection<RubricScoreDto> Rubric { get; } = new();
    public SubmissionSummaryDto? SelectedSubmission { get => selectedSubmission; set { if (Set(ref selectedSubmission, value)) RaiseCommands(); } }
    public GradeDto? Grade { get => grade; private set => Set(ref grade, value); }
    public string Score { get => score; set => Set(ref score, value); }
    public string MaxScore { get => maxScore; set => Set(ref maxScore, value); }
    public string Comment { get => comment; set => Set(ref comment, value); }
    public ICommand RefreshCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ReturnCommand { get; }
    public ICommand ReopenCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải hàng đợi chấm", "Hàng đợi chấm bài đã được cập nhật", async token =>
        {
            var data = ApiGuard.Require(await api.GetAsync<PagedResult<SubmissionSummaryDto>>("api/v1/grading/queue", token));
            Queue.ReplaceWith(data.Items);
            SelectedSubmission ??= Queue.FirstOrDefault();
            if (SelectedSubmission is not null) await OpenCoreAsync(token);
        });
    }

    private Task OpenAsync() => RunAsync("Đang mở bài chấm", "Đã tải điểm và rubric", OpenCoreAsync);
    private async Task OpenCoreAsync(CancellationToken ct)
    {
        if (SelectedSubmission is null) return;
        Grade = ApiGuard.Require(await api.GetAsync<GradeDto>($"api/v1/grading/submissions/{SelectedSubmission.Id}", ct));
        Score = Grade.Score?.ToString() ?? string.Empty;
        MaxScore = Grade.MaxScore.ToString();
        Comment = Grade.GeneralComment ?? string.Empty;
        Rubric.ReplaceWith(Grade.RubricScores);
    }

    private Task SaveAsync() => RunAsync("Đang lưu điểm", "Điểm và nhận xét đã được lưu", async ct =>
    {
        if (SelectedSubmission is null) return;
        if (!decimal.TryParse(MaxScore, out var max) || max <= 0) throw new InvalidOperationException("Thang điểm không hợp lệ.");
        decimal? parsedScore = string.IsNullOrWhiteSpace(Score) ? null : decimal.Parse(Score);
        var request = new SaveGradeRequest(parsedScore, max, Rubric.ToArray(), Comment, Grade?.RowVersion ?? "1");
        Grade = ApiGuard.Require(await api.PutAsync<SaveGradeRequest, GradeDto>($"api/v1/grading/submissions/{SelectedSubmission.Id}", request, ct));
    });

    private Task ReturnAsync() => RunAsync("Đang công bố kết quả", "Kết quả đã được trả cho học sinh", async ct =>
    {
        if (SelectedSubmission is null || !AppServices.Dialogs.Confirm("Trả kết quả", "Công bố điểm, nhận xét và file đã chấm cho học sinh?")) return;
        Grade = ApiGuard.Require(await api.PostAsync<ReturnGradeRequest, GradeDto>($"api/v1/grading/submissions/{SelectedSubmission.Id}/return", new("Kết quả đã được công bố."), ct));
    });

    private Task ReopenAsync() => RunAsync("Đang mở lại kết quả", "Kết quả đã được mở để chỉnh sửa", async ct =>
    {
        if (SelectedSubmission is null) return;
        Grade = ApiGuard.Require(await api.PostAsync<ReopenGradeRequest, GradeDto>($"api/v1/grading/submissions/{SelectedSubmission.Id}/reopen", new("Điều chỉnh theo rà soát của giáo viên."), ct));
    });

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, OpenCommand, SaveCommand, ReturnCommand, ReopenCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class ControlCenterViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private SessionSummaryDto? selectedSession;
    private ViolationDto? selectedViolation;
    private bool fullscreen = true;
    private bool emergencyExit = true;
    private string focusRule = "WarnOnFocusLost";
    private string clipboardRule = "BlockPaste";
    private string blockedProcesses = "chrome.exe,msedge.exe,firefox.exe";
    private string networkRule = "LocalOnly";

    public ControlCenterViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        LoadCommand = new AsyncRelayCommand(LoadControlAsync, () => !IsBusy && SelectedSession is not null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && SelectedSession is not null);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && SelectedSession is not null);
        AcknowledgeCommand = new AsyncRelayCommand(AcknowledgeAsync, () => !IsBusy && SelectedViolation is not null);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<DeviceControlStatusDto> Devices { get; } = new();
    public ObservableCollection<ViolationDto> Violations { get; } = new();
    public SessionSummaryDto? SelectedSession { get => selectedSession; set { if (Set(ref selectedSession, value)) RaiseCommands(); } }
    public ViolationDto? SelectedViolation { get => selectedViolation; set { if (Set(ref selectedViolation, value)) RaiseCommands(); } }
    public bool Fullscreen { get => fullscreen; set => Set(ref fullscreen, value); }
    public bool EmergencyExit { get => emergencyExit; set => Set(ref emergencyExit, value); }
    public string FocusRule { get => focusRule; set => Set(ref focusRule, value); }
    public string ClipboardRule { get => clipboardRule; set => Set(ref clipboardRule, value); }
    public string BlockedProcesses { get => blockedProcesses; set => Set(ref blockedProcesses, value); }
    public string NetworkRule { get => networkRule; set => Set(ref networkRule, value); }
    public ICommand RefreshCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand AcknowledgeCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải phiên thi", "Trung tâm kiểm soát đã sẵn sàng", async token =>
        {
            var data = ApiGuard.Require(await api.GetSessionsAsync(token));
            Sessions.ReplaceWith(data.Items.Where(x => x.Status is SessionStatus.Waiting or SessionStatus.InProgress or SessionStatus.Paused));
            SelectedSession ??= Sessions.FirstOrDefault();
            if (SelectedSession is not null) await LoadControlCoreAsync(token);
        });
    }

    private Task LoadControlAsync() => RunAsync("Đang tải policy và vi phạm", "Dữ liệu kiểm soát đã được cập nhật", LoadControlCoreAsync);
    private async Task LoadControlCoreAsync(CancellationToken ct)
    {
        if (SelectedSession is null) return;
        var policy = await api.GetAsync<ControlPolicyDto?>($"api/v1/sessions/{SelectedSession.Id}/control-policy", ct);
        if (policy?.Success == true && policy.Data is not null)
        {
            Fullscreen = policy.Data.Fullscreen;
            EmergencyExit = policy.Data.EmergencyExit;
            FocusRule = policy.Data.FocusRule;
            ClipboardRule = policy.Data.ClipboardRule;
            BlockedProcesses = string.Join(',', policy.Data.BlockedProcesses);
            NetworkRule = policy.Data.NetworkRule;
        }
        var devices = ApiGuard.Require(await api.GetAsync<IReadOnlyList<DeviceControlStatusDto>>($"api/v1/sessions/{SelectedSession.Id}/devices/control-status", ct));
        var violations = ApiGuard.Require(await api.GetAsync<PagedResult<ViolationDto>>($"api/v1/sessions/{SelectedSession.Id}/violations", ct));
        Devices.ReplaceWith(devices);
        Violations.ReplaceWith(violations.Items);
        SelectedViolation = Violations.FirstOrDefault();
    }

    private Task SaveAsync() => RunAsync("Đang lưu policy", "Policy kiểm soát đã được lưu thành phiên bản mới", async ct =>
    {
        if (SelectedSession is null) return;
        var request = new SaveControlPolicyRequest(Fullscreen, FocusRule, ClipboardRule, Array.Empty<string>(), BlockedProcesses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), NetworkRule, EmergencyExit, 180, null);
        _ = ApiGuard.Require(await api.PutAsync<SaveControlPolicyRequest, ControlPolicyDto>($"api/v1/sessions/{SelectedSession.Id}/control-policy", request, ct));
    });

    private Task ApplyAsync() => RunAsync("Đang áp dụng policy", "Policy đã được gửi tới các thiết bị hỗ trợ", async ct =>
    {
        if (SelectedSession is null || !AppServices.Dialogs.Confirm("Áp dụng policy", "Gửi policy hiện tại tới toàn bộ thiết bị trong phiên?")) return;
        _ = await api.PostAsync<ApplyControlPolicyRequest, object>($"api/v1/sessions/{SelectedSession.Id}/control-policy/apply", new(null), ct);
        await LoadControlCoreAsync(ct);
    });

    private Task AcknowledgeAsync() => RunAsync("Đang đánh dấu vi phạm", "Vi phạm đã được ghi nhận là đã xử lý", async ct =>
    {
        if (SelectedViolation is null) return;
        _ = await api.PostAsync<object, object>($"api/v1/violations/{SelectedViolation.Id}/acknowledge", new { }, ct);
        await LoadControlCoreAsync(ct);
    });

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, LoadCommand, SaveCommand, ApplyCommand, AcknowledgeCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class HistoryAuditViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private string search = string.Empty;

    public HistoryAuditViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<AuditLogDto> Audits { get; } = new();
    public string Search { get => search; set => Set(ref search, value); }
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải lịch sử và audit", "Lịch sử hệ thống đã được cập nhật", async token =>
        {
            var history = ApiGuard.Require(await api.GetAsync<PagedResult<SessionSummaryDto>>($"api/v1/history/sessions?search={Uri.EscapeDataString(Search)}", token));
            var audits = ApiGuard.Require(await api.GetAsync<PagedResult<AuditLogDto>>($"api/v1/audit-logs?search={Uri.EscapeDataString(Search)}", token));
            Sessions.ReplaceWith(history.Items);
            Audits.ReplaceWith(audits.Items);
        });
    }

    private Task ExportAsync() => RunAsync("Đang xuất audit", "Báo cáo audit đã được tạo", async ct =>
    {
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        await api.PostDownloadFileAsync("api/v1/audit-logs/export", new Dictionary<string, string>(), Path.Combine(folder, "audit-log.csv"), null, ct);
    });

    protected override void RaiseCommands()
    {
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ExportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed class BackupCenterViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private BackupDto? selectedBackup;
    private bool includeFiles = true;
    private bool encrypt;

    public BackupCenterViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => !IsBusy && SelectedBackup is not null);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && SelectedBackup is not null);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && SelectedBackup is not null);
    }

    public ObservableCollection<BackupDto> Backups { get; } = new();
    public BackupDto? SelectedBackup { get => selectedBackup; set { if (Set(ref selectedBackup, value)) RaiseCommands(); } }
    public bool IncludeFiles { get => includeFiles; set => Set(ref includeFiles, value); }
    public bool Encrypt { get => encrypt; set => Set(ref encrypt, value); }
    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand DownloadCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải bản sao lưu", "Danh sách backup đã được cập nhật", async token =>
        {
            var data = ApiGuard.Require(await api.GetAsync<IReadOnlyList<BackupDto>>("api/v1/backups", token));
            Backups.ReplaceWith(data);
            SelectedBackup ??= Backups.FirstOrDefault();
        });
    }

    private Task CreateAsync() => RunAsync("Đang tạo backup", "Backup mới đã sẵn sàng", async ct =>
    {
        var backup = ApiGuard.Require(await api.PostAsync<CreateBackupRequest, BackupDto>("api/v1/backups", new(IncludeFiles, Encrypt, null), ct));
        Backups.Insert(0, backup);
        SelectedBackup = backup;
    });

    private Task ValidateAsync() => RunAsync("Đang kiểm tra checksum", "Checksum và schema backup hợp lệ", async ct =>
    {
        if (SelectedBackup is null) return;
        var backup = ApiGuard.Require(await api.PostAsync<object, BackupDto>($"api/v1/backups/{SelectedBackup.Id}/validate", new { }, ct));
        ReplaceBackup(backup);
    });

    private Task RestoreAsync() => RunAsync("Đang lên lịch khôi phục", "Khôi phục đã được lên lịch an toàn", async ct =>
    {
        if (SelectedBackup is null || !AppServices.Dialogs.Confirm("Khôi phục dữ liệu", "Ứng dụng sẽ tạo backup hiện tại và yêu cầu khởi động lại. Tiếp tục?")) return;
        _ = ApiGuard.Require(await api.PostAsync<RestoreBackupRequest, RestoreScheduledDto>($"api/v1/backups/{SelectedBackup.Id}/restore", new("RESTORE"), ct));
    });

    private Task DownloadAsync() => RunAsync("Đang tải backup", "File backup đã được lưu", async ct =>
    {
        if (SelectedBackup is null) return;
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        await api.DownloadFileAsync($"api/v1/backups/{SelectedBackup.Id}/download", Path.Combine(folder, SelectedBackup.FileName), null, ct);
    });

    private void ReplaceBackup(BackupDto backup)
    {
        var old = Backups.FirstOrDefault(x => x.Id == backup.Id);
        if (old is not null) Backups[Backups.IndexOf(old)] = backup;
        SelectedBackup = backup;
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, ValidateCommand, RestoreCommand, DownloadCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class SettingsPageViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private string serverPort = "5048";
    private bool useHttps;
    private bool discoveryEnabled = true;
    private string discoveryPort = "5050";
    private string storageRoot = @"C:\ProgramData\ExamTransfer";
    private string chunkSize = "4194304";
    private string maxUploads = "8";
    private bool cloudEnabled;
    private string supabaseUrl = string.Empty;
    private string supabasePublishableKey = string.Empty;
    private string organizationId = string.Empty;
    private string cloudEnvironment = "Development";
    private string cloudAccessMode = CloudAccessModes.UserSession;
    private bool cloudUseResumableUploads = true;
    private bool cloudSecretConfigured;
    private bool cloudAuthenticated;
    private string cloudAuthenticatedEmail = string.Empty;
    private string cloudEmail = string.Empty;
    private string cloudPassword = string.Empty;
    private string cloudConfigurationStatus = "Chưa cấu hình";
    private string diagnostics = "Chưa chạy chẩn đoán";
    private string cloudPreflight = "Chưa kiểm tra kết nối Supabase";

    public SettingsPageViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(
            () => LoadAsync(DisposeToken),
            () => !IsBusy);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        DiagnosticsCommand = new AsyncRelayCommand(
            DiagnosticsAsync,
            () => !IsBusy);
        CloudPreflightCommand = new AsyncRelayCommand(
            CloudPreflightAsync,
            () => !IsBusy && CloudEnabled);
        SyncCommand = new AsyncRelayCommand(
            SyncAsync,
            () => !IsBusy && CloudEnabled);
        CloudLoginCommand = new AsyncRelayCommand(
            CloudLoginAsync,
            () => !IsBusy
                && CloudEnabled
                && IsUserSessionMode
                && !string.IsNullOrWhiteSpace(CloudEmail)
                && !string.IsNullOrWhiteSpace(CloudPassword));
        CloudLogoutCommand = new AsyncRelayCommand(
            CloudLogoutAsync,
            () => !IsBusy && CloudAuthenticated);
        BrowseStorageCommand = new RelayCommand(BrowseStorage);
    }

    public IReadOnlyList<string> CloudAccessModesList { get; } =
        new[]
        {
            CloudAccessModes.UserSession,
            CloudAccessModes.TrustedServer
        };

    public string ServerPort { get => serverPort; set => Set(ref serverPort, value); }
    public bool UseHttps { get => useHttps; set => Set(ref useHttps, value); }
    public bool DiscoveryEnabled { get => discoveryEnabled; set => Set(ref discoveryEnabled, value); }
    public string DiscoveryPort { get => discoveryPort; set => Set(ref discoveryPort, value); }
    public string StorageRoot { get => storageRoot; set => Set(ref storageRoot, value); }
    public string ChunkSize { get => chunkSize; set => Set(ref chunkSize, value); }
    public string MaxUploads { get => maxUploads; set => Set(ref maxUploads, value); }

    public bool CloudEnabled
    {
        get => cloudEnabled;
        set
        {
            if (Set(ref cloudEnabled, value))
                RaiseCommands();
        }
    }

    public string SupabaseUrl { get => supabaseUrl; set => Set(ref supabaseUrl, value); }
    public string SupabasePublishableKey { get => supabasePublishableKey; set => Set(ref supabasePublishableKey, value); }
    public string OrganizationId { get => organizationId; set => Set(ref organizationId, value); }
    public string CloudEnvironment { get => cloudEnvironment; set => Set(ref cloudEnvironment, value); }

    public string CloudAccessMode
    {
        get => cloudAccessMode;
        set
        {
            if (Set(ref cloudAccessMode, value))
            {
                Raise(nameof(IsUserSessionMode));
                Raise(nameof(IsTrustedServerMode));
                Raise(nameof(CloudSessionStatus));
                RaiseCommands();
            }
        }
    }

    public bool IsUserSessionMode => string.Equals(
        CloudAccessMode,
        CloudAccessModes.UserSession,
        StringComparison.OrdinalIgnoreCase);

    public bool IsTrustedServerMode => !IsUserSessionMode;

    public bool CloudUseResumableUploads
    {
        get => cloudUseResumableUploads;
        set => Set(ref cloudUseResumableUploads, value);
    }

    public bool CloudSecretConfigured
    {
        get => cloudSecretConfigured;
        private set
        {
            if (Set(ref cloudSecretConfigured, value))
            {
                Raise(nameof(CloudSecretStatusText));
                Raise(nameof(CloudSessionStatus));
            }
        }
    }

    public string CloudSecretStatusText =>
        CloudSecretConfigured ? "Đã cấu hình" : "Chưa cấu hình";

    public bool CloudAuthenticated
    {
        get => cloudAuthenticated;
        private set
        {
            if (Set(ref cloudAuthenticated, value))
            {
                Raise(nameof(CloudSessionStatus));
                RaiseCommands();
            }
        }
    }

    public string CloudAuthenticatedEmail
    {
        get => cloudAuthenticatedEmail;
        private set
        {
            if (Set(ref cloudAuthenticatedEmail, value))
                Raise(nameof(CloudSessionStatus));
        }
    }

    public string CloudEmail
    {
        get => cloudEmail;
        set
        {
            if (Set(ref cloudEmail, value))
                RaiseCommands();
        }
    }

    public string CloudPassword
    {
        get => cloudPassword;
        set
        {
            if (Set(ref cloudPassword, value))
                RaiseCommands();
        }
    }

    public string CloudSessionStatus => IsTrustedServerMode
        ? CloudSecretConfigured
            ? "Máy chủ tin cậy đã có secret key"
            : "TrustedServer chưa có secret key"
        : CloudAuthenticated
            ? $"Đã đăng nhập: {CloudAuthenticatedEmail}"
            : "Chưa đăng nhập Supabase";

    public string CloudConfigurationStatus
    {
        get => cloudConfigurationStatus;
        private set => Set(ref cloudConfigurationStatus, value);
    }

    public string Diagnostics { get => diagnostics; private set => Set(ref diagnostics, value); }
    public string CloudPreflight { get => cloudPreflight; private set => Set(ref cloudPreflight, value); }

    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand CloudPreflightCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand CloudLoginCommand { get; }
    public ICommand CloudLogoutCommand { get; }
    public ICommand BrowseStorageCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync(
            "Đang tải cài đặt",
            "Cài đặt đã được tải",
            async token =>
            {
                var settings = ApiGuard.Require(
                    await api.GetSettingsAsync(token));
                ServerPort = settings.ServerPort.ToString();
                UseHttps = settings.UseHttps;
                DiscoveryEnabled = settings.DiscoveryEnabled;
                DiscoveryPort = settings.DiscoveryPort.ToString();
                StorageRoot = settings.StorageRootPath;
                ChunkSize = settings.ChunkSizeBytes.ToString();
                MaxUploads = settings.MaxConcurrentUploads.ToString();
                CloudEnabled = settings.CloudEnabled;
                SupabaseUrl = settings.SupabaseUrl ?? string.Empty;
                SupabasePublishableKey = settings.SupabasePublishableKey ?? string.Empty;
                OrganizationId = settings.OrganizationId ?? string.Empty;
                CloudEnvironment = settings.CloudEnvironment;
                CloudAccessMode = settings.CloudAccessMode;
                CloudUseResumableUploads = settings.CloudUseResumableUploads;
                CloudSecretConfigured = settings.CloudSecretConfigured;
                CloudConfigurationStatus = settings.CloudConfigurationStatus;
                CloudAuthenticated = settings.CloudAuthenticated;
                CloudAuthenticatedEmail = settings.CloudAuthenticatedEmail ?? string.Empty;
                if (string.IsNullOrWhiteSpace(CloudEmail))
                    CloudEmail = CloudAuthenticatedEmail;
                CurrentRowVersion = settings.RowVersion;

                if (CloudEnabled)
                    await RefreshCloudSessionStateAsync(token);
            });
    }

    private string CurrentRowVersion { get; set; } = "1";

    private Task SaveAsync() => RunAsync(
        "Đang lưu cài đặt",
        "Cài đặt đã được lưu; thay đổi cổng và thư mục sẽ áp dụng sau khi khởi động lại",
        async ct => _ = await SaveSettingsCoreAsync(ct));

    private async Task<SettingsDto> SaveSettingsCoreAsync(
        CancellationToken ct)
    {
        if (!int.TryParse(ServerPort, out var server)
            || !int.TryParse(DiscoveryPort, out var discovery)
            || !int.TryParse(ChunkSize, out var chunk)
            || !int.TryParse(MaxUploads, out var uploads))
        {
            throw new InvalidOperationException(
                "Cổng, chunk size và số upload phải là số hợp lệ.");
        }

        var request = new UpdateSettingsRequest(
            ServerPort: server,
            UseHttps: UseHttps,
            DiscoveryEnabled: DiscoveryEnabled,
            DiscoveryPort: discovery,
            StorageRootPath: StorageRoot,
            MinFreeBytes: 5L * 1024 * 1024 * 1024,
            ChunkSizeBytes: chunk,
            MaxConcurrentUploads: uploads,
            HeartbeatSeconds: 5,
            DisconnectAfterSeconds: 20,
            CloudEnabled: CloudEnabled,
            TemporaryHours: 24,
            LogsDays: 30,
            RowVersion: CurrentRowVersion,
            SupabaseUrl: NullIfWhiteSpace(SupabaseUrl),
            SupabasePublishableKey: NullIfWhiteSpace(SupabasePublishableKey),
            OrganizationId: NullIfWhiteSpace(OrganizationId),
            CloudEnvironment: CloudEnvironment.Trim(),
            CloudUseResumableUploads: CloudUseResumableUploads,
            CloudAccessMode: CloudAccessMode);

        var updated = ApiGuard.Require(
            await api.PutAsync<UpdateSettingsRequest, SettingsDto>(
                "api/v1/settings",
                request,
                ct));
        CurrentRowVersion = updated.RowVersion;
        CloudSecretConfigured = updated.CloudSecretConfigured;
        CloudConfigurationStatus = updated.CloudConfigurationStatus;
        CloudAuthenticated = updated.CloudAuthenticated;
        CloudAuthenticatedEmail = updated.CloudAuthenticatedEmail ?? string.Empty;
        return updated;
    }

    private Task DiagnosticsAsync() => RunAsync(
        "Đang chạy chẩn đoán",
        "Chẩn đoán hệ thống đã hoàn tất",
        async ct =>
        {
            var response = ApiGuard.Require(
                await api.GetAsync<object>("api/v1/system/diagnostics", ct));
            Diagnostics = response.ToString() ?? "Chẩn đoán hoàn tất";
        });

    private Task CloudPreflightAsync() => RunAsync(
        "Đang kiểm tra Supabase",
        "Kiểm tra Supabase đã hoàn tất",
        async ct =>
        {
            _ = await SaveSettingsCoreAsync(ct);
            var result = ApiGuard.Require(
                await api.GetAsync<CloudPreflightDto>(
                    "api/v1/cloud/preflight",
                    ct));
            CloudSecretConfigured = result.SecretConfigured;
            CloudAuthenticated = result.Authenticated;
            CloudAuthenticatedEmail = result.AuthenticatedEmail ?? string.Empty;
            CloudAccessMode = result.AccessMode;
            CloudConfigurationStatus = !result.Configured
                ? "Thiếu cấu hình"
                : result.CanSynchronize
                    ? result.Reachable
                        ? "Sẵn sàng đồng bộ"
                        : "Đã cấu hình nhưng chưa kết nối được"
                    : "Cần đăng nhập Supabase";

            var messages = new List<string>
            {
                $"Trạng thái: {CloudConfigurationStatus}",
                $"Chế độ truy cập: {result.AccessMode}",
                $"Phiên đăng nhập: {(result.Authenticated ? result.AuthenticatedEmail : "Chưa đăng nhập")}",
                $"Kiểu khóa: {result.KeyMode}",
                $"Chiến lược upload: {result.UploadStrategy}"
            };
            messages.AddRange(result.Errors.Select(x => "Lỗi: " + x));
            messages.AddRange(result.Warnings.Select(x => "Cảnh báo: " + x));
            CloudPreflight = string.Join(Environment.NewLine, messages);
        });

    private Task CloudLoginAsync() => RunAsync(
        "Đang đăng nhập Supabase",
        "Đăng nhập Supabase thành công",
        async ct =>
        {
            _ = await SaveSettingsCoreAsync(ct);
            var session = ApiGuard.Require(
                await api.PostAsync<LoginRequest, CloudSessionDto>(
                    "api/v1/cloud/auth/login",
                    new LoginRequest(CloudEmail.Trim(), CloudPassword),
                    ct));
            CloudPassword = string.Empty;
            ApplyCloudSession(session);
        });

    private Task CloudLogoutAsync() => RunAsync(
        "Đang đăng xuất Supabase",
        "Đã đăng xuất Supabase",
        async ct =>
        {
            _ = ApiGuard.Require(
                await api.PostAsync<object, object>(
                    "api/v1/cloud/auth/logout",
                    new { },
                    ct));
            ApplyCloudSession(new CloudSessionDto(
                false, null, null, null, OrganizationId, null));
        });

    private async Task RefreshCloudSessionStateAsync(CancellationToken ct)
    {
        var session = ApiGuard.Require(
            await api.GetAsync<CloudSessionDto>(
                "api/v1/cloud/auth/session",
                ct));
        ApplyCloudSession(session);
    }

    private void ApplyCloudSession(CloudSessionDto session)
    {
        CloudAuthenticated = session.Authenticated;
        CloudAuthenticatedEmail = session.Email ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(session.OrganizationId))
            OrganizationId = session.OrganizationId;
    }

    private Task SyncAsync() => RunAsync(
        "Đang yêu cầu đồng bộ cloud",
        "Các bản ghi đang chờ đã được đưa vào luồng đồng bộ",
        async ct =>
        {
            _ = ApiGuard.Require(
                await api.PostAsync<object, object>(
                    "api/v1/cloud/sync",
                    new { },
                    ct));
        });

    private void BrowseStorage()
    {
        var path = AppServices.Folders.PickFolder();
        if (path is not null)
            StorageRoot = path;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    protected override void RaiseCommands()
    {
        foreach (var command in new[]
                 {
                     RefreshCommand,
                     SaveCommand,
                     DiagnosticsCommand,
                     CloudPreflightCommand,
                     SyncCommand,
                     CloudLoginCommand,
                     CloudLogoutCommand
                 }.OfType<AsyncRelayCommand>())
        {
            command.RaiseCanExecuteChanged();
        }
    }
}

public sealed class StudentWaitingViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private ParticipantDto? participant;
    private SessionDetailDto? session;

    public StudentWaitingViewModel(IBackendClient api, StudentSessionState state)
    {
        this.api = api;
        this.state = state;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy && state.HasSession);
        LeaveCommand = new RelayCommand(Leave);
    }

    public ParticipantDto? Participant { get => participant; private set => Set(ref participant, value); }
    public SessionDetailDto? Session { get => session; private set => Set(ref session, value); }
    public string RoomCode => state.RoomCode;
    public ICommand RefreshCommand { get; }
    public ICommand LeaveCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        if (!state.HasSession)
        {
            Status = "Chưa có phiên tham gia. Hãy kết nối phòng trước.";
            StatusTone = "warning";
            return;
        }
        await RunAsync("Đang kiểm tra trạng thái duyệt", "Trạng thái phòng chờ đã được cập nhật", async token =>
        {
            if (state.AccessMode == SessionAccessMode.PublicCloud)
            {
                var publicStatus = await AppServices.PublicCloud.GetParticipantStatusAsync(state.ParticipantId!.Value, token);
                Participant = new ParticipantDto(state.ParticipantId.Value, state.SessionId!.Value,
                    state.StudentCode, state.DisplayName, Environment.MachineName + "-" + Environment.UserName,
                    Environment.MachineName, null, "1.0.0", publicStatus, DateTimeOffset.UtcNow,
                    DownloadStatus.NotStarted, SubmissionStatus.NotStarted, 0, null, ConnectionState.Online);
                return;
            }
            api.SetParticipantToken(state.AccessToken);
            Session = ApiGuard.Require(await api.GetSessionAsync(state.SessionId!.Value, token));
            Participant = ApiGuard.Require(await api.GetAsync<ParticipantDto>($"api/v1/sessions/{state.SessionId}/participants/{state.ParticipantId}", token));
            state.ExamId = Session.Summary.ExamId;
        });
    }

    private void Leave()
    {
        if (AppServices.Dialogs.Confirm("Rời phòng", "Rời phòng chờ và xóa thông tin phiên hiện tại?"))
        {
            AppServices.StudentRealtime.StopAsync().SafeFireAndForget("StudentRealtime.Leave");
            state.Reset();
            api.SetParticipantToken(null);
            Participant = null;
            Session = null;
            Status = "Đã rời phòng chờ";
            StatusTone = "info";
        }
    }

    protected override void RaiseCommands() => (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
}

public sealed class StudentDownloadViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private FileDescriptorDto? selectedFile;
    private string destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ExamTransfer", "Exam");
    private double progress;

    public StudentDownloadViewModel(IBackendClient api, StudentSessionState state)
    {
        this.api = api;
        this.state = state;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        BrowseCommand = new RelayCommand(Browse);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && SelectedFile is not null);
        DownloadAllCommand = new AsyncRelayCommand(DownloadAllAsync, () => !IsBusy && Files.Count > 0);
    }

    public ObservableCollection<FileDescriptorDto> Files { get; } = new();
    public FileDescriptorDto? SelectedFile { get => selectedFile; set { if (Set(ref selectedFile, value)) RaiseCommands(); } }
    public string Destination { get => destination; set => Set(ref destination, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public ICommand RefreshCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand DownloadAllCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        if (!state.SessionId.HasValue)
        {
            Status = "Hãy tham gia phòng trước khi nhận đề.";
            StatusTone = "warning";
            return;
        }
        await RunAsync("Đang tải manifest", "Manifest đề thi đã được cập nhật", async token =>
        {
            if (state.AccessMode == SessionAccessMode.PublicCloud)
            {
                if (!state.ExamId.HasValue) throw new InvalidOperationException("Phiên PublicCloud chưa có ExamId.");
                Files.ReplaceWith(await AppServices.PublicCloud.ListExamFilesAsync(state.ExamId.Value, token));
                SelectedFile = Files.FirstOrDefault();
                return;
            }
            api.SetParticipantToken(state.AccessToken);
            var session = ApiGuard.Require(await api.GetSessionAsync(state.SessionId.Value, token));
            state.ExamId = session.Summary.ExamId;
            var manifest = ApiGuard.Require(await api.GetAsync<ExamManifestDto>($"api/v1/exams/{session.Summary.ExamId}/manifest", token));
            Files.ReplaceWith(manifest.Files);
            SelectedFile = Files.FirstOrDefault();
        });
    }

    private void Browse()
    {
        var folder = AppServices.Folders.PickFolder();
        if (folder is not null) Destination = folder;
    }

    private Task DownloadAsync() => RunAsync("Đang tải file đề", "File đề đã được tải về", async ct =>
    {
        if (SelectedFile is null || !state.ExamId.HasValue) return;
        if (state.AccessMode == SessionAccessMode.PublicCloud)
        {
            var signed = await AppServices.PublicCloud.GetExamFileUrlAsync(state.SessionId!.Value, SelectedFile.Id, ct);
            await AppServices.PublicCloud.DownloadVerifiedAsync(signed, Path.Combine(Destination, SelectedFile.Name), ct);
            Progress = 100;
            return;
        }
        var reporter = new Progress<double>(x => Progress = x);
        await api.DownloadVerifiedFileAsync($"api/v1/exams/{state.ExamId}/files/{SelectedFile.Id}/content", Path.Combine(Destination, SelectedFile.Name), SelectedFile.Sha256, reporter, ct);
    });

    private Task DownloadAllAsync() => RunAsync("Đang tải toàn bộ đề", "Tất cả file đề đã được tải về", async ct =>
    {
        if (!state.ExamId.HasValue) return;
        Directory.CreateDirectory(Destination);
        var index = 0;
        foreach (var file in Files)
        {
            index++;
            if (state.AccessMode == SessionAccessMode.PublicCloud)
            {
                var signed = await AppServices.PublicCloud.GetExamFileUrlAsync(state.SessionId!.Value, file.Id, ct);
                await AppServices.PublicCloud.DownloadVerifiedAsync(signed, Path.Combine(Destination, file.Name), ct);
            }
            else
            {
                await api.DownloadVerifiedFileAsync($"api/v1/exams/{state.ExamId}/files/{file.Id}/content", Path.Combine(Destination, file.Name), file.Sha256, null, ct);
            }
            Progress = index * 100d / Files.Count;
        }
    });

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, DownloadCommand, DownloadAllCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed record WorkspaceFileRow(string Name, string Size, string Modified, string Status);

public sealed class StudentSubmissionViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private readonly AppAuthSessionState authState;
    private string? selectedPath;
    private double progress;
    private string fileName = "Chưa chọn file";
    private string fileType = "-";
    private string fileSize = "-";
    private string sha256 = "Chưa tính";
    private string validationStatus = "Chưa kiểm tra";
    private bool isFileValid;

    public StudentSubmissionViewModel(IBackendClient api, StudentSessionState state, AppAuthSessionState authState)
    {
        this.api = api;
        this.state = state;
        this.authState = authState;
        PickCommand = new AsyncRelayCommand(PickAsync, () => !IsBusy);
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, () => !IsBusy && IsFileValid && !string.IsNullOrWhiteSpace(SelectedPath) && state.HasSession);
    }

    public string? SelectedPath { get => selectedPath; private set { if (Set(ref selectedPath, value)) RaiseCommands(); } }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public string FileName { get => fileName; private set => Set(ref fileName, value); }
    public string FileType { get => fileType; private set => Set(ref fileType, value); }
    public string FileSize { get => fileSize; private set => Set(ref fileSize, value); }
    public string Sha256 { get => sha256; private set => Set(ref sha256, value); }
    public string ValidationStatus { get => validationStatus; private set => Set(ref validationStatus, value); }
    public string LimitText => "10 MB · 1 file · ZIP/RAR/7Z";
    public bool IsFileValid { get => isFileValid; private set { if (Set(ref isFileValid, value)) RaiseCommands(); } }
    public ICommand PickCommand { get; }
    public ICommand SubmitCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        Status = state.HasSession ? "Chọn một file nén ZIP, RAR hoặc 7Z để nộp" : "Hãy tham gia phòng trước khi nộp bài";
        StatusTone = state.HasSession ? "info" : "warning";
        if (state.HasSession)
        {
            var pending = (await Infrastructure.SubmissionQueueStore.LoadAsync(ct))
                .FirstOrDefault(x => x.SessionId == state.SessionId && x.ParticipantId == state.ParticipantId && File.Exists(x.FilePath));
            if (pending is not null)
            {
                SelectedPath = pending.FilePath;
                FileName = pending.FileName;
                FileType = Path.GetExtension(pending.FileName).TrimStart('.').ToUpperInvariant();
                FileSize = FormatBytes(pending.SizeBytes);
                Sha256 = pending.Sha256;
                ValidationStatus = QueueStatusText(pending.QueueStatus);
                IsFileValid = pending.QueueStatus != Infrastructure.SubmissionQueueStatus.FailedPermanent;
                Status = $"Có 1 bài đã lưu trên máy: {QueueStatusText(pending.QueueStatus)}";
                AppServices.SubmissionRecovery.Trigger();
            }
        }
    }

    private async Task PickAsync()
    {
        var path = AppServices.Files.PickFile("Bài làm đã nén|*.zip;*.rar;*.7z");
        if (string.IsNullOrWhiteSpace(path)) return;
        var info = new FileInfo(path);
        SelectedPath = info.FullName;
        FileName = info.Name;
        FileType = Path.GetExtension(info.Name).TrimStart('.').ToUpperInvariant();
        FileSize = info.Exists ? FormatBytes(info.Length) : "-";
        Sha256 = "Chưa tính";
        IsFileValid = false;
        if (!info.Exists)
        {
            IsFileValid = false;
            ValidationStatus = "File không tồn tại";
        }
        else if (!StudentSubmissionPolicy.IsAllowedExtension(info.Name))
        {
            IsFileValid = false;
            ValidationStatus = "Bài làm phải được nén thành một file .zip, .rar hoặc .7z trước khi nộp.";
        }
        else if (info.Length <= 0 || info.Length > StudentSubmissionPolicy.MaxBytes)
        {
            IsFileValid = false;
            ValidationStatus = "File bài làm vượt quá 10 MB. Hãy xóa dữ liệu không cần thiết hoặc giảm dung lượng rồi nén lại.";
        }
        else
        {
            ValidationStatus = "Đang tính SHA-256";
            Sha256 = await Infrastructure.SubmissionQueueStore.HashFileAsync(info.FullName, DisposeToken);
            IsFileValid = true;
            ValidationStatus = "Hợp lệ · sẵn sàng lưu an toàn";
        }
    }

    private Task SubmitAsync() => RunAsync("Đang sao chép bài vào vùng lưu an toàn", "Đã lưu trên máy; hệ thống sẽ tự gửi và chỉ báo thành công sau khi có biên nhận", async ct =>
    {
        if (SelectedPath is null || !state.SessionId.HasValue || !state.ParticipantId.HasValue || authState.CurrentAccount is null) return;
        var queued = await Infrastructure.SubmissionQueueStore.PrepareAsync(
            SelectedPath, api.BaseAddress.ToString(), authState.CurrentAccount.UserId, authState.CurrentAccount.StudentCode ?? state.StudentCode,
            state.SessionId.Value, state.ParticipantId.Value, state.RoomCode, state.AccessMode, state.ServerId, state.AccessToken, ct);
        SelectedPath = queued.FilePath;
        Sha256 = queued.Sha256;
        ValidationStatus = "Đã lưu trên máy";
        Progress = 10;
        AppServices.SubmissionRecovery.Trigger();
    });

    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:N2} MB";
    private static string QueueStatusText(Infrastructure.SubmissionQueueStatus status) => status switch
    {
        Infrastructure.SubmissionQueueStatus.Prepared => "Đã lưu trên máy",
        Infrastructure.SubmissionQueueStatus.WaitingForConnection => "Đang chờ kết nối",
        Infrastructure.SubmissionQueueStatus.Initializing or Infrastructure.SubmissionQueueStatus.Uploading => "Đang gửi tiếp",
        Infrastructure.SubmissionQueueStatus.Finalizing => "Đang xác nhận",
        Infrastructure.SubmissionQueueStatus.AwaitingReceipt => "Máy chủ đã nhận, đang chờ biên nhận",
        Infrastructure.SubmissionQueueStatus.Completed => "Đã có biên nhận",
        Infrastructure.SubmissionQueueStatus.NeedsLogin => "Cần đăng nhập lại",
        Infrastructure.SubmissionQueueStatus.NeedsRejoin => "Cần giáo viên duyệt lại",
        Infrastructure.SubmissionQueueStatus.Expired => "Đã quá thời hạn",
        _ => "File bị lỗi"
    };

    protected override void RaiseCommands()
    {
        (PickCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SubmitCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed class StudentReceiptViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private ReceiptDto? receipt;

    public StudentReceiptViewModel(IBackendClient api, StudentSessionState state)
    {
        this.api = api;
        this.state = state;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy && state.LastSubmissionId.HasValue);
        CopyCommand = new RelayCommand(Copy);
        SaveCommand = new RelayCommand(Save);
    }

    public ReceiptDto? Receipt { get => receipt; private set => Set(ref receipt, value); }
    public ICommand RefreshCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand SaveCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        if (state.LastReceipt is not null)
        {
            Receipt = state.LastReceipt;
            Status = "Biên nhận đã được xác minh";
            StatusTone = "success";
            return;
        }
        if (!state.LastSubmissionId.HasValue)
        {
            Status = "Chưa có bài nộp được máy chủ xác nhận";
            StatusTone = "warning";
            return;
        }
        await RunAsync("Đang tải biên nhận", "Biên nhận đã được xác minh", async token =>
        {
            api.SetParticipantToken(state.AccessToken);
            Receipt = ApiGuard.Require(await api.GetAsync<ReceiptDto>($"api/v1/submissions/{state.LastSubmissionId}/receipt", token));
            state.LastReceipt = Receipt;
        });
    }

    private void Copy()
    {
        if (Receipt is null) return;
        AppServices.Clipboard.SetText(Receipt.ReceiptCode);
        Status = "Mã biên nhận đã được sao chép";
        StatusTone = "success";
    }

    private void Save()
    {
        if (Receipt is null) return;
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        var json = System.Text.Json.JsonSerializer.Serialize(Receipt, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(folder, $"receipt-{Receipt.ReceiptCode}.json"), json);
        Status = "Biên nhận JSON đã được lưu";
        StatusTone = "success";
    }

    protected override void RaiseCommands() => (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
}

public sealed class StudentHistoryViewModel : ProductPageBase
{
    private readonly StudentSessionState state;

    public StudentHistoryViewModel(StudentSessionState state)
    {
        this.state = state;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
    }

    public ObservableCollection<StudentHistoryRow> History { get; } = new();
    public ICommand RefreshCommand { get; }

    protected override Task LoadAsync(CancellationToken ct) => RunAsync("Đang tải lịch sử trên máy", "Lịch sử cục bộ đã được cập nhật", token =>
    {
        History.Clear();
        if (state.LastReceipt is not null)
        {
            History.Add(new(state.RoomCode, state.LastReceipt.ReceiptCode, state.LastReceipt.ServerReceivedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), state.LastReceipt.IsLate ? "Nộp muộn" : "Đúng hạn"));
        }
        return Task.CompletedTask;
    });

    protected override void RaiseCommands() => (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
}

public sealed record StudentHistoryRow(string RoomCode, string ReceiptCode, string SubmittedAt, string Status);

public sealed class StudentSettingsViewModel : ProductPageBase
{
    private readonly ILocalPreferenceService preferences;
    private string displayName = string.Empty;
    private string studentCode = string.Empty;
    private string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ExamTransfer", "Working");
    private bool notifications = true;

    public StudentSettingsViewModel(ILocalPreferenceService preferences)
    {
        this.preferences = preferences;
        SaveCommand = new RelayCommand(Save);
        BrowseCommand = new RelayCommand(Browse);
        OpenLogCommand = new RelayCommand(OpenLog);
    }

    public string DisplayName { get => displayName; set => Set(ref displayName, value); }
    public string StudentCode { get => studentCode; set => Set(ref studentCode, value); }
    public string Workspace { get => workspace; set => Set(ref workspace, value); }
    public bool Notifications { get => notifications; set => Set(ref notifications, value); }
    public string DeviceId => Environment.MachineName + "-" + Environment.UserName;
    public ICommand SaveCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand OpenLogCommand { get; }

    protected override Task LoadAsync(CancellationToken ct)
    {
        DisplayName = preferences.Get("student-name") ?? string.Empty;
        StudentCode = preferences.Get("student-code") ?? string.Empty;
        Workspace = preferences.Get("workspace") ?? Workspace;
        Status = "Hồ sơ và cấu hình cục bộ đã được tải";
        StatusTone = "success";
        return Task.CompletedTask;
    }

    private void Save()
    {
        preferences.Set("student-name", DisplayName);
        preferences.Set("student-code", StudentCode);
        preferences.Set("workspace", Workspace);
        Status = "Hồ sơ học sinh đã được lưu";
        StatusTone = "success";
    }

    private void Browse()
    {
        var folder = AppServices.Folders.PickFolder();
        if (folder is not null) Workspace = folder;
    }

    private static void OpenLog()
    {
        Directory.CreateDirectory(FrontendLogger.LogDirectory);
        Process.Start(new ProcessStartInfo(FrontendLogger.LogDirectory) { UseShellExecute = true });
    }
}

internal static class CollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
