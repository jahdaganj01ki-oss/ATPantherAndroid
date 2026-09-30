# AT Panther – ALDI Talk Auto-Refill

Monorepo mit **vier Android-Varianten** von AT Panther: eine App, die das
ALDI-Talk-Datenvolumen überwacht und automatisch **1 GB** nachbucht, sobald der
Restbestand unter die eingestellte Schwelle fällt (Standard: **850 MB**,
Prüfintervall standardmäßig 60 Sekunden).

## Repo-Struktur

| Ordner | Variante | CI-Workflow | Artifact |
|---|---|---|---|
| `app/` + Root-Gradle-Projekt | **Android-App** (Basisversion) – gleicher Funktionsstand wie die Gerätevarianten (Freeze-Fixes, Login-Pause, monochromes Design) | `android.yml`, `build.yml` | `AT-Panther-debug-apk` bzw. `ATPanther-debug` |
| [`Ulefone Power Armor X11Pro/`](Ulefone%20Power%20Armor%20X11Pro/README.md) | **Android-Gerätevariante** fürs Ulefone Power Armor X11Pro (Android 12, Helio G25) – Screenshot-UI (scrollfrei, Verlauf per Wisch), Freeze-Fixes + monochromem Design | `x11pro.yml` | `AT-Panther-X11Pro-debug-apk` (Datei: **`AT Panther Ulefone Power Armor X11Pro.apk`**) |
| [`Redmi Note 9 Pro/`](Redmi%20Note%209%20Pro/README.md) | **Android-Gerätevariante** fürs Xiaomi Redmi Note 9 Pro (Android 11, MIUI, Snapdragon 720G) – Freeze-Fixes + monochromes Design + MIUI-Akku-Anleitung | `redmi-note-9-pro.yml` | `AT-Panther-RedmiNote9Pro-debug-apk` (Datei: **`AT Panther.apk`**) |
| [`Moto G84 5G/`](Moto%20G84%205G/README.md) | **Android-Gerätevariante** fürs Motorola Moto G84 5G (Android 13, Snapdragon 695, 6,5" 120 Hz pOLED) – Freeze-Fixes + monochromes Design, Stock-Android-Optimierung | `motog84.yml` | `AT-Panther-MotoG84-debug-apk` (Datei: **`AT Panther Moto G84 5G.apk`**) |
| [`Windows/`](Windows/README.md) | **Windows-Variante** (WPF, .NET 8) – Login/Abfrage/Auto-Nachbuchung 1:1 wie Ulefone-Variante | `windows.yml` | `AT-Panther-Windows-win-x64` (self-contained `.exe`) |

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
- 🔒 **Monitor-Freigabe** (alle Varianten): nur **ein** Gerät darf zur Zeit
  das Portal abfragen. Die Freigabe liegt in einer Cloudflare-Worker-Lease
  (kostenlos) – alle anderen Varianten gehen automatisch in den
  Bereitschaftsmodus und berühren das Portal gar nicht. Details unten.
- 🔄 AlarmManager-Fallback + START_STICKY, damit der Monitor das System-Kill
  überlebt

## Monitor-Freigabe: nur eine Variante überwacht

Wird dieselbe App auf mehreren Geräten mit **demselben Login** betrieben
(Windows + Ulefone + Moto), fragen alle Varianten eigenständig alle 60 s das
Portal ab. Das vervielfacht die Abfragen und damit das Risiko einer
temporären Kontosperre. Deshalb gibt es eine zentrale Freigabe:

- Genau **eine** Variante besitzt die „Freigabe" (eine 15-Minuten-Lease im
  Cloudflare Worker + D1) und darf das Portal abfragen.
- Alle anderen starten nicht: kein Login, keine Datenabfrage, keine Buchung.
  Sie zeigen „⏸ Bereitschaft: <Variante> fragt ab" und beenden sich selbst.
- **Umschalten**: auf dem gewünschten Gerät *Monitor-Freigabe → Übernehmen*.
  Das alte Gerät erkennt den Verlust beim nächsten Check (≤ 5 min) und hört
  von selbst auf zu pollen. Eine Freigabe läuft außerdem automatisch ab,
  wenn das Gerät ausfällt – die nächste Variante nimmt sie dann allein.
- **Kostenlos**: Cloudflare Workers (100.000 Requests/Tag) und D1 sind
  kostenlos; die Lösung braucht rund 870 Requests/Tag. Es werden **keine**
  Zugangsdaten gespeichert, nur ein Variantenname.
- **Ausfallverhalten**: standardmäßig *fail-closed* – ist der Freigabe-Server
  nicht erreichbar, wird **nicht** abgefragt (der letzte bekannte Stand gilt
  30 Minuten). Das Verhalten ist im Dialog umschaltbar.

Einrichtung und API: [`worker/README.md`](worker/README.md).
Falls beim Cloudflare-Token etwas klemmt:
[`CLOUDFLARE-TOKEN-ANLEITUNG.md`](CLOUDFLARE-TOKEN-ANLEITUNG.md) – dort steht
Schritt für Schritt, welche Berechtigungen der Token braucht.

Nach der Einrichtung wird die Worker-URL in jeder App unter
*Monitor-Freigabe* eingetragen; Standard-Owner ist die **Windows**-Variante.

## Speicherung der Zugangsdaten

Die Login-Daten liegen auf Android in App-SharedPreferences
(`at_panther_secure`) – **nicht** verschlüsselt. Die App läuft als
Foreground-Service mit permanenter Notification. Für ein privates
Einzelbenutzer-Gerät akzeptabel, aber bewusst wissen.

## Builds via GitHub Actions

Die fünf Workflows bauen bei Push auf `main`/`master` (teilweise
pfadgefiltert) und per manuellem `workflow_dispatch`:

| Workflow | Baut | Ergebnis |
|---|---|---|
| `android.yml` | Root-App | `app-debug.apk` (Artifact `AT-Panther-debug-apk`) |
| `build.yml` | Root-App | `app-debug.apk` (Artifact `ATPanther-debug`) |
| `x11pro.yml` | X11Pro-Variante | **`AT Panther Ulefone Power Armor X11Pro.apk`** (Artifact `AT-Panther-X11Pro-debug-apk`) |
| `redmi-note-9-pro.yml` | Redmi-Note-9-Pro-Variante | **`AT Panther.apk`** (Artifact `AT-Panther-RedmiNote9Pro-debug-apk`) |
| `motog84.yml` | Moto-G84-5G-Variante | **`AT Panther Moto G84 5G.apk`** (Artifact `AT-Panther-MotoG84-debug-apk`) |

Die fertigen APKs liegen im Tab **Actions → jeweiligen Run → Artifacts**.

## Lokal bauen

**Android (Root oder Gerätevarianten):**
```powershell
.\gradlew.bat assembleDebug                 # Root-App
cd "Ulefone Power Armor X11Pro"; .\gradlew.bat assembleDebug   # X11Pro-Variante
cd "Redmi Note 9 Pro"; .\gradlew.bat assembleDebug             # Redmi-Variante
cd "Moto G84 5G"; .\gradlew.bat assembleDebug                   # Moto-G84-Variante
```

## Hinweise

- Die **Gerätevarianten installieren sich parallel** zur Root-App und
  zueinander (eigene `applicationId` pro Variante:
  `com.alditalk.panther.x11pro`, `com.alditalk.panther.redminote9pro`,
  `com.alditalk.panther.motog845g`) –
  mehrere können gleichzeitig auf dem Gerät sein. Der App-Code
  (Kotlin + Ressourcen) ist seit v1.1 in allen vier Varianten identisch;
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
