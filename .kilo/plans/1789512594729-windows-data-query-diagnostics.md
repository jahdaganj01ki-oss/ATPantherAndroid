# Plan: Windows data-query failure diagnostics + fixes

## Context
- Windows WPF build runs, login + contract-ID lookup succeed.
- `GetRemainingDataAsync` returns `null` repeatedly, monitor pauses after retries.
- **Confirmed behavior:** the app remains open showing the pause dialog. This means the monitor loop is alive and running, but data queries fail silently until the pause threshold is hit.
- The user's `at-panther.log` shows **no monitor-loop activity** at all: only startup events and "Monitor started". The `MonitorService` currently writes only to SQLite via `_db.Insert()`, not to the file logger, so we have zero visibility into why data queries fail.
- Log shows a `CryptographicException` on first start when loading DPAPI credentials (file from another machine/user); currently handled, but invalid file stays on disk.
- Android reference implementation works; suspect missing diagnostics and/or subtle auth/cookie/redirect handling differences.

## Goal
Make the Windows variant reliably fetch remaining data, and make failures diagnosable from `at-panther.log`.

## Decisions
1. **Add structured HTTP diagnostics in `AldiTalkApi`** instead of silent `catch { return null; }`.
   - Log for each API call: method, URL, status code, truncated response body (max 500 chars), exception type/message.
   - Log JSON parse failures and missing expected fields separately.
   - Keep returning `null` on failure to preserve monitor behavior.
2. **Add diagnostics in `AuthService`**:
   - Log each redirect hop: resolved URL, status code, Location header snippet.
   - Log when `iPlanetDirectoryPro` cookie is added to container.
   - Do NOT log full request/response bodies to avoid sensitive data.
3. **Add file logging to `MonitorService`** — **critical gap**:
   - Currently `MonitorService` only writes to SQLite via `_db.Insert()`. The file logger has zero visibility into the monitor loop.
   - Add `FileLogger.Info/Warning/Error` calls at: monitor start, each data query attempt (with contractId and result), each re-login attempt, pause trigger, and monitor stop.
   - This is necessary to trace the exact failure chain that leads to pause.
4. **Fix DPAPI credential loading**:
   - Catch `CryptographicException` specifically, delete the invalid `_file`, log warning, return empty defaults so the user re-saves.
5. **Preserve existing behavior**:
   - Do not change login flow, cookie handling, redirect logic, or monitor retry logic unless diagnostics show a concrete mismatch.
6. **Validation**:
   - Build succeeds locally (`dotnet build`).
   - Tests cannot run in this Codespace (missing .NET 8 runtime), so rely on GitHub Actions for test + publish.
   - Push changes, trigger Windows workflow, download artifact `AT-Panther-Windows-win-x64`.
   - User runs artifact on Windows, reproduces the failure, and shares the full `at-panther.log` content.

## Steps
1. Update `Windows/src/ATPanther/Core/AldiTalkApi.cs`:
   - Replace bare `catch { return null; }` with structured logging via `FileLogger`.
   - Before returning `null` on non-success status, log: method, URL, status code, truncated body (max 500 chars).
   - Log JSON parse failures, missing `subscribedOffers`, and missing `dataGrantAmount` pack entry.
   - Log successful response with remaining MB for correlation.
2. Update `Windows/src/ATPanther/Core/AuthService.cs`:
   - Add logging at each redirect hop: resolved URL, status code, Location header snippet.
   - Log when `iPlanetDirectoryPro` cookie is added to container.
   - Do NOT log full request/response bodies to avoid sensitive data exposure.
3. Update `Windows/src/ATPanther/Core/MonitorService.cs`:
   - Add `FileLogger.Info/Warning/Error` calls at key points: monitor start, each data query attempt (with contractId and result), each re-login attempt, pause trigger, monitor stop.
   - This fills the critical visibility gap in the monitor loop.
4. Update `Windows/src/ATPanther/Core/Stores.cs`:
   - In `CredentialStore.Load()`, catch `CryptographicException` specifically, delete `_file`, log warning, return empty credentials.
5. Commit, push, trigger GitHub Actions `windows.yml`, wait for success, download artifact `AT-Panther-Windows-win-x64`.
6. Provide artifact path and run ID to user for reproduction. Instruct user to reproduce the failure and share the full `at-panther.log` content.

## Risks
- Logging response bodies may capture sensitive data; truncate to 500 chars and do not log full passwords/tokens.
- Changing cookie/redirect behavior without evidence could break the working login flow; avoid unless diagnostics require it.
- `FileLogger` writes to `AppContext.BaseDirectory`; ensure directory is writable in portable EXE context (already addressed).
- Diagnostic logs may grow the file; consider log rotation if issue takes multiple runs to reproduce.
- `FileLogger` writes to `AppContext.BaseDirectory`; ensure directory is writable in portable EXE context (already addressed).

## Open questions
- None. The monitor loop is confirmed running; the issue is diagnosable with the planned file-log instrumentation.
