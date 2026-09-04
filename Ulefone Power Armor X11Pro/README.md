# AT Panther – X11Pro-Variante (Ulefone Power Armor X11Pro)

Eigenstaendiges Gradle-Projekt für das **Ulefone Power Armor X11Pro** (MediaTek
Helio G25, 4 GB RAM, 64 GB, Android 12, 8150 mAh). Basiert auf dem Root-Projekt
(`app/`), enthält aber gerätespezifische Optimierungen und Freeze-Fixes.

Die Variante installiert sich als **eigene App** (`com.alditalk.panther.x11pro`)
parallel zur Original-App – kein Update-Konflikt, beide können gleichzeitig
installiert sein.

## Build

### GitHub Actions
Der Workflow `.github/workflows/x11pro.yml` baut bei jedem Push auf `main`/`master`,
der den Ordner `Ulefone Power Armor X11Pro/` berührt, automatisch ein APK und
lädt es als Artifact **AT-Panther-X11Pro-debug-apk** hoch. Die APK-Datei im
Artifact heißt **`AT Panther.apk`** (nicht `app-debug.apk`).

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
| 1 | RecyclerView mit `layout_weight` in ScrollView → kein Recycling; bei wachsender Log-Liste werden alle Zeilen auf einmal gemessen/gezeichnet → Freeze beim Öffnen/Drehen | Feste Höhe `260dp` – RecyclerView scrollt intern, misst nur sichtbare Zeilen (`app/src/main/res/layout/activity_main.xml`) |
| 2 | `notifyDataSetChanged()` bei jedem 60-s-Poll – komplette Liste neu binden | `ListAdapter` + `DiffUtil`: nur neue Zeilen werden gebunden (`MainActivity.kt`) |
| 3 | Log-DB wächst unbegrenzt (~1440 Zeilen/Tag), UI lädt ALLE Einträge | UI lädt max. 200 Zeilen (`getRecent()`), Service trimmt DB hart auf 5000 (`deleteBeyondLimit()`) |
| 4 | WakeLock lief nach 10 min still aus → CPU schläft zwischen Pollings ein | WakeLock 30 min + Guard-Job, der alle 9 min verlängert (`MonitorService.kt`) |
| 5 | CoroutineScope im Service nicht geschlossen → Leak bei Stop/Start-Zyklen | Scope als Member, sauber `cancel()` in `onDestroy()` |
| 6 | CookieJar nicht thread-safe → sporadische `ConcurrentModificationException` im Login-Loop | `ConcurrentHashMap` + `synchronized` (`OkHttpCookieJar.kt`) |
| 7 | Activity-Rebuild bei Drehung → komplette View-Hierarchie + RecyclerView neu auf dem schwachen Helio G25 | `launchMode="singleTask"` + `configChanges` → App-Start/Drehen/Re-Öffnen re-uses die laufende Activity (`AndroidManifest.xml`) |

Fix #1 + #2 sind die wahrscheinlichsten Auslöser für das von dir beobachtete
Muster „Freeze nach längerer Laufzeit / bei Drehen / beim Wiederaufnehmen".

## Geräte-Konfiguration

- **Monochromes Theme**: ausschließliches Schwarz/Weiß/Grau – schwarzer
  Hintergrund (`#000000`), helle Schrift (`#FFFFFF`/`#B0B0B0`), dunkelgraue
  Karten (`#141414`), weiße Buttons mit schwarzer Schrift, grau-skalierte
  Statusfarben. Keine Buntakzente (Violett/Blau/Grün/Orange/Rot entfernt).
- **Monochromes App-Icon**: schwarzer Grund, weiße Pfoten-Silhouette
  (`values/colors.xml`, `values/ic_launcher_background.xml`, `drawable/ic_launcher_foreground.xml`)
- `abiFilters`: `arm64-v8a`, `armeabi-v7a` (Helio G25 ist ARM) – schlanke APK
- `resConfigs("de","en")` – weniger Ressourcen-Auflösung
- `applicationId`: `com.alditalk.panther.x11pro` (Debug: `...x11pro.debug`)
- `versionName`: `1.0-x11pro`
- App-Name: **AT Panther X11Pro**

## Empfohlene Einstellungen auf dem Gerät (Android 12 / Ulefone)

Damit der Monitor zuverlässig läuft:

1. **Einstellungen → Akku → Akku-Optimierung** → AT Panther X11Pro → *Nicht optimieren*
2. **Einstellungen → Apps → AT Panther X11Pro → Akku** → *Uneingeschränkt* + Hintergrundaktivität erlauben
3. In der App: **Batterie-Optimierung ignorieren**-Button nutzen
4. Falls vorhanden (Android 12): Akku → App-Standby deaktivieren für die App
