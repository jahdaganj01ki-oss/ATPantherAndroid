# AT Panther – ALDI Talk Auto-Refill

Monorepo mit **zwei Android-Varianten** von AT Panther: eine App, die das
ALDI-Talk-Datenvolumen überwacht und automatisch **1 GB** nachbucht, sobald der
Restbestand unter die eingestellte Schwelle fällt (Standard: **850 MB**,
Prüfintervall standardmäßig 60 Sekunden).

## Repo-Struktur

| Ordner | Variante | CI-Workflow | Artifact |
|---|---|---|---|
| `app/` + Root-Gradle-Projekt | **Android-App** (Basisversion) | `android.yml`, `build.yml` | `AT-Panther-debug-apk` bzw. `ATPanther-debug` |
| [`Ulefone Power Armor X11Pro/`](Ulefone%20Power%20Armor%20X11Pro/README.md) | **Android-Gerätevariante** fürs Ulefone Power Armor X11Pro (Android 12, Helio G25) – mit Freeze-Fixes + monochromem Design | `x11pro.yml` | `AT-Panther-X11Pro-debug-apk` (Datei: **`AT Panther.apk`**) |

Details, Build-Anleitungen und gerätespezifische Hinweise stehen in der
README des jeweiligen Ordners.

## Features (beide Varianten)

- 📡 Hintergrundüberwachung des ALDI-Talk-Datenvolumens (Login über das
  Kundenportal: ForgeRock-PoW → PKCE → Redirect-Kette)
- ⚡ Automatische 1-GB-Nachbuchung unterhalb der einstellbaren Schwelle
- 📋 Verlauf/Log aller Prüfungen und Buchungen, Export als Textdatei
- ⚙️ Einstellbare Schwelle (Standard 850 MB) und Prüfintervall (Standard 60 s)
- 🌑 Dark-Theme – Root-App: schwarz mit violetten Akzenten;
  X11Pro-Variante: monochrom (nur Schwarz/Weiß/Grau)
- 🔁 Auto-Re-Login bei abgelaufener Session (max. 5 Fehlversuche in Folge)
- 🔄 AlarmManager-Fallback + START_STICKY, damit der Monitor das System- kill
  überlebt (X11Pro zusätzlich mit WakeLock-Guard)

## Speicherung der Zugangsdaten

Die Login-Daten liegen auf Android in App-SharedPreferences
(`at_panther_secure`) – **nicht** verschlüsselt. Die App läuft als
Foreground-Service mit permanenter Notification. Für ein privates
Einzelbenutzer-Gerät akzeptabel, aber bewusst wissen.

## Builds via GitHub Actions

Die drei Workflows bauen bei Push auf `main`/`master` (teilweise
pfadgefiltert) und per manuellem `workflow_dispatch`:

| Workflow | Baut | Ergebnis |
|---|---|---|
| `android.yml` | Root-App | `app-debug.apk` (Artifact `AT-Panther-debug-apk`) |
| `build.yml` | Root-App | `app-debug.apk` (Artifact `ATPanther-debug`) |
| `x11pro.yml` | X11Pro-Variante | **`AT Panther.apk`** (Artifact `AT-Panther-X11Pro-debug-apk`) |

Die fertigen APKs liegen im Tab **Actions → jeweiligen Run → Artifacts**.

## Lokal bauen

**Android (Root oder X11Pro-Variante):**
```powershell
.\gradlew.bat assembleDebug                 # Root-App
cd "Ulefone Power Armor X11Pro"; .\gradlew.bat assembleDebug   # Gerätevariante
```

## Hinweise

- Die **X11Pro-Variante installiert sich parallel** zur Root-App (eigene
  `applicationId` `com.alditalk.panther.x11pro`) – beide können gleichzeitig
  auf dem Gerät sein. Die Varianten-README beschreibt zusätzlich die
  Freeze-Fixes und die empfohlenen Akku-Einstellungen fürs Ulefone.
- CI-Debug-Builds werden **pro Lauf neu signiert** (generierter Debug-Key):
  vor einer Update-Installation die alte App deinstallieren – oder eigene
  Keystore-Secrets hinterlegen (nur X11Pro-Workflow unterstützt das aktuell).
- Die automatische Nachbuchung bitte nur im Rahmen der
  ALDI-Talk-Nutzungsbedingungen verwenden.
