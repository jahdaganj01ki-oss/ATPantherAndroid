# AT Panther – Windows-Version (portables EXE mit Tray-Icon)

1:1-Port der Android-App **AT Panther** (ALDI-Talk-Datenvolumen-Monitor) als
nativer Windows-Desktop-Tray-Dienst. Der Monitor prüft in einem einstellbaren
Intervall das verbleibende Datenvolumen im ALDI-Talk-Kundenportal und bucht
automatisch **1 GB** nach, sobald der Restbestand unter die eingestellte
Schwelle (Standard: 850 MB) fällt.

```
windows/
├── AT.Panther.sln                  # Visual-Studio-Lösung
├── README.md                       # diese Datei
├── AT.Panther/
│   ├── AT.Panther.csproj           # .NET 8 WinForms, self-contained Single-File
│   ├── Program.cs                  # Einstieg, Single-Instance-Mutex
│   ├── AppPaths.cs                 # Datenpfade (%AppData%\ATPanther)
│   ├── AppSettings.cs              # Einstellungen (JSON)
│   ├── SecureStore.cs              # Zugangsdaten via Windows-DPAPI
│   ├── LogStore.cs                 # Verlauf (JSON, 7-Tage-Retention)
│   ├── AldiTalk/                   # Login + BFF-API (Port AuthService.kt/AldiTalkApi.kt)
│   │   ├── AuthConfig.cs
│   │   ├── AuthService.cs          # ForgeRock-Login: PoW → PKCE → Redirect-Kette
│   │   ├── AldiTalkApi.cs          # contractId, Restvolumen, 1-GB-Buchung
│   │   ├── Pkce.cs
│   │   └── Models.cs
│   ├── Monitor/
│   │   └── MonitorEngine.cs        # Monitor-Loop (Port MonitorService.kt)
│   ├── UI/
│   │   └── MainForm.cs             # Fenster (Black-Theme) + Tray-Icon
│   ├── assets/app.ico              # App-Icon (Generator: tools/generate-icon.mjs)
│   └── Properties/PublishProfiles/ # win-x64-portable.pubxml
└── tools/
    └── generate-icon.mjs           # Icon-Generator (bun tools/generate-icon.mjs)
```

## Build

### Lokal (Windows, .NET 8 SDK)

```bash
dotnet publish AT.Panther/AT.Panther.csproj -p:PublishProfile=win-x64-portable
```

oder direkt:

```bash
dotnet publish windows/AT.Panther/AT.Panther.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o windows/out
```

Die fertige **ATPanther.exe** (eine einzige Datei, enthält die .NET-Runtime)
liegt danach in `windows/out/` bzw. `AT.Panther/bin/Release/net8.0-windows/win-x64/publish/`.
Sie läuft ohne Installation auf Windows 10/11 (x64) – einfach starten.

### Automatisch (GitHub Actions)

Der Workflow [`.github/workflows/windows.yml`](../.github/workflows/windows.yml)
baut bei jedem Push/PR auf einem `windows-latest`-Runner die portable EXE und
lädt sie als Artifact **ATPanther-Windows-portable** hoch
(Actions-Tab → Workflow → Artifact).

> Hinweis: `PublishSingleFile` + `EnableCompressionInSingleFile` extrahiert die
> Runtime beim ersten Start kurz nach `%TEMP%`. Sollte das in seltenen
> Umgebungen Probleme machen, einfach `-p:EnableCompressionInSingleFile=false`
> setzen (Datei wird dann größer, startet aber ohne Entpacken).

## Bedienung

- **Login-Daten**: ALDI-Talk-Zugangsdaten eingeben und „Speichern“ – das Passwort
  wird per Windows-DPAPI nur für den aktuellen Windows-Benutzer verschlüsselt
  (`%AppData%\ATPanther\credentials.dat`).
- **Einstellungen**: Schwelle in MB (Standard 850) und Prüfintervall in Sekunden
  (Standard 60, Minimum 10). „Mit Windows starten“ legt einen Autostart-Eintrag
  unter `HKCU\...\CurrentVersion\Run` an (ohne Admin-Rechte); „Monitor beim
  Programmstart automatisch laufen lassen“ startet die Überwachung direkt mit.
- **Monitor**: Start/Stopp über das Fenster oder das Tray-Menü. Der Status
  (Restvolumen, Buchungen, Fehler) erscheint im Fenster und als Tray-Tooltip.
- **Wartung**: „Cache leeren“ entfernt lokale Temp-Daten, „Log exportieren“
  speichert den Verlauf als Textdatei (gleiches Format wie die Android-Version).
- **Tray**: Schließen des Fensters beendet die App nicht – sie bleibt im
  Tray-Bereich aktiv. Doppelklick öffnet das Fenster, „Beenden“ im Tray-Menü
  beendet auch den Monitor.

Alle Daten liegen unter `%AppData%\ATPanther\` (Einstellungen, verschlüsselte
Zugangsdaten, Log) – die EXE selbst ist damit vollständig portabel.

## Feature-Mapping Android → Windows

| Android | Windows |
|---|---|
| EncryptedSharedPreferences | DPAPI (`SecureStore.cs`) |
| Room-DB `log_entries` (7-Tage-Retention) | JSON-Log `log.json` (`LogStore.cs`) |
| Foreground-Service + WakeLock | Hintergrund-Task in `MonitorEngine.cs` (immer aktiv, solange die App läuft) |
| `BOOT_COMPLETED`-Receiver | Autostart via HKCU-Run-Key + „Monitor automatisch starten“ |
| Notification + Status-Broadcast | Tray-Icon mit Tooltip/Balloon + Status-Label |
| SAF-Log-Export | SaveFileDialog (`MainForm.ExportLog`) |
| Black-Theme (colors.xml) | WinForms-Dark-Theme (`Theme`-Klasse) |

**Wichtig:** Wie die Android-Version meldet sich die Windows-Version bei
abgelaufener Session automatisch neu an (max. 5 aufeinanderfolgende Fehlversuche,
danach stoppt der Monitor). Login-Mechanik (ForgeRock PoW, PKCE, Redirect-Kette)
und BFF-Endpunkte sind 1:1 übernommen – ein Login via Kundenportal ist
Voraussetzung und das automatische Nachbuchen sollte nur im Rahmen der
ALDI-Talk-Nutzungsbedingungen verwendet werden.

## Sicherheitshinweis

Die Zugangsdaten werden ausschließlich DPAPI-verschlüsselt (CurrentUser)
abgelegt. Die Log-Datei enthält keine Passwörter. Netzwerkverkehr läuft
ausschließlich über HTTPS zu den offiziellen ALDI-Talk-Domains
(`alditalk-kundenportal.de`, `login.alditalk-kundenbetreuung.de`).
