# AT Panther – Windows (WPF, .NET 8)

Windows-Port der Ulefone-Variante (`Ulefone Power Armor X11Pro/`):
Login (ForgeRock-PoW → PKCE → Redirect-Kette), Abfrage und
automatisches 1-GB-Nachbuchen funktionieren 1:1 wie in der App.

Details: `ANALYSE-UND-SPEZIFIKATION.md`.

## Build via GitHub Actions (empfohlen)

Workflow `.github/workflows/windows.yml` läuft bei jedem Push auf
`main`/`master`, der `Windows/` berührt, sowie manuell
(`workflow_dispatch`) auf `windows-latest`:

- `setup-dotnet 8.0.x` → `restore` → `build Release` → `test`
- `publish win-x64` self-contained Single-File
- Artifact **`AT-Panther-Windows-win-x64`** (lauffähige `.exe`, ohne installierte Runtime)

Die fertige EXE liegt unter **Actions → jeweiligen Run → Artifacts**.
Unsigniert → beim ersten Start kommt ggf. eine SmartScreen-Warnung
(„Trotzdem ausführen“).

## Lokal bauen

.NET 8 SDK installieren, dann:

```powershell
cd Windows
dotnet restore ATPanther.sln
dotnet build ATPanther.sln -c Release
dotnet test ATPanther.sln -c Release
dotnet publish src/ATPanther/ATPanther.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64
```

## Verhalten

- Standard-Schwelle **850 MB**, Standard-Intervall **60 s**
- Bei Rest < Schwelle: automatisch +1 GB; bei 0,0 MB (< 0,05 MB) nach
  erfolgreicher Erstbuchung + 3 s + frischer Abfrage ein zweites Mal
- Nach 3 Verbindungsfehlern oder 5 Re-Logins ohne Abfrage: ⛔ Pause,
  Fortsetzung nur manuell (2× Start: 1. hebt Pause auf ohne Login,
  2. startet)
- **Monitor-Freigabe**: Die Windows-Variante ist der Standard-Owner, aber nur
  solange sie die Lease im Cloudflare Worker hält (siehe
  [`../worker/README.md`](../worker/README.md)). Läuft dieselbe App auf
  Ulefone/Moto, darf immer nur **eine** Variante das Portal abfragen; die
  anderen starten nicht („⏸ Bereitschaft"). Umschalten über
  *Monitor-Freigabe → Übernehmen*. Ohne eingetragene Worker-URL startet der
  Monitor bewusst nicht (fail-closed).
- Daten: `%AppData%/ATPanther/` (DPAPI-Credentials, State-JSON, SQLite-Log,
  `lock_settings.json` + `lock_cache.json` für die Freigabe – enthalten nur
  URL/Variantenname, keine Zugangsdaten)
- Autostart: Checkbox schreibt `HKCU\...\Run\ATPanther`
