# AT Panther – X11Pro-Variante (Ulefone Power Armor X11Pro)

Eigenstaendiges Gradle-Projekt für das **Ulefone Power Armor X11Pro** (MediaTek
Helio G25, 4 GB RAM, 64 GB, Android 12, 8150 mAh, 5,45" 720x1440).
Seit v1.3 eigene Screenshot-UI: Haupt-Seite scrollfrei (Login-Daten +
Einstellungen + Speichern in Card 1, Monitor + Wartung in Card 2),
Verlauf als eigene Seite per Wisch (links hin, rechts zurueck, plus
Zurueck-Button); graue Buttons fuer Speichern/Monitor starten; nach
"Monitor starten" oeffnet sich automatisch der Verlauf.

Die Variante installiert sich als **eigene App** (`com.alditalk.panther.x11pro`)
parallel zur Original-App – kein Update-Konflikt, beide können gleichzeitig
installiert sein.

## Build

### GitHub Actions
Der Workflow `.github/workflows/x11pro.yml` baut bei jedem Push auf `main`/`master`,
der den Ordner `Ulefone Power Armor X11Pro/` berührt, automatisch ein APK und
lädt es als Artifact **AT-Panther-X11Pro-debug-apk** hoch. Die APK-Datei im
Artifact heißt **`AT Panther Ulefone Power Armor X11Pro.apk`** (nicht `app-debug.apk`).

Optionales signiertes Release: Repo-Secrets `ANDROID_KEYSTORE_BASE64`,
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` setzen.

### Lokal
```powershell
cd "Ulefone Power Armor X11Pro"
.\gradlew.bat assembleDebug
# APK: app\build\outputs\apk\debug\app-debug.apk
```

## Freeze-Fixes (Warum hängt die Original-App?)

| # | Ursache | Fix |
|---|---------|-----|
| 1 | RecyclerView mit `layout_weight` in ScrollView → kein Recycling; bei wachsender Log-Liste werden alle Zeilen auf einmal gemessen/gezeichnet → Freeze beim Öffnen/Drehen | v1.2: Feste Höhe `260dp`; **v1.3: Verlauf als eigene ViewPager-Seite mit `match_parent`-RecyclerView (kein ScrollView mehr)** |
| 2 | `notifyDataSetChanged()` bei jedem 60-s-Poll – komplette Liste neu binden | `ListAdapter` + `DiffUtil`: nur neue Zeilen werden gebunden (v1.3 in `LogFragment.kt`) |
| 3 | Log-DB wächst unbegrenzt (~1440 Zeilen/Tag), UI lädt ALLE Einträge | UI lädt max. 200 Zeilen (`getRecent()`), Service trimmt DB hart auf 5000 (`deleteBeyondLimit()`) |
| 4 | WakeLock lief nach 10 min still aus → CPU schläft zwischen Pollings ein | WakeLock 30 min + Guard-Job, der alle 9 min verlängert (`MonitorService.kt`) |
| 5 | CoroutineScope im Service nicht geschlossen → Leak bei Stop/Start-Zyklen | Scope als Member, sauber `cancel()` in `onDestroy()` |
| 6 | CookieJar nicht thread-safe → sporadische `ConcurrentModificationException` im Login-Loop | `ConcurrentHashMap` + `synchronized` (`OkHttpCookieJar.kt`) |
| 7 | Activity-Rebuild bei Drehung → komplette View-Hierarchie + RecyclerView neu auf dem schwachen Helio G25 | `launchMode="singleTask"` + `configChanges` → App-Start/Drehen/Re-Öffnen re-uses die laufende Activity (`AndroidManifest.xml`) |
| 8 | Log-DB ohne Index: `ORDER BY timestamp DESC` sortierte bei jedem 60-s-Poll bis zu 5000 Zeilen voll | Index auf `LogEntry.timestamp` (DB v2, `AppDatabase`/`LogEntry.kt`) |
| 9 | DB-Trim (2 Schreib-Transaktionen) bei jedem Poll → Flow-Requery + DiffUtil-Durchlauf alle 60 s | Trim gedrosselt: Alter nur ~stündlich, Limit nur alle ~10 min und nur bei Bedarf (`MonitorService.kt`, v1.2) |
| 10 | SHA-1-PoW-Loop (bis 10 Mio Hashes) blockierte einen `Dispatchers.IO`-Thread → Login-Starvation | PoW läuft auf `Dispatchers.Default` (`AuthService.kt`, v1.2) |
| 11 | Dutzende `Log.e`-Zeilen pro Login (inkl. Body-Dump) → CPU/I-O im Logcat | Trace-Logs auf `Log.d` zurückgestuft (`AuthService.kt`, v1.2) |
| 12 | `DefaultItemAnimator` animierte jeden 60-s-Diff auf der schwachen GPU → Ruckler, v. a. bei Rotation | `itemAnimator = null` + `setHasFixedSize(true)` + 20er View-Cache (v1.3 in `LogFragment.kt`, v1.2 in `MainActivity.kt`) |
| 13 | Log-Export baute bis zu 200 formatierte Zeilen auf dem UI-Thread → Hänger beim Tippen | Kompletter Export auf `Dispatchers.IO` (`MainActivity.kt`, v1.2) |
| 14 | `configChanges` unvollständig → Android 12 konnte beim Drehen trotzdem rebuilden | `smallestScreenSize\|layoutDirection` ergänzt (v1.2); v1.3: kein manueller Scroll-Erhalt mehr noetig (Verlauf in eigenem Fragment) |
| 15 | DB wurde synchron in `Application.onCreate` aufgebaut → langsamer Kaltstart | Vorwärmen im Hintergrund-Scope (`PantherApp.kt`, v1.2) |

Fix #1 + #2 sind die wahrscheinlichsten Auslöser für das von dir beobachtete
Muster „Freeze nach längerer Laufzeit / bei Drehen / beim Wiederaufnehmen".
Fix #8–#15 (v1.2) adressieren die restlichen Drehen-/Ruckler-Ursachen gezielt
auf dem Helio G25 des X11Pro.

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

## Geräte-Konfiguration

- **Monochromes Theme**: ausschließliches Schwarz/Weiß/Grau – schwarzer
  Hintergrund (`#000000`), helle Schrift (`#FFFFFF`/`#B0B0B0`), dunkelgraue
  Karten (`#141414`), **graue Buttons** (`#9E9E9E`, Speichern + Monitor starten),
  grau-skalierte Statusfarben. Keine Buntakzente (Violett/Blau/Grün/Orange/Rot entfernt).
- **Monochromes App-Icon**: schwarzer Grund, weiße Pfoten-Silhouette
  (`values/colors.xml`, `values/ic_launcher_background.xml`, `drawable/ic_launcher_foreground.xml`)
- `abiFilters`: `arm64-v8a`, `armeabi-v7a` (Helio G25 ist ARM) – schlanke APK
- `resConfigs("de","en")` – weniger Ressourcen-Auflösung
- `applicationId`: `com.alditalk.panther.x11pro` (Debug: `...x11pro.debug`)
- `versionName`: `1.3-x11pro` (versionCode 4)
- App-Name: **AT Panther X11Pro**

## Empfohlene Einstellungen auf dem Gerät (Android 12 / Ulefone)

Damit der Monitor zuverlässig läuft:

1. **Einstellungen → Akku → Akku-Optimierung** → AT Panther X11Pro → *Nicht optimieren*
2. **Einstellungen → Apps → AT Panther X11Pro → Akku** → *Uneingeschränkt* + Hintergrundaktivität erlauben
3. In der App: **Batterie-Optimierung ignorieren**-Button nutzen
4. Falls vorhanden: **DuraSpeed** (Ulefone-Hintergrundmanager) → AT Panther X11Pro zulassen/whitelisten
5. Falls vorhanden (Android 12): Akku → App-Standby deaktivieren für die App
