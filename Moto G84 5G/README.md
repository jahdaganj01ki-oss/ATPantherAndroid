# AT Panther – Moto G84 5G Variante (Motorola)

Eigenstaendiges Gradle-Projekt fuer das **Motorola Moto G84 5G** (Qualcomm Snapdragon 695 5G, 6/8/12 GB RAM, 128/256 GB, 5000 mAh, Android 13 – upgradable auf Android 15, 6,5" 1080x2400 pOLED 120 Hz).

> **Android 15 (targetSdk 35)** – zwei Stolperfallen sind ausdruecklich
> entschaerft:
> 1. **`specialUse` statt `dataSync`**: `dataSync`-Dienste werden auf Android 15
>    nach **6 Stunden je 24 h** hart beendet (`ForegroundServiceTimeout`) – ein
>    24/7-Monitor waere damit nach 6 Stunden tot. `specialUse` ist von dieser
>    Grenze ausgenommen.
> 2. **Kein Dienststart aus `BOOT_COMPLETED`**: Android 15 verbietet das fuer
>    jeden FGS-Typ. `MonitorWakeReceiver` schaltet stattdessen nur noch den
>    AlarmManager-Fallback scharf; der erste Alarm startet den Dienst 60 s
>    nach dem Boot regulaer.
>
> Ausserdem: Edge-to-edge-Insets (ab targetSdk 35 Pflicht) und `arm64-v8a` ohne
> den nutzlosen `armeabi-v7a`-Split.
>
> **Eine Seite statt ViewPager (v1.6)**: Freigabe, Login, Monitor und Verlauf
> stehen untereinander in einem ScrollView. Der Verlauf-RecyclerView hat eine
> **feste Hoehe von 260 dp** – eine Liste mit unbegrenzter Hoehe im ScrollView
> verliert das Recycling und friert die 120-Hz-pOLED ein.

Basierend auf der **Ulefone Power Armor X11Pro**-Variante (Freeze-Fixes, Login-Pause, monochromes Design, ViewPager-UI). Die Moto-G84-Variante nutzt das gleiche
Production-Ready-Build mit folgenden gerätespezifischen Optimierungen:

- **Kein DuraSpeed/MIUI** – Stock-Android/MyUX mit moderater Hintergrund-Begrenzung. WakeLock + AlarmManager-Fallback bleiben aktiv, damit der Foreground-Service
über Doze/Standby überlebt.
- **Leistungs-Headroom**: Snapdragon 695 (6 nm) + 8/12 GB RAM + 120-Hz-pOLED. Die UI (ViewPager2 mit Haupt-/Verlauf-Seite, ListAdapter/DiffUtil, 200-Zeilen-Limit)
läuft butterweich; der 60-s-Poll verursacht keine spürbaren Ruckler.
- **Monochromes Theme** wie in den anderen Varianten: Schwarz/Weiß/Grau, graue Buttons, schwarzer Hintergrund.

Die Variante installiert sich als **eigene App** (`com.alditalk.panther.motog845g`) parallel zur Original-App und zu den anderen Gerätevarianten – kein Update-Konflikt.

## Build

### GitHub Actions
Der Workflow `.github/workflows/motog84.yml` baut bei jedem Push auf `main`/`master`, der den Ordner `Moto G84 5G/` berührt, automatisch ein APK und lädt es als Artifact **AT-Panther-MotoG84-debug-apk** hoch.
Die APK-Datei im Artifact heißt **`AT Panther Moto G84 5G.apk`** (nicht `app-debug.apk`).

Optionales signiertes Release: Repo-Secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` setzen.

### Lokal
```powershell
cd "Moto G84 5G"
.\gradlew.bat assembleDebug
# APK: app\build\outputs\apk\debug\app-debug.apk
```

## Freeze-Fixes (geerbt aus der Ulefone-Variante)

| # | Ursache | Fix |
|---|---------|-----|
| 1 | RecyclerView mit `layout_weight` in ScrollView → kein Recycling | Verlauf als eigene ViewPager-Seite mit `match_parent`-RecyclerView |
| 2 | `notifyDataSetChanged()` bei jedem 60-s-Poll | `ListAdapter` + `DiffUtil` |
| 3 | Log-DB wächst unbegrenzt | UI max. 200 Zeilen, Service trimmt DB auf 5000 |
| 4 | WakeLock lief nach 10 min aus | WakeLock 30 min + Guard-Job alle 9 min |
| 5 | CoroutineScope Leak | Scope als Member, sauber `cancel()` in `onDestroy()` |
| 6 | CookieJar nicht thread-safe | `ConcurrentHashMap` + `synchronized` |
| 7 | Activity-Rebuild bei Drehung | `launchMode="singleTask"` + `configChanges` |
| ... | weitere 8–15 Optimierungen | siehe Ulefone-README |

Die Freeze-Fixes sind besonders relevant für das 120-Hz-pOLED des Moto G84, damit das Wischen zwischen Haupt- und Verlauf-Seite ruckelfrei bleibt.

## Schutz vor Account-Sperre (Login-Pause)

Scheitern **3 Verbindungs-/Login-Versuche hintereinander** (oder 5 Re-Logins ohne erfolgreiche Datenabfrage), stoppt der Monitor automatisch:

- ⛔ Hohe Alarm-Benachrichtigung „AT Panther pausiert" (Kanal „Monitor-Alarme")
- Kein automatischer Neustart mehr (Fallback-Wecker abgebrochen, Boot-Start blockiert)
- Fortsetzung nur manuell: App öffnen → „Start" hebt Pause auf → nochmal „Start" startet Monitor

## Geräte-Konfiguration

- **Monochromes Theme**: Schwarz `#000000`, Text `#FFFFFF`/`#B0B0B0`, Karten `#141414`, Buttons `#9E9E9E`
- **Monochromes App-Icon**: schwarzer Grund, weiße Pfoten-Silhouette
- `abiFilters`: `arm64-v8a`, `armeabi-v7a` (Snapdragon 695 ist 64-Bit-ARM)
- `resConfigs("de","en")`
- `applicationId`: `com.alditalk.panther.motog845g` (Debug: `...motog845g.debug`)
- `versionName`: `1.0-motog845g` (versionCode 1)
- App-Name: **AT Panther Moto G84**

## Empfohlene Einstellungen auf dem Gerät (Motorola MyUX / Android 13)

Motorola nutzt kein aggressives DuraSpeed/MIUI, dennoch empfiehlt sich:

1. **Einstellungen → Apps → AT Panther Moto G84 → Akku** → *Uneingeschränkt* (oder *Nicht optimieren* unter Akku-Optimierung)
2. **Einstellungen → Akku → Adaptive Batterie** → für AT Panther deaktivieren (oder App auf Whitelist)
3. In der App: **Batterie-Optimierung ignorieren**-Button nutzen (öffnet `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`)
4. **Hintergrundaktivität erlauben**
5. Optional: **Automatische App-Bereinigung** in den Motorola-Einstellungen für AT Panther deaktivieren

Aufgrund des 5000-mAh-Akkus und des effizienten Snapdragon 695 ist der Mehraufwand des 60-s-Pollings vernachlässigbar (<1 %/h im Idle).

## Unterschiede zur Ulefone-Variante

- **Kein DuraSpeed-Workaround** nötig, stattdessen Standard-Doze-Handling
- `versionName`/`applicationId` angepasst
- `README` und Manifest-Kommentare auf Moto G84 5G (Android 13, 120 Hz pOLED, Snapdragon 695) angepasst
- Identischer App-Code, identische Freeze-Fixes und Login-Pause-Logik

Weitere Details siehe Root-README und `Ulefone Power Armor X11Pro/README.md`.
