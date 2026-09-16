# Plan: Windows UI/UX improvements — tray, notifications, clear history

## Context
- The Windows WPF app works end-to-end now.
- Requested UX improvements:
  1. Add an in-app option to clear history.
  2. Minimize to tray with tooltip and context menu.
  3. Error notifications via Windows 10 toast/balloon.

## Current state verified
- `MainWindow.xaml` has two tabs: “Haupt” and “Verlauf”.
- “Verlauf” already has `BtnClear` (“Cache löschen”).
- No tray icon, no system-integrated notifications yet.
- `App.xaml.cs` has no Windows Forms references.
- `ATPanther.csproj` does not have `<UseWindowsForms>`.

## Decisions

### 1. Clear history
- Add a “Verlauf löschen” button on the “Haupt” tab, below the monitor controls.
- Reuse the existing `OnClearClicked` logic; keep the confirmation dialog.
- Keep the existing button in “Verlauf” as-is.

### 2. Minimize to tray
- Use `System.Windows.Forms.NotifyIcon` because WPF has no built-in tray control.
- Add `<UseWindowsForms>true</UseWindowsForms>` to `ATPanther.csproj`.
- Create `NotifyIcon` in `App.xaml.cs` so it survives window state changes.
- Behavior:
  - `Window.StateChanged == Minimized` → hide from taskbar (`ShowInTaskbar = false`) and show tray icon.
  - `Window.Closing` → cancel close, minimize to tray instead. The app only truly exits via tray context menu “Beenden”.
  - Tray tooltip: `AT Panther\n<status>` where status is the latest monitor status, or “Gestoppt” if not running.
  - Double-click tray icon → restore window and bring to front.
  - Right-click context menu:
    - “Öffnen” → restore window and bring to front.
    - “Beenden” → stop monitor, dispose tray icon, shutdown app.
  - On window close/exit: dispose `NotifyIcon`.

### 3. Error notifications
- Use `System.Windows.Forms.NotifyIcon` balloon tips.
  - Rationale: no extra NuGet packages, works reliably from a self-contained WPF/.NET 8 EXE.
  - If native Win10 toast is desired later, we can switch to `CommunityToolkit.WinUI.Notifications`.
- Trigger notifications for:
  - Login failure
  - Repeated data-query failures leading to pause
  - Booking failure
  - Unhandled exceptions
- Do **not** notify on every single retry to avoid spam.
- Use `BalloonTipTitle`, `BalloonTipText`, `BalloonTipIcon` with a reasonable timeout (e.g., 5 seconds).
- If the app is minimized to tray, the balloon is still shown by Windows.
- If multiple errors occur in quick succession, Windows may suppress duplicates; this is acceptable.

## Project changes
- `ATPanther.csproj`:
  - Add `<UseWindowsForms>true</UseWindowsForms>`.
- `App.xaml.cs`:
  - Add `using System.Windows.Forms;`.
  - Initialize `NotifyIcon` with icon, tooltip, context menu, and event handlers.
  - Add a public helper `UpdateTrayTooltip(string status)` that `MainWindow` can call.
  - Add a public helper `ShowTrayNotification(string title, string text)` for error notifications.
  - Handle `NotifyIcon` double-click to restore window.
  - Dispose `NotifyIcon` in `OnExit`.
- `MainWindow.xaml.cs`:
  - Wire `StateChanged` to minimize-to-tray logic.
  - Wire `Closing` to cancel close and minimize to tray instead.
  - Wire monitor status changes to update tray tooltip via `App.Current` cast or exposed helper.
  - Add “Verlauf löschen” button on “Haupt” tab pointing to `OnClearClicked`.
- `MainWindow.xaml`:
  - Add “Verlauf löschen” button in the “Haupt” tab, below the monitor controls.
- `MonitorService.cs`:
  - On error/pause/login failure, call `FileLogger.Error` and request a tray notification via `App.Current.ShowTrayNotification(...)`.
  - Do **not** call tray notification on every retry; only on failure-to-pause transition and booking failure.

## Out of scope / explicitly deferred
- Native Win10 toast via `CommunityToolkit.WinUI.Notifications`.
- Minimize-to-tray as user-toggleable setting (default on for now; can be made configurable later).
- Tray icon with dynamic data volume badge (tooltip text is sufficient for now).

## Validation
- Build: `dotnet build ATPanther.sln -c Release` succeeds.
- CI: push → GitHub Actions `windows.yml` → publish artifact.
- Manual test:
  - Minimize window → tray icon appears, taskbar entry disappears.
  - Hover tray icon → tooltip shows status.
  - Double-click tray icon → window restores.
  - Right-click tray icon → “Öffnen” / “Beenden” work.
  - Trigger a login failure or pause → balloon notification appears.
  - “Verlauf löschen” on “Haupt” tab clears history with confirmation.
  - Close button (X) minimizes to tray instead of closing; only “Beenden” from tray exits.

## Open questions
- None. Proceed with implementation.
