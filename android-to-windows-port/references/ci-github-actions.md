# Build ausschließlich über GitHub Actions

Harte Regel: **kein Build auf dem Entwickler-Rechner.** Kein `dotnet build`, kein `dotnet publish`,
kein `dotnet run`, kein `msbuild`, kein lokales SDK. Der einzige Nachweis, dass etwas kompiliert, ist
ein grüner Actions-Run mit Artefakt.

## Warum das tragfähig ist

Der Runner stellt das SDK. Ein fehlendes lokales `dotnet` ist damit kein Hindernis, sondern
gewollt. Wer lokal baut, erhält eine Binärdatei, die niemand reproduzieren kann — und verstößt gegen
die Regel.

## Workflow-Vorlage

`.github/workflows/windows.yml` — an Repo-Konventionen anpassen (Triggers, Artefaktname, Retention):

```yaml
name: Windows Build

# Baut die native Windows-Version. Bewusst KEIN lokaler Build:
# das .NET 8 SDK stellt der Runner.

on:
  push:
    branches: [ main, master ]
    paths:
      - "ATPanther Windows/**"
      - ".github/workflows/windows.yml"
  pull_request:
    branches: [ main, master ]
    paths:
      - "ATPanther Windows/**"
      - ".github/workflows/windows.yml"
  workflow_dispatch:

jobs:
  build:
    runs-on: windows-latest
    defaults:
      run:
        working-directory: "ATPanther Windows"

    steps:
      - name: Checkout repository
        uses: actions/checkout@v4

      - name: Set up .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Publish portable win-x64
        run: >
          dotnet publish -c Release -r win-x64 --self-contained true
          -p:PublishSingleFile=true
          -p:IncludeNativeLibrariesForSelfExtract=true
          -o publish

      - name: Upload Windows build
        uses: actions/upload-artifact@v4
        with:
          name: AT-Panther-Windows-portable
          path: "ATPanther Windows/publish/**"
          retention-days: 14
```

WinForms braucht den Windows Desktop SDK-Teil — deshalb `windows-latest`, nicht Ubuntu.

## Ablauf pro Build-Zyklus

1. Änderungen committen (klein, thematisch geschlossen).
2. Push auf den Arbeits-Branch.
3. Run beobachten:
   ```powershell
   gh run list --workflow "Windows Build" --limit 5
   gh run watch <run-id> --exit-status
   gh run view <run-id> --log-failed
   ```
4. Artefakt holen und prüfen:
   ```powershell
   gh run download <run-id> -n AT-Panther-Windows-portable -D <zielordner>
   ```
5. Fehlschlag: Log lesen, **Code** korrigieren, erneut pushen. Nicht lokal bauen wollen.

## Secrets

Für einen lauffähigen Debug-/Portable-Build sind keine Secrets nötig. Falls Signierung gebraucht
wird, als Repository Secrets hinterlegen und im Workflow referenzieren — niemals Werte ins Repo, in
Logs oder in die Chat-Ausgabe.

## Token-Hinweis (ATPanther-Umfeld)

Der `gh`-CLI-eigene Token sieht dieses private Repo unter Umständen nicht. Dann läuft `push` und
`gh`-API über den aus dem Credential Manager geholten Token als `GH_TOKEN`. Vorgehen:

```powershell
$gcm = 'C:\Program Files\Git\mingw64\bin\git-credential-manager.exe'
$fin = [IO.Path]::GetTempFileName(); $fout = [IO.Path]::GetTempFileName()
[IO.File]::WriteAllText($fin, "protocol=https`nhost=github.com`n")
Start-Process $gcm -ArgumentList 'get' -RedirectStandardInput $fin `
  -RedirectStandardOutput $fout -NoNewWindow -Wait
$tok = (Get-Content $fout | Where-Object { $_ -like 'password=*' }).Substring(9)
[IO.File]::WriteAllText($fout, '')   # Ausgabedatei mit Passwort sofort leeren
Remove-Item $fin, $fout
$env:GH_TOKEN = $tok
```

Das Pipe-Verfahren (`"..." | git-credential-manager.exe get`) funktioniert auf diesem System
**nicht** — `Start-Process` mit umgeleiteter Input-Datei ist der verlässliche Weg. Token-Wert nie
ausgeben.
