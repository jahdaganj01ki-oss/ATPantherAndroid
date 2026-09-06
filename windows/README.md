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
- ✅ WinForms UI with system-tray presence, live status/log, threshold + interval, log export.
- ✅ Credentials and settings stored DPAPI-protected (`DataProtectionScope.CurrentUser`), improving on the Android plaintext preferences.
- ✅ `dotnet build windows/ATPanther.sln` passes with 0 warnings / 0 errors on .NET SDK 8.

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
│   ├── Program.cs
│   ├── PantherApp.cs              # main window (phone/password/threshold/interval/log/buttons)
│   ├── MonitorController.cs       # 1:1 port of MonitorService.monitorLoop()
│   ├── TrayManager.cs             # system tray (show/resume/exit, live tooltip)
│   ├── Credentials.cs             # DPAPI-protected storage (SharedPreferences port)
│   ├── AppConfig.cs               # defaults + credential key names
│   ├── CryptoUtils.cs
│   └── app.manifest
├── App.config
├── ProjectMetadata.json
└── SourceMapping.cs           # machine-readable manifest Android → Windows mapping
```

## Ported Android constants (do not change without updating the Android side too)

| Android (AuthService.kt / AuthConfig) | Windows (AuthConfig.cs) |
| --- | --- |
| `U-621-Varnish` OAuth2 client ID | `ClientId` |
| Portal host, auth host | `Portal`, `AuthHost` |
| ForgeRock auth tree path | `AuthPath` |
| App User-Agent | `UserAgent` |
| JSON media type used for the credential step | `JsonMediaType` |
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
