# AT Panther – ALDI Talk Auto-Refill

Monorepo mit **drei Android-Varianten** von AT Panther: eine App, die das
ALDI-Talk-Datenvolumen überwacht und automatisch **1 GB** nachbucht, sobald der
Restbestand unter die eingestellte Schwelle fällt (Standard: **850 MB**,
Prüfintervall standardmäßig 60 Sekunden).

## Repo-Struktur

| Ordner | Variante | CI-Workflow | Artifact |
|---|---|---|---|
| `app/` + Root-Gradle-Projekt | **Android-App** (Basisversion) – gleicher Funktionsstand wie die Gerätevarianten (Freeze-Fixes, Login-Pause, monochromes Design) | `android.yml`, `build.yml` | `AT-Panther-debug-apk` bzw. `ATPanther-debug` |
| [`Ulefone Power Armor X11Pro/`](Ulefone%20Power%20Armor%20X11Pro/README.md) | **Android-Gerätevariante** fürs Ulefone Power Armor X11Pro (Android 12, Helio G25) – mit Freeze-Fixes + monochromem Design | `x11pro.yml` | `AT-Panther-X11Pro-debug-apk` (Datei: **`AT Panther.apk`**) |
| [`Redmi Note 9 Pro/`](Redmi%20Note%209%20Pro/README.md) | **Android-Gerätevariante** fürs Xiaomi Redmi Note 9 Pro (Android 11, MIUI, Snapdragon 720G) – Freeze-Fixes + monochromes Design + MIUI-Akku-Anleitung | `redmi-note-9-pro.yml` | `AT-Panther-RedmiNote9Pro-debug-apk` (Datei: **`AT Panther.apk`**) |
| [`AT Panther Windows/`](AT%20Panther%20Windows/README.md) | **Windows-Port** (.NET 8, WinForms) – 1:1-Port der Ulefone-Variante (v1.2-Stand: Trim-Drosselung, Verlauf neueste zuerst, App-Icon, Diagnose-Trace) | `at-panther-windows.yml` | `ATPanther-win-x64-windows` (Datei: **`ATPanther-win-x64-windows.zip`** mit `ATPanther.exe`) |

Details, Build-Anleitungen und gerätespezifische Hinweise stehen in der
README des jeweiligen Ordners.

## Features (alle Varianten)

- 📡 Hintergrundüberwachung des ALDI-Talk-Datenvolumens (Login über das
  Kundenportal: ForgeRock-PoW → PKCE → Redirect-Kette)
- ⚡ Automatische 1-GB-Nachbuchung unterhalb der einstellbaren Schwelle
- 📋 Verlauf/Log aller Prüfungen und Buchungen, Export als Textdatei
- ⚙️ Einstellbare Schwelle (Standard 850 MB) und Prüfintervall (Standard 60 s)
- 🌑 Monochromes Design (alle Varianten): nur Schwarz/Weiß/Grau –
  dunkler Hintergrund, helle Schrift, monochromes App-Icon
- ❄️ Freeze-Fixes: Log-UI auf 200 Einträge begrenzt + DB-Trim auf 5000,
  ListAdapter/DiffUtil statt `notifyDataSetChanged()`, `singleTask` +
  `configChanges`, thread-safe CookieJar, sauberes Coroutine-Scope-Handling
- 🔁 Auto-Re-Login bei abgelaufener Session (max. 5 Fehlversuche in Folge)
- ⛔ Login-Schutz (alle Varianten): nach 3 fehlgeschlagenen
  Verbindungs-/Login-Versuchen hintereinander (oder 5 Re-Logins ohne
  erfolgreiche Datenafrage) pausiert der Monitor automatisch mit
  Alarm-Benachrichtigung – kein weiterer automatischer Versuch bis zum
  manuellen Neustart (2× „Start" in der App)
- 🔄 AlarmManager-Fallback + START_STICKY, damit der Monitor das System-Kill
  überlebt

## Speicherung der Zugangsdaten

Die Login-Daten liegen auf Android in App-SharedPreferences
(`at_panther_secure`) – **nicht** verschlüsselt. Die App läuft als
Foreground-Service mit permanenter Notification. Für ein privates
Einzelbenutzer-Gerät akzeptabel, aber bewusst wissen.

## Builds via GitHub Actions

Die vier Workflows bauen bei Push auf `main`/`master` (teilweise
pfadgefiltert) und per manuellem `workflow_dispatch`:

| Workflow | Baut | Ergebnis |
|---|---|---|
| `android.yml` | Root-App | `app-debug.apk` (Artifact `AT-Panther-debug-apk`) |
| `build.yml` | Root-App | `app-debug.apk` (Artifact `ATPanther-debug`) |
| `x11pro.yml` | X11Pro-Variante | **`AT Panther.apk`** (Artifact `AT-Panther-X11Pro-debug-apk`) |
| `redmi-note-9-pro.yml` | Redmi-Note-9-Pro-Variante | **`AT Panther.apk`** (Artifact `AT-Panther-RedmiNote9Pro-debug-apk`) |
| `at-panther-windows.yml` | Windows-Port (`AT Panther Windows/`) | **`ATPanther.exe`** im ZIP (Artifact `ATPanther-win-x64-windows`) |

Die fertigen APKs/ZIPs liegen im Tab **Actions → jeweiligen Run → Artifacts**.

## Lokal bauen

**Android (Root oder Gerätevarianten):**
```powershell
.\gradlew.bat assembleDebug                 # Root-App
cd "Ulefone Power Armor X11Pro"; .\gradlew.bat assembleDebug   # X11Pro-Variante
cd "Redmi Note 9 Pro"; .\gradlew.bat assembleDebug             # Redmi-Variante
```

## Hinweise

- Die **Gerätevarianten installieren sich parallel** zur Root-App und
  zueinander (eigene `applicationId` pro Variante:
  `com.alditalk.panther.x11pro`, `com.alditalk.panther.redminote9pro`) –
  mehrere können gleichzeitig auf dem Gerät sein. Der App-Code
  (Kotlin + Ressourcen) ist seit v1.1 in allen drei Varianten identisch;
  die Root-App unterscheidet sich nur durch `applicationId`
  `com.alditalk.panther`, App-Name „AT Panther" und universale
  ABI-/Sprachauswahl. Die Varianten-READMEs beschreiben zusätzlich die
  empfohlenen Akku-Einstellungen (Ulefone: Akku-Optimierung; Redmi:
  MIUI-Autostart, „Keine Einschränkungen", App-Sperre im Recents-Screen).
- CI-Debug-Builds werden **pro Lauf neu signiert** (generierter Debug-Key):
  vor einer Update-Installation die alte App deinstallieren – oder eigene
  Keystore-Secrets hinterlegen (nur die Gerätevarianten-Workflows
  unterstützen das aktuell).
- Die automatische Nachbuchung bitte nur im Rahmen der
  ALDI-Talk-Nutzungsbedingungen verwenden.
