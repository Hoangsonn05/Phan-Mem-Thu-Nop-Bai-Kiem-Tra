using System.IO;

var content = File.ReadAllText("ExamTransfer_Product_FullStack/frontend/src/ExamTransfer.Desktop/ViewModels/MainViewModel.cs");

content = content.Replace("if (Set(ref selected, accepted) && accepted is not null)\n                NavigateSafely(accepted);", "if (Set(ref selected, accepted) && accepted is not null)\n                NavigateSafelyAsync(accepted).SafeFireAndForget(\"MainViewModel.NavigateSafely\");");

content = content.Replace("if (Set(ref selected, accepted) && accepted is not null)\r\n                NavigateSafely(accepted);", "if (Set(ref selected, accepted) && accepted is not null)\r\n                NavigateSafelyAsync(accepted).SafeFireAndForget(\"MainViewModel.NavigateSafely\");");

content = content.Replace("if (first is not null) NavigateSafely(first);", "if (first is not null) NavigateSafelyAsync(first).SafeFireAndForget(\"MainViewModel.NavigateSafely\");");

content = content.Replace("if (Selected is { } item) NavigateSafely(item);", "if (Selected is { } item) NavigateSafelyAsync(item).SafeFireAndForget(\"MainViewModel.NavigateSafely\");");

var oldNav = @"    private void NavigateSafely(NavigationItem item)
    {
        if (isNavigating) return;

        if (!item.IsAvailable)
        {
            AppServices.Notifications.Publish(CreateUnavailableNotification(item));
            return;
        }

        if (item.Key is ""S-05"" or ""S-06""
            && !CanEnterExamDelivery(item.Key))
        {
            var waiting = Navigation.FirstOrDefault(x => x.Key == ""S-03"");
            if (waiting is not null)
            {
                item = waiting;
                Set(ref selected, waiting, nameof(Selected));
            }
        }

        object? nextPage = null;
        try
        {
            isNavigating = true;
            FrontendLogger.SetContext(Mode.ToString(), item.Key);
            nextPage = CreatePage(item);
            var previous = page;
            SetCurrentPageWithoutDisposing(nextPage);
            DisposePage(previous);

            if (nextPage is IAsyncInitializable initializable)
                initializable.InitializeAsync(CancellationToken.None).SafeFireAndForget($""{nextPage.GetType().Name}.InitializeAsync"");
        }
        catch (Exception ex)
        {
            DisposePage(nextPage);
            var traceId = FrontendLogger.Log(ex, $""MainViewModel.NavigateSafely:{item.Key}"");
            CurrentPage = CreateErrorPage(""Không th? m? màn hình này. ?ng d?ng v?n dang ch?y và l?i dã du?c ghi log."", traceId);
        }
        finally
        {
            isNavigating = false;
        }

        RaisePageProperties();
    }";

var newNav = @"    private async Task NavigateSafelyAsync(NavigationItem item)
    {
        if (isNavigating) return;

        if (!item.IsAvailable)
        {
            AppServices.Notifications.Publish(CreateUnavailableNotification(item));
            return;
        }

        if (item.Key is ""S-05"" or ""S-06""
            && !CanEnterExamDelivery(item.Key))
        {
            var waiting = Navigation.FirstOrDefault(x => x.Key == ""S-03"");
            if (waiting is not null)
            {
                item = waiting;
                Set(ref selected, waiting, nameof(Selected));
            }
        }

        if (item.Key == ""S-06"" && CurrentPage is StudentQuizViewModel)
        {
            return;
        }

        var generation = Interlocked.Increment(ref navigationGeneration);
        navigationCts?.Cancel();
        navigationCts?.Dispose();
        var cts = new CancellationTokenSource();
        navigationCts = cts;
        var token = cts.Token;

        object? nextPage = null;
        try
        {
            isNavigating = true;
            FrontendLogger.SetContext(Mode.ToString(), item.Key);
            nextPage = CreatePage(item);
            var previous = page;
            SetCurrentPageWithoutDisposing(nextPage);
            DisposePage(previous);
        }
        finally
        {
            isNavigating = false;
        }

        RaisePageProperties();

        if (nextPage is IAsyncInitializable initializable)
        {
            try
            {
                await initializable.InitializeAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (generation != navigationGeneration || !ReferenceEquals(CurrentPage, nextPage))
                    return;

                DisposePage(nextPage);
                var traceId = FrontendLogger.Log(ex, $""MainViewModel.NavigateSafely:{item.Key}"");
                CurrentPage = CreateErrorPage(""Không th? m? màn hình này. ?ng d?ng v?n dang ch?y và l?i dã du?c ghi log."", traceId);
                RaisePageProperties();
            }
        }
    }";

content = content.Replace(oldNav, newNav);
content = content.Replace(oldNav.Replace("    \n", "    \r\n"), newNav.Replace("    \n", "    \r\n"));
content = content.Replace(oldNav.Replace("\r\n", "\n"), newNav.Replace("\r\n", "\n"));
File.WriteAllText("ExamTransfer_Product_FullStack/frontend/src/ExamTransfer.Desktop/ViewModels/MainViewModel.cs", content);
