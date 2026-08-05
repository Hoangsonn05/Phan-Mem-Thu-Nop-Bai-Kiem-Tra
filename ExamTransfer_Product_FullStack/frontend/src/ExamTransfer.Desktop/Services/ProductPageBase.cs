using ExamTransfer.Desktop.Core;

namespace ExamTransfer.Desktop.Services;

public abstract class ProductPageBase : ObservableObject, IAsyncInitializable, IDisposable
{
    private readonly CancellationTokenSource disposeCts = new();
    private readonly Dictionary<string, Guid> pendingMutationRequestIds = new(StringComparer.Ordinal);
    private bool initialized;
    private bool disposed;
    private bool isBusy;
    private string status = "Sẵn sàng";
    private string statusTone = "info";

    public bool IsBusy
    {
        get => isBusy;
        protected set
        {
            if (Set(ref isBusy, value))
            {
                RaiseCommands();
            }
        }
    }

    public string Status
    {
        get => status;
        protected set => Set(ref status, value);
    }

    public string StatusTone
    {
        get => statusTone;
        protected set => Set(ref statusTone, value);
    }

    protected CancellationToken DisposeToken => disposeCts.Token;
    protected bool IsDisposed => disposed;

    protected Guid GetMutationRequestId(string operationKey)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
            throw new ArgumentException("Mutation operation key is required.", nameof(operationKey));
        if (!pendingMutationRequestIds.TryGetValue(operationKey, out var requestId))
        {
            requestId = Guid.NewGuid();
            pendingMutationRequestIds[operationKey] = requestId;
        }
        return requestId;
    }

    protected void CompleteMutationRequest(string operationKey) =>
        pendingMutationRequestIds.Remove(operationKey);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (initialized || disposed)
        {
            return;
        }

        initialized = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposeCts.Token);
        await LoadAsync(linked.Token);
    }

    protected abstract Task LoadAsync(CancellationToken cancellationToken);

    protected async Task RunAsync(string working, string success, Func<CancellationToken, Task> action)
        => await RunAsync(working, () => success, action);

    protected async Task RunAsync(
        string working,
        Func<string> success,
        Func<CancellationToken, Task> action)
    {
        if (disposed || IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            Status = working;
            StatusTone = "primary";
            await action(disposeCts.Token);
            Status = success();
            StatusTone = "success";
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (ExamTransfer.Desktop.ViewModels.BackendApiException ex)
        {
            ReportFailure(ex);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
        finally
        {
            if (!disposed)
            {
                IsBusy = false;
            }
        }
    }

    protected virtual void ReportFailure(Exception exception)
    {
        if (exception is ExamTransfer.Desktop.Infrastructure.PublicCloudApiException cloud)
        {
            var traceId = FrontendLogger.Log(cloud, GetType().Name);
            var http = cloud.StatusCode.HasValue
                ? $" / HTTP {(int)cloud.StatusCode.Value}"
                : string.Empty;
            Status = $"Không thể hoàn tất thao tác. (Mã lỗi: {cloud.Code}{http}; Mã tra cứu: {traceId})";
        }
        else if (exception is ExamTransfer.Desktop.ViewModels.BackendApiException backend)
        {
            var traceId = FrontendLogger.Log(backend, GetType().Name, backend.BackendTraceId);
            var http = backend.HttpStatusCode.HasValue ? $" / HTTP {backend.HttpStatusCode}" : string.Empty;
            Status = $"{backend.Message} (Mã lỗi: {backend.ApiCode}{http}; Mã tra cứu: {traceId})";
        }
        else
        {
            var traceId = FrontendLogger.Log(exception, GetType().Name);
            Status = $"Không thể hoàn tất thao tác. Mã tra cứu: {traceId}";
        }
        StatusTone = "danger";
    }

    protected virtual void RaiseCommands() { }

    public virtual void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        pendingMutationRequestIds.Clear();
        disposeCts.Cancel();
        disposeCts.Dispose();
    }
}
