# Bugfix: Teacher Mode Crash

Date: 2026-07-13

## Reproduction

Command:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-frontend.ps1 -UseMock $true
```

Steps:

1. Opened the app.
2. Invoked the first mode button, Teacher, through Windows UI Automation.
3. Before the fix, `ExamTransfer.Desktop.exe` exited immediately with code `-532462766`.

## Root Exception

Source: Windows Event Viewer, Application log, `.NET Runtime` event 1026.

```text
Exception Info: System.InvalidOperationException: A TwoWay or OneWayToSource binding cannot work on the read-only property 'ConnectedPercent' of type 'ExamTransfer.Desktop.ViewModels.ActiveSessionCard'.
   at MS.Internal.Data.PropertyPathWorker.CheckReadOnly(Object item, Object info)
   at MS.Internal.Data.PropertyPathWorker.ReplaceItem(Int32 k, Object newO, Object parent)
   at MS.Internal.Data.PropertyPathWorker.UpdateSourceValueState(Int32 k, ICollectionView collectionView, Object newValue, Boolean isASubPropertyChange)
   at MS.Internal.Data.ClrBindingWorker.AttachDataItem()
   at System.Windows.Application.RunInternal(Window window)
   at ExamTransfer.Desktop.App.Main()
```

Application Error event 1000 also recorded `Exception code: 0xe0434352` for `ExamTransfer.Desktop.exe`.

## Cause

`DashboardView.xaml` bound `ProgressBar.Value` to computed, read-only record properties:

- `ActiveSession.ConnectedPercent`
- `ActiveSession.SubmittedPercent`

`ProgressBar.Value` defaults to a two-way binding. When the Dashboard view was materialized after switching to Teacher mode, WPF tried to attach a source-updating binding to those read-only properties, threw during layout/data binding, and the app had no global exception logging to capture it.

## Fix Summary

- Set Dashboard progress bindings to `Mode=OneWay`.
- Added global frontend logging in `App.xaml.cs` for:
  - `Application.DispatcherUnhandledException`
  - `AppDomain.CurrentDomain.UnhandledException`
  - `TaskScheduler.UnobservedTaskException`
- Added `FrontendLogger` writing to `%LocalAppData%\ExamTransfer\logs\frontend.log` with UTC timestamp, exception type, message, stack trace, inner exception, current mode, and current page key.
- Hardened `MainViewModel` navigation:
  - creates the next page before replacing `CurrentPage`;
  - disposes the old page only after the new page is assigned;
  - uses `CreatePage` and `NavigateSafely`;
  - blocks re-entry with `isBuildingNavigation` and `isNavigating`;
  - adds an `ErrorPageViewModel` / `ErrorPageView` fallback.
- Removed direct constructor fire-and-forget loads from:
  - `DashboardViewModel`
  - `ListPageViewModel`
- Added `IAsyncInitializable` and logged `SafeFireAndForget` for async page initialization.
- Updated `AsyncRelayCommand` to catch/log command exceptions.

## Files Changed

- `frontend/src/ExamTransfer.Desktop/App.xaml.cs`
- `frontend/src/ExamTransfer.Desktop/Core/FrontendLogger.cs`
- `frontend/src/ExamTransfer.Desktop/Core/RelayCommand.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/MainViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/DashboardViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/ListPageViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/ErrorPageViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/Views/DashboardView.xaml`
- `frontend/src/ExamTransfer.Desktop/Views/ErrorPageView.xaml`
- `frontend/src/ExamTransfer.Desktop/Views/ErrorPageView.xaml.cs`
- `frontend/src/ExamTransfer.Desktop/Views/MainWindow.xaml`

## Verification

Build:

```powershell
dotnet clean ExamTransfer.slnx
dotnet restore ExamTransfer.slnx
dotnet build ExamTransfer.slnx -c Debug
```

Result: passed. Remaining warnings:

- `NU1903` for backend `SQLitePCLRaw.lib.e_sqlite3` 2.1.11.
- `CS9113` unread backend parameter `realtime` in `ExamService.cs`.

Mock smoke:

- Open app.
- Click Teacher.
- Dashboard stays open.
- Select all 13 Teacher navigation items.
- Switch Student, select all 9 Student navigation items.
- Switch Teacher/Student 20 times.
- Toggle theme several times.
- Refresh Dashboard several times.
- Result: no crash, process remained responsive, no new `.NET Runtime` / `Application Error` events.

Offline backend smoke:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-frontend.ps1 -UseMock $false -ApiUrl "http://localhost:5048"
```

- Backend offline.
- Click Teacher.
- Dashboard stays open and uses fallback state.
- `%LocalAppData%\ExamTransfer\logs\frontend.log` records refused HTTP connection exceptions with `mode=Teacher` and `page_key=T-01`.

Real backend smoke:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-backend.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\run-frontend.ps1 -UseMock $false -ApiUrl "http://localhost:5048"
```

- Backend listened on `http://0.0.0.0:5048`.
- Click Teacher.
- App stayed open and responsive.

## Remaining Notes

- The backend emitted worker errors during the real-backend smoke:
  - `ExportWorker`: SQLite does not support `DateTimeOffset` expressions in `ORDER BY`.
  - `HeartbeatMonitorWorker`: query against `LastSeenUtc` could not be translated.
- Backend worker errors were not changed because this fix is scoped to the frontend Teacher-mode crash.
- Several existing UI strings in the frontend source were already mojibake. The touched view-model labels were normalized to ASCII Vietnamese to avoid further encoding churn.
