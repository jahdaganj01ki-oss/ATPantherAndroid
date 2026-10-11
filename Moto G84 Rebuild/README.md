# AT Panther – Moto G84 Rebuild (einseitiges UI, Android 15)

Eigenstaendiges Gradle-Projekt fuer das **Moto G84 mit Android 15**.

Die Basis ist eine **1:1-Kopie** der X11Pro-Variante im Stand
**v1.2-x11pro** – dem letzten Stand, **bevor das UI auf zwei Seiten
geteilt wurde**. Auf dieser Basis kommen die Monitor-Freigabe und die
Android-15-Anpassung dazu.

> **Das UI ist und bleibt EINE Seite:** ein einziger `ScrollView`.
> Kein `ViewPager2`, keine Fragmente, kein Tab-Umschalter.
> (Nachweis: 0 Treffer fuer `ViewPager|Fragment|UiState` im Quellcode.)

## Herkunft des Snapshots

| | |
|---|---|
| Quelle | `Ulefone Power Armor X11Pro/` (Commit `dfa10e3`) |
| Stand | `versionName = 1.2-x11pro`, `versionCode = 3` |
| Abzug | `git archive dfa10e3 "Ulefone Power Armor X11Pro"` |
| Dateien | 43, per SHA256-Abgleich gegen die Quelle verifiziert |

Der 2-Seiten-Split kam mit **v1.3** (`26b3db5`): dort wurde
`activity_main.xml` durch einen `ViewPager2` mit zwei Seiten ersetzt
(Seite 0 = Haupt, Seite 1 = Verlauf) und `MainActivity` bekam
`MainFragment`/`LogFragment`. Davor war alles **eine einzige Seite** –
ein `ScrollView` mit sechs Karten.

## Monitor-Freigabe

Nur **eine** AT-Panther-Instanz darf das ALDI-Talk-Portal abfragen. Das
verhindert, dass mehrere Varianten (Windows, Ulefone, Moto) parallel pollen
und dadurch das Konto gesperrt wird. Umsetzung: zentrale Freigabe-Lease als
Cloudflare Worker + D1, atomar, ohne Server des Nutzers.

Einrichtung Worker/URL: siehe `CLOUDFLARE-WORKER-EINRICHTUNG.md` im Repo-Root.

### Bedienung

Die Freigabe-Karte steht **ganz oben** auf der Seite:

- **Statuszeile** – welche Variante gerade aktiv ist
- **„Hier übernehmen"** – Freigabe auf dieses Gerät holen; die andere
  Variante schaltet sich beim nächsten Check selbst in den Bereitschaftsmodus
- **„Freigeben"** – eigene Freigabe abgeben, damit die andere übernehmen kann
- **„Freigabe-Einstellungen"** – Dialog für Worker-URL, Token und
  Fail-open-Schalter

Startet der Monitor ohne eingetragene URL, startet er **nicht**
(fail-closed).

### Cooldown dieser Variante: 10 s statt 120 s

Nach frischer Übernahme wartet der Dienst hier **10 s**, bevor er das Portal
zum ersten Mal anfasst. Der Worker hätte von sich aus 120 s gesetzt – diese
Variante schickt aber `cooldownMs = 10000` im Request mit. Der Worker wertet
den Wert aus (`clampCooldown`, nur nach oben begrenzt), deshalb war **kein
Redeploy des Workers** nötig. Die übrigen Varianten fragen den Worker ohne
dieses Feld und behalten ihre 120 s.

Festgelegt in `MonitorGate.COOLDOWN_MS`. Wirkt für `evaluate(autoClaim = true)`
und für `claim()`; `/release` ist nicht betroffen.

> **Bewusste Abwägung:** `CHECK_INTERVAL_MS` bleibt bewusst bei 5 min. Das
> alte Gerät erfährt erst bei seinem nächsten echten Worker-Abruf vom Verlust
> der Freigabe und fragt bis dahin weiter (der Cache liefert `allowed` ohne
> Netzabruf). Mit 10 s startet diese Variante also früher in ein Fenster, in
> dem das alte Gerät noch pollen kann.
>
> Für echte Überlappungsfreiheit müsste der Cooldown ≥ `CHECK_INTERVAL_MS`
> sein – dann wartet man aber länger. Wer beides will, senkt stattdessen
> `CHECK_INTERVAL_MS` (z. B. auf 10 s): dann macht die App vor *jedem* Poll
> einen echten Sync, das alte Gerät merkt den Verlust binnen eines
> Intervalls, und 10 s Cooldown werden sicher. Kosten: ~1.440 statt 288
> Worker-Aufrufe pro Tag und Gerät (kostenloses Cloudflare-Limit: 100.000).

### Wichtig: eigene Varianten-ID

`monitor_variant_id = motog84rebuild` – **bewusst nicht** `motog84`.

`MonitorGate` bildet `deviceId = variantId + ANDROID_ID`. Die Worker-Lease
führt genau **eine** Zeile (`id = 1`) über `device_id`. Da `ANDROID_ID` ab
Android 8 pro (Nutzer, Gerät, Signaturschlüssel) gilt, hätten zwei Apps
mit gleicher `variantId` auf demselben, gleich signierten Gerät dieselbe
`deviceId` – beide glaubten Inhaber zu sein und würden parallel pollen.
Genau das verhindert die Freigabe.

## Android 15 (targetSdk / compileSdk 35)

| Punkt | Umsetzung |
|---|---|
| `dataSync` → `specialUse` | Android 15 beendet `dataSync`-Dienste nach **6 h je 24 h** (`ForegroundServiceTimeout`) – der 24/7-Monitor wäre nach 6 h tot. `specialUse` ist ausgenommen. |
| Begründung | `PROPERTY_SPECIAL_USE_FGS_SUBTYPE` im Manifest hinterlegt |
| Kein FGS-Start aus `BOOT_COMPLETED` | Ab API 35 verboten (`ForegroundServiceStartNotAllowedException`). Der Receiver stellt stattdessen nur den AlarmManager-Fallback scharf (`scheduleFallbackAfterBoot`, +60 s). |
| Edge-to-edge | Ab targetSdk 35 Pflicht. Insets werden auf `rootScroll` addiert, sonst läge die Titelzeile unter Statusleiste/Display-Cutout. |
| AGP | 8.5.0 → **8.7.3** (8.5 kann compileSdk 35 nicht); Gradle 8.10 bleibt |
| ABIs | nur `arm64-v8a` (Snapdragon 695 ist 64-Bit-only) |

## Projekt-Identität

| Feld | Wert |
|---|---|
| `applicationId` | `com.alditalk.panther.motog84rebuild` (Debug: `….debug`) |
| `versionName` | `1.7-motog84rebuild` |
| `versionCode` | `2` |
| `namespace` | `com.alditalk.panther` (deshalb war keine Code-Änderung nötig) |
| App-Name | **AT Panther G84 Rebuild** |

Installierbar **parallel** zu allen anderen Varianten:

| Variante | applicationId |
|---|---|
| Original (`app/`) | `com.alditalk.panther` |
| **Moto G84 Rebuild** | `com.alditalk.panther.motog84rebuild` |
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

### GitHub Actions
`.github/workflows/moto-g84-rebuild.yml` baut bei jedem Push auf
`main`/`master`, der den Ordner `Moto G84 Rebuild/` berührt:

| Artefakt | APK-Datei im Archiv |
|---|---|
| `AT-Panther-MotoG84Rebuild-debug-apk` | `AT Panther Moto G84 Rebuild Debug.apk` |
| `AT-Panther-MotoG84Rebuild-release-apk` | `AT Panther Moto G84 Rebuild Release.apk` |

Release wird signiert, sobald `ANDROID_KEYSTORE_BASE64`,
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`
als Repo-Secret gesetzt sind; ohne Secrets wird es unsigniert gebaut.
Manueller Start: **Actions → Moto G84 Rebuild Build → Run workflow**.

## Übernommene Bugfixes (aus `Moto G84 5G`)

- **`AldiTalkApi`**: Das Portal antwortet auf `book1Gb` nicht immer mit JSON –
  bei einem zu schnellen zweiten POST kam ein HTML-Dokument, an dem
  `JSONObject` warf. wird abgefangen. **Bewusst ohne automatisches Retry:**
  Eine HTML-Antwort kann eine *erfolgreiche* Buchung bedeuten; ein Retry
  würde genau das 2. GB doppelt buchen.
- **Schwelle 850 → 950 MB** in `MainActivity`, `MonitorService` und
  `MonitorWakeReceiver`. Alle drei müssen übereinstimmen, sonst startet ein
  Alarm- oder Boot-Neustart mit einer anderen Schwelle, als die App anzeigt.
- `SECOND_BOOKING_DELAY_MS` 3 s → 12 s, dazu `CONFIRM_DELAY_MS` (5 s),
  damit nach einer Buchung nicht sofort der veraltete Portalzustand gemeldet
  wird.

## Einstellungen auf dem Gerät

Damit der Monitor zuverlässig läuft:

1. **Einstellungen → Apps → AT Panther G84 Rebuild → Akku** → *Uneingeschränkt* + Hintergrundaktivität erlauben
2. **Einstellungen → Akku → Akku-Optimierung** → die App → *Nicht optimieren*
3. In der App: **Batterie-Optimierung ignorieren**-Button nutzen
4. Oben auf der Seite die **Freigabe-URL** eintragen, sonst startet der Monitor nicht
5. Akku → App-Standby deaktivieren, falls vorhanden

## Freeze-Fixes (aus v1.2 uebernommen)

| # | Ursache | Fix |
|---|---------|-----|
| 1 | RecyclerView mit `layout_weight` im ScrollView → kein Recycling; bei wachsender Log-Liste werden alle Zeilen auf einmal gemessen/gezeichnet → Freeze | Feste Höhe `260dp` – RecyclerView scrollt intern (`activity_main.xml`) |
| 2 | `notifyDataSetChanged()` bei jedem 60-s-Poll | `ListAdapter` + `DiffUtil` (`MainActivity.kt`) |
| 3 | Log-DB wächst unbegrenzt (~1440 Zeilen/Tag), UI lädt ALLE Einträge | UI lädt max. 200 Zeilen, Service trimmt DB hart auf 5000 |
| 4 | WakeLock lief nach 10 min still aus | WakeLock 30 min + Guard-Job, der alle 9 min verlängert |
| 5 | CoroutineScope im Service nicht geschlossen → Leak bei Stop/Start | Scope als Member, sauber `cancel()` in `onDestroy()` |
| 6 | CookieJar nicht thread-safe → sporadische `ConcurrentModificationException` | `ConcurrentHashMap` + `synchronized` (`OkHttpCookieJar.kt`) |
| 7 | Activity-Rebuild bei Drehung | `launchMode="singleTask"` + `configChanges` |
| 8 | Log-DB ohne Index: `ORDER BY timestamp DESC` sortierte bei jedem Poll bis zu 5000 Zeilen voll | Index auf `LogEntry.timestamp` (DB v2) |
| 9 | DB-Trim bei jedem Poll → Flow-Requery + DiffUtil-Durchlauf | Trim gedrosselt: Alter stündlich, Limit alle ~10 min |
| 10 | SHA-1-PoW-Loop blockierte einen `Dispatchers.IO`-Thread | PoW läuft auf `Dispatchers.Default` |
| 11 | Dutzende `Log.e`-Zeilen pro Login | Trace-Logs auf `Log.d` zurückgestuft |
| 12 | `DefaultItemAnimator` animierte jeden 60-s-Diff | `itemAnimator = null` + `setHasFixedSize(true)` + 20er View-Cache |
| 13 | Log-Export baute bis zu 200 Zeilen auf dem UI-Thread | Kompletter Export auf `Dispatchers.IO` |
| 14 | `configChanges` unvollständig → Drehen rebuildete trotzdem | `smallestScreenSize\|layoutDirection` ergänzt |
| 15 | DB synchron in `Application.onCreate` gebaut | Vorwärmen im Hintergrund-Scope (`PantherApp.kt`) |

## Schutz vor Account-Sperre (Login-Pause)

Scheitern **3 Verbindungs-/Login-Versuche hintereinander** (oder 5
erfolgreiche Re-Logins ohne erfolgreiche Datenafrage), stoppt der Monitor
**vollständig automatisch**:

- ⛔ Hohe Alarm-Benachrichtigung „AT Panther pausiert" – bleibt sichtbar, obwohl der Service gestoppt ist
- Kein automatischer Neustart: Fallback-Wecker abgebrochen, Boot-Start blockiert, Netzwerk-Exceptions zählen jetzt als Fehlversuch
- Nach jedem erfolgreichen Re-Login sofortige erneute Abfrage
- Steht das Volumen auf 0,0 MB (< 0,05 MB), wird nach der ersten 1-GB-Buchung – nach Pause plus frischer Abfrage – ein zweites Mal 1 GB gebucht
- **Fortsetzung nur manuell:** App öffnen → „Start" hebt die Pause auf → nochmal „Start" startet den Monitor

## Geraete-Konfiguration

- **Monochromes Theme**: ausschließlich Schwarz/Weiß/Grau, keine Buntakzente
- `minSdk 24`, `targetSdk 35`, `compileSdk 35`
- `resConfigs("de","en")`