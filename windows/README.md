# AT Panther – Windows Port

Native C# / .NET 8 Windows desktop port of **AT Panther**, the ALDI Talk data-volume monitor and automatic 1 GB top-up app whose Android source lives in this repository.

The port is not a redesign: it reproduces the Android app's API calls, authentication flow, headers, cookies, retry behavior, and pause rules 1:1.

## Status

- ✅ Auth flow ported from `app/src/main/java/com/alditalk/panther/auth/AuthService.kt`:
  ForgeRock proof-of-work step, credential submission, cookie handling, OAuth2 authorize with PKCE, and the manual redirect-chain resolution (`//host` → base-domain path) with the real constants from the Android source (`U-621-Varnish` client ID, portal/auth domains, User-Agent, JSON media type).
- ✅ Usage/top-up API ported from `app/src/main/java/com/alditalk/panther/api/AldiTalkApi.kt`:
  contract-ID resolution (customer-master-data BFF), remaining-data read (selfcare-dashboard offers BFF), and the 1 GB booking (`offer/updateUnlimited`), including BFF correlation/transaction headers.
- ✅ Monitor loop ported from `app/src/main/java/com/alditalk/panther/service/MonitorService.kt`:
  login → poll → book below threshold, with the same pause protection (3 consecutive connection failures or 5 re-logins without a successful poll stop the monitor until an explicit user start) and bounded 7-day log store.
  - **Initial-login retry** mirrors the Android alarm semantics: a failed initial login is retried once per interval until the 3-failure pause instead of dying after one attempt.
  - **Log file trimming**: the persistent history file is compacted every cycle to the 7-day window / 5000-row cap, exactly like the Room `deleteOlderThan` + `deleteBeyondLimit` calls.
  - **Keep-awake**: the system is kept from sleeping while the monitor runs (Windows `SetThreadExecutionState`, counterpart of the Android partial wake lock) and released on stop/pause.
  - **Session hygiene**: the HTTP client of a replaced session is disposed on re-login and on loop exit.
- ✅ WinForms UI with system-tray presence, live status/log (with remaining-volume like the Android status line), threshold + interval, log export, pause alert balloon, single-instance guard.
  The password field has an "Anzeigen" checkbox, and failed logins show the
  cause (Step-N/PoW/OAuth/HTTP detail) directly in the status line and log —
  the full trace stays in `diagnostics.log`.
- ✅ **Save button** (Android parity): stores phone/password/threshold/interval
  DPAPI-protected without starting the monitor (Start saves them too).
- ✅ **Login trace** (ForgeRock diagnosis): `diagnostics.log` records per attempt
  `Step1: HTTP …/cookies`, `Step2: filled inputs/json-size` and
  `Step2: HTTP …/callbacks/cookies` plus the full Step-2 body when no `tokenId`
  is returned — enough to tell wrong credentials apart from a changed portal
  flow or lost session cookies. Cookie *values* are never logged.
  A portal-side error key (`custom.alditalk.common.error$…`, e.g. the
  accountLock message) is surfaced in plain language in the status line and
  log, so a rejected login no longer looks like a connection error.
- ✅ Credentials and settings stored DPAPI-protected (`DataProtectionScope.CurrentUser`), improving on the Android plaintext preferences.
- ✅ `dotnet build windows/ATPanther.sln` passes with 0 warnings / 0 errors on .NET SDK 8.
- ✅ Paritätsstand: **Ulefone Power Armor X11Pro v1.2** (Dreh-/Freeze-Optimierung),
  Verlauf **neueste zuerst** (`ORDER BY timestamp DESC, id DESC`, UI-Limit 200),
  ALDI-Talk-App-Icon sowie die Diagnose-Trace-Verbesserungen
  (Callback-Inventar, PoW-Nonce, vollständige Step-2-Antwort im Trace).
  Android-exklusive v1.2-Punkte (RecyclerView-Animator, `configChanges`,
  DB-Index, PoW-Dispatcher) haben kein Windows-Gegenstück und wurden
  sinngemäß portiert: **Trim gedrosselt** (Alter ~stündlich, Limit nur alle
  ~10 min bei Bedarf), **Export im Hintergrund-Thread**, PoW läuft auf dem
  Monitor-Task (kein UI-Block).

> ⚠️ **Not yet validated against the live portal.** The login/BFF sequence is a faithful code-level port of the Android source, but it has not been exercised end-to-end against a real ALDI Talk account from this environment. Validate parity on a test account before relying on it (see "Validation" below).

## Repository layout

```
windows/
├── ATPanther.sln
├── ATPanther.Core/            # platform-neutral port of the Android logic
│   ├── Auth/
│   │   ├── AuthConfig.cs          # real constants (client ID, UA, domains, media type)
│   │   ├── PoWSolver.cs           # SHA-1 proof-of-work solver
│   │   ├── PkcePair.cs            # PKCE verifier/challenge (RFC 7636)
│   │   ├── AldiTalkAuthenticator.cs  # full login + redirect chain
│   │   └── LoginResult.cs
│   └── Api/
│       └── AldiTalkApi.cs         # contract-ID, remaining data, 1 GB booking
├── ATPanther.Windows/         # .NET 8 WinForms app
│   ├── Program.cs                 # entry point + single-instance guard
│   ├── PantherApp.cs              # main window (phone/password/threshold/interval/log/buttons)
│   ├── MonitorController.cs       # 1:1 port of MonitorService.monitorLoop()
│   ├── TrayManager.cs             # system tray (show/resume/exit, live tooltip, pause balloon)
│   ├── Credentials.cs             # DPAPI-protected storage (SharedPreferences port)
│   ├── AppConfig.cs               # defaults + credential key names
│   ├── DiagLog.cs                 # file diagnostics log (diagnostics.log + rotation)
│   ├── AppIcon.cs                 # window/tray icon loader (exe icon + fallback)
│   ├── SystemSleep.cs             # keep-awake while monitoring (wake-lock counterpart)
│   ├── CryptoUtils.cs
│   ├── assets/app.ico             # embedded app icon (from assets/app-icon-source.png)
│   └── app.manifest               # explicit amd64 identity (side-by-side hardening)
├── Diagnose-AT-Panther.bat  # log + sysinfo + sxstrace collector (loader failures)
├── App.config
├── ProjectMetadata.json
└── SourceMapping.cs           # machine-readable manifest Android → Windows mapping
```

## Diagnostics & app icon

- **Diagnose-Log:** `%LocalAppData%\ATPanther\diagnostics.log` (rotation 1 MiB × 5)
  records startup, crashes (UI/AppDomain/task handlers in `Program.cs`), all
  UI catch-all branches and the monitor trace (start/stop/pause/loop errors).
  Open via tray menu ("Diagnose-Log öffnen" / "Diagnose-Ordner öffnen").
- **Loader failures** ("side-by-side configuration is invalid") happen in Windows
  before any managed code runs: run `windows/Diagnose-AT-Panther.bat` as
  administrator (sxstrace flow, also bundled in the release ZIP) and attach
  `sysinfo.txt` + `diagnostics.log` (+ `sxstrace.txt`).
- **CI:** `.github/workflows/windows.yml` baut diesen Ordner
  (`windows/ATPanther.sln`) per `dotnet publish win-x64`
  und lädt das ZIP `ATPanther-win-x64.zip` (Artifact `ATPanther-win-x64`) hoch.
- **Icon:** `ATPanther.Windows/assets/app.ico` (7 sizes, PNG-compressed) is
  embedded as `ApplicationIcon` and also feeds the window + tray icon at
  runtime (`AppIcon.cs`, system-icon fallback). Source:
  `assets/app-icon-source.png`. Never mix exe files from different builds in
  one folder.

## Ported Android constants (do not change without updating the Android side too)

| Android (AuthService.kt / AuthConfig) | Windows (AuthConfig.cs) |
| --- | --- |
| `U-621-Varnish` OAuth2 client ID | `ClientId` |
| Portal host, auth host | `Portal`, `Auth` |
| ForgeRock auth tree URL (`.../signin/json/realms/alditalk/authenticate?...`) | `AuthEndpoint` |
| Redirect URI (`Portal` + `/logged-in-home-page/`) | `RedirectUri` |
| App User-Agent | `UserAgent` |
| JSON media type used for the credential step | `JsonMediaType` |
| Cookie name/domain/path | `CookieName`/`CookieDomain`/`CookiePath` |
| Max redirect hops (8) / PoW nonce cap (10M) | `MaxRedirectHops` / `MaxPowNonce` |
| SHA-1 PoW `sha1(work + nonce)` | `PoWSolver` |

## Architecture (Android → Windows mapping)

1. **Auth layer** (`AldiTalkAuthenticator`)
   - Request login page, parse the PoW parameters from the returned page, solve the PoW.
   - POST the empty-bodied first step, then submit username/password to the ForgeRock callback endpoint.
   - Keep the session cookie; run OAuth2 `authorize` with PKCE and manually resolve the redirect chain until the final BFF-session is established.
   - On failure, the monitor counts it like the Android app and pauses after the configured cap.

2. **Monitoring layer** (`MonitorController` runs `MonitorLoopAsync` on a background task)
   - Initial login + contract-ID resolution (`performLogin` equivalent).
   - Periodic `GetRemainingDataAsync` poll; below threshold → `Book1GbAsync`.
   - Failure counters and pause state persisted to `%LocalAppData%\ATPanther\monitor_state.json` (SharedPreferences port) so a pause survives restarts.

3. **UI / storage**
   - Credentials + threshold/interval saved DPAPI-encrypted under `%LocalAppData%\ATPanther\credentials\`.
   - Log store bounded to 5000 rows / 7-day window, mirroring the X11Pro freeze fix.
   - Log export writes `Documents\ATPanther\atpanther_log_*.txt`.

## Defaults (inherited from the Android app)

- Threshold: **850 MB**
- Check interval: **60 s**
- Top-up size: **1 GB**
- Pause rule: after repeated consecutive failures the monitor stops; the first Start tap only lifts the pause, the second tap starts the monitor.

## Build and run

Requires the .NET 8 SDK (Windows machine; `net8.0-windows10.0.19041.0` needs the Windows targeting pack — `EnableWindowsTargeting` is set, so a Linux build server can compile it too).

```bash
dotnet build windows/ATPanther.sln
dotnet run --project windows/ATPanther.Windows
```

Release-style single-file publish (run on Windows):

```bash
dotnet publish windows/ATPanther.Windows -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Validation checklist (parity against Android)

1. Run the Windows app and the Android app against the same (test) account.
2. Compare each outgoing request (URL, method, headers, body) with the Android HAR baseline.
3. Confirm a top-up completes and the balance/offer state updates identically.
4. Verify pause behavior: kill the network 3+ times in a row → monitor pauses; first Start lifts the pause only.
