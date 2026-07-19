using ExamTransfer.Desktop.Core;

namespace ExamTransfer.Desktop.Services;

public abstract class ProductPageBase : ObservableObject, IAsyncInitializable, IDisposable
{
    private readonly CancellationTokenSource disposeCts = new();
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
            Status = success;
            StatusTone = "success";
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var traceId = FrontendLogger.Log(ex, GetType().Name);
            Status = $"Không thể hoàn tất thao tác. Mã tra cứu: {traceId}";
            StatusTone = "danger";
        }
        finally
        {
            if (!disposed)
            {
                IsBusy = false;
            }
        }
    }

    protected virtual void RaiseCommands() { }

    public virtual void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposeCts.Cancel();
        disposeCts.Dispose();
    }
}
