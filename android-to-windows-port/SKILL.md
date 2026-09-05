---
name: android-to-windows-port
description: Portiert eine Android-App funktionsgleich in ein natives Windows-Programm (.NET 8 WinForms). Immer dann nutzen, wenn ein Android-Repo (GitHub plus lokaler Klon) nach Windows überführt werden soll: zuerst Repo abgleichen, dann den API-Vertrag byte-genau aus dem Quellcode übernehmen, dann portieren. Builds laufen ausnahmslos über GitHub Actions, niemals lokal.
---

# Android → Windows Port (funktionsgleich)

Ziel ist ein natives Windows-Programm, das sich **nachweislich identisch** verhält wie die
Android-App: dieselben Endpunkte, Header, Body-Formate, Cookie- und Redirect-Logik,
Fehlerbehandlung, Zustände und Hintergrundverhalten.

## Grundregeln (nicht verhandelbar)

1. **Erst abgleichen, dann arbeiten.** Vor dem ersten Blick in den Port-Code wird das Repo mit
   GitHub abgeglichen. Ohne erfolgreichen Abgleich keine Portierung.
2. **Nichts erfinden.** Jeder Endpunkt, Header, Query-Parameter und jedes JSON-Feld wird **aus dem
   Quellcode gelesen und übernommen**, nicht aus Vermutung, Doku oder "üblicher Praxis" rekonstruiert.
   Ein erfundener Header ist ein Fehler, auch wenn die App zufällig trotzdem funktioniert.
3. **Kein lokaler Build.** `dotnet build`, `dotnet publish`, `dotnet run`, `gradlew assemble*` oder
   ein lokales SDK sind verboten und unnötig. Bauen heißt: committen, pushen, GitHub Actions laufen
   lassen, Artefakt prüfen. Fehlendes lokales .NET-SDK ist der **erwartete** Zustand, kein Problem.
4. **Projektdateien von Hand schreiben.** `csproj`, `sln`, `Program.cs` etc. werden als Dateien
   angelegt, nicht per `dotnet new` erzeugt (setzt ein lokales SDK voraus).
5. **Jede Abweichung ist eine dokumentierte Abweichung.** Alles, was nicht 1:1 übernehmbar ist,
   kommt mit Grund und Auswirkung in `PARITY.md`. Stillschweigende Anpassungen gibt es nicht.
6. **Keine Secrets im Repo.** Zugangsnachweise, Tokens und Passwörter weder einchecken noch ausgeben.
   Zielplattform-Secrets leben in GitHub Actions Secrets.

## Arbeitsplan

### Phase 0 — Abgleich

```powershell
git -C <repo> fetch origin --prune
git -C <repo> status -sb
git -C <repo> pull --ff-only
git -C <repo> rev-parse HEAD   # als BASE-COMMIT notieren
```

Bei unsauberem Arbeitsverzeichnis oder divergierenden Branches: **stoppen**, Zustand melden, nicht
einfach `--force`, `reset` oder `stash` verwenden. Ergebnis: bestätigter `BASE-COMMIT`.

### Phase 1 — Bestandsaufnahme

Alle Quellen des App-Moduls erfassen und eine Inventartabelle schreiben:

- `AndroidManifest.xml`: Services, Receiver, Berechtigungen, `launchMode`, `configChanges`,
  Notification-Kanäle, App-Name, `applicationId`, Version.
- `build.gradle(.kts)`: Abhängigkeiten (Netzwerk, DB, Serialisierung), `minSdk`/`targetSdk`,
  Build-Typen, Signierung.
- Quellcode nach Schichten sortieren: Netzwerk/Auth, Datenhaltung, Hintergrund/Ablauf, UI.

Netzwerkstellen zu übersehen ist der teuerste Fehler dieses Schritts. Suche aktiv nach
`Request.Builder`, `.header(`, `HttpUrl`, `URL(`, `OkHttpClient`, `Retrofit`, `@GET`, `@POST`,
`addQueryParameter`, `MultipartBody`, `RequestBody`.

### Phase 2 — API-Vertrag extrahieren

Für **jeden** einzelnen HTTP-Aufruf ein vollständiges Protokoll erzeugen nach
[references/api-parity-checklist.md](references/api-parity-checklist.md). Ergebnis:
`API-CONTRACT.md`. Diese Datei ist die verbindliche Spezifikation für Phase 5 — nicht der Kotlin-Code
im Kopf.

### Phase 3 — Funktionsinventar und Mapping

Jede Funktion der App als Zeile erfassen, mit Android-Mechanismus und Windows-Pendant nach
[references/net8-winforms-recipes.md](references/net8-winforms-recipes.md). Spalten:
`Funktion | Android-Nachweis (Datei:Zeile) | Windows-Umsetzung | Status`.
Status ist an diesem Punkt überall `geplant`.

### Phase 4 — Zielprojekt anlegen

Neuer Ordner im Repo (Preset ATPanther: `ATPanther Windows/`, mit Leerzeichen — in Pfadangaben
stets quoten). Struktur: `<Ordner>/<Projektname>.csproj`, `Program.cs`, Unterordner für
`Api/`, `Auth/`, `Data/`, `Monitor/`, `Ui/`, dazu `README.md`, `API-CONTRACT.md`, `PARITY.md` und ein
Publish-Profil. Ziel-Framework: `net8.0-windows` mit WinForms.

Vorhandene Ports aus der Git-Historie sind **Lesematerial, keine Arbeitsgrundlage**:
`git show <commit>^:<pfad>` zeigt alte Dateien, ohne sie wiederherzustellen. Nicht wholesale
zurückkopieren — der Port entsteht neu und wird gegen den Vertrag validiert.

### Phase 5 — Implementieren

Umsetzung strikt aus `API-CONTRACT.md`, nicht aus dem Gefühl. Pro Netzwerkaufruf: URL-Aufbau,
Header in derselben Reihenfolge, Body-byte-genau, Redirect-/Cookie-Verhalten identisch,
Timeouts und Fehlerpfade inklusive Exception-Zählung übernommen.

### Phase 6 — Build über GitHub Actions

Workflow nach [references/ci-github-actions.md](references/ci-github-actions.md) anlegen, triggern,
Run beobachten, Artefakt herunterladen. Der Build gilt erst als Erfolg, wenn der Actions-Run grün
und das Artefakt vorhanden ist — nicht, wenn der Code "fertig aussieht".

### Phase 7 — Verifikation und Übergabe

1. jeden Vertrag aus `API-CONTRACT.md` gegen den fertigen C#-Code abgleichen (Parameter, Header,
   Body, Fehlerpfade) und Ergebnisse in `PARITY.md` auf `verifiziert` setzen;
2. `PARITY.md`-Zeilen mit Status unklar offenlegen statt sie zu löschen;
3. CI-Artefakt benennen und Installations-/Startweg beschreiben;
4. berichten: Was ist gleich, was weicht ab (mit Begründung), was ist ungeprüft.

## Wenn es hakt

- **Vertrag unvollständig** → Phase 2 wiederholen, nicht mit Platzhaltern weiterbauen.
- **CI schlägt fehl** → Log lesen, Fehler im Code beheben, erneut pushen. Kein Ausweichen auf lokalen
  Build, auch nicht "nur zum Testen".
- **Verhalten auf Windows anders, obwohl Code gleich aussieht** → fast immer Header-Reihenfolge,
  Groß-/Kleinschreibung, URL-Kodierung, Redirect-Politik oder String-vs-Zahl im JSON. Genau dort
  zuerst nachprüfen.
