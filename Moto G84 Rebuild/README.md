# AT Panther – Moto G84 Rebuild (X11Pro v1.2, einseitig)

Eigenstaendiges Gradle-Projekt. Inhalt ist eine **1:1-Kopie** der X11Pro-Variante
(`Ulefone Power Armor X11Pro/`) im Stand **v1.2-x11pro**, dem **letzten Stand vor
dem Split des UI auf zwei Seiten**.

## Quelle des Snapshots

| | |
|---|---|
| Quelle | `Ulefone Power Armor X11Pro/` (Commit `dfa10e3`) |
| Stand | `versionName = 1.2-x11pro`, `versionCode = 3` |
| Abzug | `git archive dfa10e3 "Ulefone Power Armor X11Pro"` |
| Dateien | 43, per SHA256-Abgleich gegen die Quelle verifiziert |

### Warum genau dieser Stand?

Der 2-Seiten-Split kam mit **v1.3** (`26b3db5`): dort wurde `activity_main.xml`
durch einen `ViewPager2` mit zwei Seiten ersetzt (Seite 0 = Haupt, Seite 1 =
Verlauf) und `MainActivity` bekam `MainFragment`/`LogFragment`. Davor war alles
**eine einzige Seite** – ein `ScrollView` mit sechs Karten (Login, Einstellungen,
Monitor, Wartung, Verlauf). Genau dieser Stand liegt hier.

Was dieser Ordner **nicht** enthält (alles erst ab v1.3):

- kein `ViewPager2`, keine Fragmente, kein `MainFragment.kt`/`LogFragment.kt`
- kein `MainUiState`/`LogUiState` (Zustand lag direkt in der Activity)
- keine Tab-Buttons mit ● / ○ Indikator (die kamen erst in v1.5)
- keine kompakte einseitige Hauptseite aus v1.4

### Abweichungen gegenüber dem Original-Snapshot

Der Code ist unveraendert. Nur die Projekt-Identitaet wurde angepasst, damit der
Rebuild parallel zu allen anderen Varianten installierbar bleibt:

| Datei | Original (v1.2) | Rebuild |
|---|---|---|
| `app/build.gradle.kts` | `applicationId = com.alditalk.panther.x11pro` | `com.alditalk.panther.motog84rebuild` |
| `app/build.gradle.kts` | `versionCode = 3` | `1` |
| `app/build.gradle.kts` | `versionName = 1.2-x11pro` | `1.2-x11pro-rebuild` |
| `settings.gradle.kts` | `ATPanther-X11Pro` | `ATPanther-MotoG84Rebuild` |
| `res/values/strings.xml` | `AT Panther X11Pro` | `AT Panther G84 Rebuild` |

Alle uebrigen 40 Dateien sind bitidentisch mit dem Original-Snapshot.
`namespace` bleibt `com.alditalk.panther` – nur die `applicationId` unterscheidet
sich, dadurch ist keine Code-Aenderung noetig.

### Installierbarkeit neben den anderen Varianten

| Variante | applicationId |
|---|---|
| Original (`app/`) | `com.alditalk.panther` |
| **Moto G84 Rebuild (dieser Ordner)** | `com.alditalk.panther.motog84rebuild` |
| Ulefone Power Armor X11Pro | `com.alditalk.panther.x11pro` |
| Moto G84 5G | `com.alditalk.panther.motog845g` |
| Redmi Note 9 Pro | `com.alditalk.panther.redminote9pro` |

## Build

### Lokal
```powershell
cd "Moto G84 Rebuild"
.\gradlew.bat assembleDebug
# APK: app\build\outputs\apk\debug\app-debug.apk
```

Die Debug-APK traegt das Suffix `.debug` und ist damit parallel zur Release-Variante
installierbar.

### GitHub Actions
Der Workflow `.github/workflows/moto-g84-rebuild.yml` baut bei jedem Push auf
`main`/`master`, der den Ordner `Moto G84 Rebuild/` berührt, automatisch beide
APKs und lädt sie als Artefakte hoch:

| Artefakt | APK-Datei im Archiv |
|---|---|
| `AT-Panther-MotoG84Rebuild-debug-apk` | `AT Panther Moto G84 Rebuild Debug.apk` |
| `AT-Panther-MotoG84Rebuild-release-apk` | `AT Panther Moto G84 Rebuild Release.apk` |

Debug und Release werden getrennt gebaut und getrennt benannt. Das
Release-APK ist signiert, sofern die Repo-Secrets `ANDROID_KEYSTORE_BASE64`,
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` gesetzt
sind; ohne Secrets wird es unsigniert gebaut.

Manueller Start: **Actions → Moto G84 Rebuild Build → Run workflow**.

## Freeze-Fixes (Warum haengt die Original-App?)

| # | Ursache | Fix |
|---|---------|-----|
| 1 | RecyclerView mit `layout_weight` in ScrollView → kein Recycling; bei wachsender Log-Liste werden alle Zeilen auf einmal gemessen/gezeichnet → Freeze beim Öffnen/Drehen | Feste Höhe `260dp` – RecyclerView scrollt intern, misst nur sichtbare Zeilen (`app/src/main/res/layout/activity_main.xml`) |
| 2 | `notifyDataSetChanged()` bei jedem 60-s-Poll – komplette Liste neu binden | `ListAdapter` + `DiffUtil`: nur neue Zeilen werden gebunden (`MainActivity.kt`) |
| 3 | Log-DB wächst unbegrenzt (~1440 Zeilen/Tag), UI lädt ALLE Einträge | UI lädt max. 200 Zeilen (`getRecent()`), Service trimmt DB hart auf 5000 (`deleteBeyondLimit()`) |
| 4 | WakeLock lief nach 10 min still aus → CPU schläft zwischen Pollings ein | WakeLock 30 min + Guard-Job, der alle 9 min verlängert (`MonitorService.kt`) |
| 5 | CoroutineScope im Service nicht geschlossen → Leak bei Stop/Start-Zyklen | Scope als Member, sauber `cancel()` in `onDestroy()` |
| 6 | CookieJar nicht thread-safe → sporadische `ConcurrentModificationException` im Login-Loop | `ConcurrentHashMap` + `synchronized` (`OkHttpCookieJar.kt`) |
| 7 | Activity-Rebuild bei Drehung → komplette View-Hierarchie + RecyclerView neu auf dem schwachen Helio G25 | `launchMode="singleTask"` + `configChanges` → App-Start/Drehen/Re-Öffnen re-uses die laufende Activity (`AndroidManifest.xml`) |
| 8 | Log-DB ohne Index: `ORDER BY timestamp DESC` sortierte bei jedem 60-s-Poll bis zu 5000 Zeilen voll | Index auf `LogEntry.timestamp` (DB v2, `AppDatabase`/`LogEntry.kt`) |
| 9 | DB-Trim (2 Schreib-Transaktionen) bei jedem Poll → Flow-Requery + DiffUtil-Durchlauf alle 60 s | Trim gedrosselt: Alter nur ~stündlich, Limit nur alle ~10 min und nur bei Bedarf (`MonitorService.kt`, v1.2) |
| 10 | SHA-1-PoW-Loop (bis 10 Mio Hashes) blockierte einen `Dispatchers.IO`-Thread → Login-Starvation | PoW läuft auf `Dispatchers.Default` (`AuthService.kt`, v1.2) |
| 11 | Dutzende `Log.e`-Zeilen pro Login (inkl. Body-Dump) → CPU/I-O im Logcat | Trace-Logs auf `Log.d` zurückgestuft (`AuthService.kt`, v1.2) |
| 12 | `DefaultItemAnimator` animierte jeden 60-s-Diff auf der schwachen GPU → Ruckler, v. a. bei Rotation | `itemAnimator = null` + `setHasFixedSize(true)` + 20er View-Cache (`MainActivity.kt`, v1.2) |
| 13 | Log-Export baute bis zu 200 formatierte Zeilen auf dem UI-Thread → Hänger beim Tippen | Kompletter Export auf `Dispatchers.IO` (`MainActivity.kt`, v1.2) |
| 14 | `configChanges` unvollständig → Android 12 konnte beim Drehen trotzdem rebuilden | `smallestScreenSize\|layoutDirection` ergänzt + `onConfigurationChanged()` hält Scroll-Position (v1.2) |
| 15 | DB wurde synchron in `Application.onCreate` aufgebaut → langsamer Kaltstart | Vorwärmen im Hintergrund-Scope (`PantherApp.kt`, v1.2) |

Fix #1 + #2 sind die wahrscheinlichsten Auslöser für das beobachtete Muster
„Freeze nach längerer Laufzeit / bei Drehen / beim Wiederaufnehmen".
Fix #8–#15 (v1.2) adressieren die restlichen Drehen-/Ruckler-Ursachen gezielt
auf dem Helio G25 des X11Pro.

> Hinweis zur Ziel-Hardware: Die Geräte-Tweaks (#1, #12, #15 u. a.) sind auf den
> schwachen Helio G25 des X11Pro abgestimmt. Der Moto G84 hat deutlich mehr
> Leistung – die Fixes sind dort aber harmlos und verhindern Ruckler ebenfalls.

## Schutz vor Account-Sperre (Login-Pause)

Scheitern **3 Verbindungs-/Login-Versuche hintereinander** (oder 5
erfolgreiche Re-Logins ohne erfolgreiche Datenafrage), stoppt der Monitor
**vollständig automatisch**:

- ⛔ **Hohe Alarm-Benachrichtigung** „AT Panther pausiert" (eigener Kanal
  „Monitor-Alarme") – sie bleibt sichtbar, obwohl der Service gestoppt ist
- **Kein automatischer Neustart mehr**: Fallback-Wecker ist abgebrochen,
  Boot-Start wird blockiert, und auch Netzwerk-Exceptions zählen jetzt als
  fehlgeschlagene Versuche (vorher lief der Loop bei WLAN-/DNS-Problemen
  endlos weiter)
- Nach jedem erfolgreichen Re-Login fragt der Loop das Datenvolumen sofort
  erneut ab und bucht ggf. direkt nach – kein Warten aufs normale Intervall.
  Schutz vor Login-Sturm: 5 Re-Logins ohne erfolgreiche Abfrage pausieren
  (der Login selbst mit PoW + Redirect-Kette dauert bereits Sekunden).
- Steht das Volumen komplett auf 0,0 MB (< 0,05 MB), wird nach erfolgreicher
  Erstbuchung – nach 3 s Pause plus frischer Datenabfrage – ein zweites Mal
  1 GB gebucht (wie 2x Klick auf „+1GB" im Portal, je mit Benachrichtigung).
- **Fortsetzung nur manuell:** App öffnen → „Start" hebt die Pause auf
  (Status „Pausiert — Start zum Fortsetzen") → nochmal „Start" startet den
  Monitor. Der erste Tipp loggt sich bewusst NICHT sofort ein.

## Geraete-Konfiguration

- **Monochromes Theme**: ausschließliches Schwarz/Weiß/Grau – schwarzer
  Hintergrund (`#000000`), helle Schrift (`#FFFFFF`/`#B0B0B0`), dunkelgraue
  Karten (`#141414`), weiße Buttons mit schwarzer Schrift, grau-skalierte
  Statusfarben. Keine Buntakzente (Violett/Blau/Grün/Orange/Rot entfernt).
- **Monochromes App-Icon**: schwarzer Grund, weiße Pfoten-Silhouette
  (`values/colors.xml`, `values/ic_launcher_background.xml`, `drawable/ic_launcher_foreground.xml`)
- `minSdk 24`, `targetSdk 34`, `compileSdk 34`
- `abiFilters`: `arm64-v8a`, `armeabi-v7a` – schlanke APK ohne x86-Emulatoren-ABIs
- `resConfigs("de","en")` – weniger Ressourcen-Auflösung
- `applicationId`: `com.alditalk.panther.motog84rebuild` (Debug: `...motog84rebuild.debug`)
- `versionName`: `1.2-x11pro-rebuild` (versionCode 1)
- App-Name: **AT Panther G84 Rebuild**

## Empfohlene Einstellungen auf dem Gerät

Damit der Monitor zuverlässig läuft:

1. **Einstellungen → Akku → Akku-Optimierung** → AT Panther G84 Rebuild → *Nicht optimieren*
2. **Einstellungen → Apps → AT Panther G84 Rebuild → Akku** → *Uneingeschränkt* + Hintergrundaktivität erlauben
3. In der App: **Batterie-Optimierung ignorieren**-Button nutzen
4. Falls vorhanden (Android 12+): Akku → App-Standby deaktivieren für die App