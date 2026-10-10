# AT Panther – Redmi Note 9 Pro Variante (Xiaomi)

Eigenstaendiges Gradle-Projekt für das **Xiaomi Redmi Note 9 Pro (global)**
(Qualcomm Snapdragon 720G, 6 GB RAM, 5020 mAh, Android 11 / MIUI –
Build `RKQ1.200826.002`). Basiert auf der X11Pro-Variante (`../Ulefone
Power Armor X11Pro/`) und enthält deren komplette Freeze-Fixes sowie das
monochrome Design – plus MIUI-spezifische Überlebensmaßnahmen (siehe unten).

Die Variante installiert sich als **eigene App**
(`com.alditalk.panther.redminote9pro`) parallel zur Original-App und zur
X11Pro-Variante – kein Update-Konflikt, alle können gleichzeitig installiert
sein.

## Build

### GitHub Actions
Der Workflow `.github/workflows/redmi-note-9-pro.yml` baut bei jedem Push auf
`main`/`master`, der den Ordner `Redmi Note 9 Pro/` berührt, automatisch ein
APK und lädt es als Artifact **AT-Panther-RedmiNote9Pro-debug-apk** hoch.
Die APK-Datei im Artifact heißt **`AT Panther.apk`** (nicht `app-debug.apk`).

Optionales signiertes Release: Repo-Secrets `ANDROID_KEYSTORE_BASE64`,
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` setzen.

### Lokal
```powershell
cd "Redmi Note 9 Pro - Version 1"
.\gradlew.bat assembleDebug
# APK: app\build\outputs\apk\debug\app-debug.apk
```

## Freeze-Fixes (geerbt aus der X11Pro-Variante)

| # | Ursache | Fix |
|---|---------|-----|
| 1 | RecyclerView mit `layout_weight` in ScrollView → kein Recycling; bei wachsender Log-Liste werden alle Zeilen auf einmal gemessen/gezeichnet → Freeze beim Öffnen/Drehen | Feste Höhe `260dp` – RecyclerView scrollt intern, misst nur sichtbare Zeilen (`app/src/main/res/layout/activity_main.xml`) |
| 2 | `notifyDataSetChanged()` bei jedem 60-s-Poll – komplette Liste neu binden | `ListAdapter` + `DiffUtil`: nur neue Zeilen werden gebunden (`MainActivity.kt`) |
| 3 | Log-DB wächst unbegrenzt (~1440 Zeilen/Tag), UI lädt ALLE Einträge | UI lädt max. 200 Zeilen (`getRecent()`), Service trimmt DB hart auf 5000 (`deleteBeyondLimit()`) |
| 4 | WakeLock lief nach 10 min still aus → CPU schläft zwischen Pollings ein | WakeLock 30 min + Guard-Job, der alle 9 min verlängert (`MonitorService.kt`) |
| 5 | CoroutineScope im Service nicht geschlossen → Leak bei Stop/Start-Zyklen | Scope als Member, sauber `cancel()` in `onDestroy()` |
| 6 | CookieJar nicht thread-safe → sporadische `ConcurrentModificationException` im Login-Loop | `ConcurrentHashMap` + `synchronized` (`OkHttpCookieJar.kt`) |
| 7 | Activity-Rebuild bei Drehung → komplette View-Hierarchie + RecyclerView neu aufgebaut | `launchMode="singleTask"` + `configChanges` → App-Start/Drehen/Re-Öffnen re-uses die laufende Activity (`AndroidManifest.xml`) |

## Geräte-Konfiguration

- **Monochromes Theme**: ausschließliches Schwarz/Weiß/Grau – schwarzer
  Hintergrund (`#000000`), helle Schrift (`#FFFFFF`/`#B0B0B0`), dunkelgraue
  Karten (`#141414`), weiße Buttons mit schwarzer Schrift, grau-skalierte
  Statusfarben. Keine Buntakzente.
- **Monochromes App-Icon**: schwarzer Grund, weiße Pfoten-Silhouette
  (`values/colors.xml`, `values/ic_launcher_background.xml`, `drawable/ic_launcher_foreground.xml`)
- `abiFilters`: `arm64-v8a`, `armeabi-v7a` (Snapdragon 720G ist 64-Bit-ARM) – schlanke APK
- `resConfigs("de","en")` – weniger Ressourcen-Auflösung
- `applicationId`: `com.alditalk.panther.redminote9pro` (Debug: `...redminote9pro.debug`)
- `versionName`: `1.0-redmi9pro`
- App-Name: **AT Panther Redmi**

## Empfohlene Einstellungen auf dem Gerät (MIUI / Android 11) ⚠️ WICHTIG

**MIUI killt Hintergrund-Apps deutlich aggressiver als stock Android** – der
Foreground-Service allein reicht auf dem Redmi nicht aus. Ohne die folgenden
Schritte stirbt der Monitor spätestens beim RAM-Aufräumen oder nach
Bildschirmsperre:

1. **Autostart erlauben** (der wichtigste MIUI-Schalter):
   **Einstellungen → Apps → Apps verwalten → AT Panther Redmi** →
   **Autostart** aktivieren. Ohne Autostart startet die App weder nach
   Neustart noch nach System-Kill selbst wieder.
2. **Akku-Restriktion aufheben**:
   **Einstellungen → Apps → Apps verwalten → AT Panther Redmi →
   Akkuspar-Modus → Keine Einschränkungen**.
3. **Kein „Akku sparen"-Modus**: **Einstellungen → Akku & Leistung** →
   Akkuspar-Modus nicht dauerhaft aktiviert lassen (deaktiviert auch die
   obige Freistellung).
4. **Sicherheits-App bereinigen lassen?** Die „Sicherheit"-App von MIUI
   schließt Apps beim Reinigen („Aufräumen"). AT Panther Redmi dort unter
   **Sicherheits-App → Akku → Zahnrad oben rechts → App-Akku-Einstellungen →
   Keine Einschränkungen** freistellen.
5. **App im Recents-Screen „verriegeln"** (MIUI-typisch): App öffnen →
   Recents-Taste (≡) → auf die AT-Panther-Karte lange drücken (oder
   Menü „App sperren") → **Schloss-Icon**. Gesperrte Apps überlebt das
   RAM-Aufräumen („Alles schließen").
6. In der App selbst: **Batterie-Optimierung ignorieren**-Button nutzen
   (Android-Batterie-Optimierung „Nicht optimieren").

Kurz-Check nach Installation: Autostart an → Akku „Keine Einschränkungen"
→ Recents-Karte verriegelt → einmal „Batterie-Optimierung ignorieren"
gedrückt. Dann 60-s-Polling dauerhaft beobachten: Steht der „letzte Check"
still, während die App läuft, hat MIUI den Service stillgelegt – dann
Schritt 1–4 nochmal prüfen.
