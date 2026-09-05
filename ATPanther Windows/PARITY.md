# PARITY — Android `app/` → `ATPanther Windows/`

Abgleich aller Funktionen der Android-App (Basis `590e226`) mit der Windows-Umsetzung.
Status: `geplant` → `implementiert` → `verifiziert`, oder `Abweichung` (mit Begründung).

Legende-Nachweis: `Datei:Zeile` im Android-Quellpfad `app/src/main/java/com/alditalk/panther/`.

## 1. Netzwerk / Auth

| # | Funktion | Android-Nachweis | Windows-Umsetzung | Status |
| --- | --- | --- | --- | --- |
| 1.1 | Auth-Config-Konstanten | `auth/AuthService.kt:21–32` | `Auth/AuthConfig.cs` | geplant |
| 1.2 | Auth-HttpClient (no redirect, 30 s, CookieJar) | `AuthService.kt:53–60` | `Auth/AuthClients.cs` (`SocketsHttpHandler`) | geplant |
| 1.3 | API-Client teilt CookieJar, folgt Redirects | `AuthService.kt:233–236` | gleiche `MemoryCookieJar`-Instanz | geplant |
| 1.4 | In-Memory-CookieJar (host-keyed, name-ersetzt, kein Ablauf) | `util/OkHttpCookieJar.kt:15–31` | `Auth/MemoryCookieJar.cs` | geplant |
| 1.5 | `Cookie.matches` (Domain/Path/Secure) | OkHttp-Semantik | `Auth/CookieEntry.cs` | geplant |
| 1.6 | Manuell gesetztes `iPlanetDirectoryPro` | `AuthService.kt:151–159` | `jar.SaveFromResponse(AUTH_EP, …)` | geplant |
| 1.7 | Schritt 1: POST mit **0-Byte-Body** | `AuthService.kt:68–81` | `EmptyJsonContent` (Content-Type ohne charset) | geplant |
| 1.8 | Schritt 1b: `TextOutputCallback` → `message` | `AuthService.kt:84–99` | `JsonNode`-Walk | geplant |
| 1.9 | PoW-Regexen `var work` / `var difficulty` | `AuthService.kt:102–104` | `Regex` identisch | geplant |
| 1.10 | PoW-Suche 0…10.000.000, SHA-1 lowercase hex | `AuthService.kt:43–49`, `util/CryptoExtensions.kt:8–20` | `Auth/Pow.cs` (Span, ohne String-Allokation) | geplant |
| 1.11 | Schritt 2: Callback-Inputs, **alle Werte String** | `AuthService.kt:114–129` | `JsonObject`-Mutation, Werte als `JsonNode` String | geplant |
| 1.12 | Schritt 2-Body = modifiziertes Server-JSON | `AuthService.kt:136` | `JsonObject.ToJsonString(relaxed)` | geplant |
| 1.13 | `tokenId` via `optString`, Fehlerpfad mit `take(300)` | `AuthService.kt:143–148` | `OptString`-Helper + `Substring(0,300)` | geplant |
| 1.14 | PKCE S256 (43-Zeichen-Verifier, Base64Url ohne Padding) | `util/PkceUtil.kt`, `CryptoExtensions.kt:23–28` | `Auth/Pkce.cs` | geplant |
| 1.15 | Authorize-URL, Query-Reihenfolge, UUID ohne Bindestriche | `AuthService.kt:164–180` | `Auth/AuthService.cs` (manuell gebaut) | geplant |
| 1.16 | Redirect-Kette max. 8 Hops, `baseUrl` pro Hop | `AuthService.kt:195–230` | `FollowRedirectChainAsync` | geplant |
| 1.17 | `//user/…` als Pfad der Basis-Domain | `AuthService.kt:246–261` | `ResolveUrl` identisch | geplant |
| 1.18 | Fehlerpfade/Exception → `LoginResult(false, msg)` | `AuthService.kt:240–243` | `LoginResult.Failure` | geplant |

## 2. Business-API

| # | Funktion | Android-Nachweis | Windows-Umsetzung | Status |
| --- | --- | --- | --- | --- |
| 2.1 | `bffHeaders()` inkl. Reihenfolge + `User-Agent` zuletzt | `api/AldiTalkApi.kt:35–40,57–58` | `AldiTalkApi.BffHeaders()` | geplant |
| 2.2 | `navigation-list` → `contractId` (msisdn-Präferenz, Fallback [0]) | `AldiTalkApi.kt:50–92` | `ResolveContractIdAsync` | geplant |
| 2.3 | `offers?contractId=` (Parameter in URL-Zeichenkette) | `AldiTalkApi.kt:97–98` | identische String-Konstruktion | geplant |
| 2.4 | `pack[]` → `dataGrantAmount`, `allocated - used` in KB → MB | `AldiTalkApi.kt:121–131` | `GetRemainingDataAsync` | geplant |
| 2.5 | Pflichtfelder via `getString` (fehlend ⇒ `null`) | `AldiTalkApi.kt:134–141` | `RequireString` wirft → `null` | geplant |
| 2.6 | `updateUnlimited`-POST, Schlüsselreihenfolge, `updateOfferResourceID` | `AldiTalkApi.kt:149–168` | `Book1GbAsync` | geplant |
| 2.7 | Erfolg nur bei 2xx **und** `isUpdated` | `AldiTalkApi.kt:170–181` | `BookingResult` | geplant |
| 2.8 | Exception ⇒ `statusCode = -1` | `AldiTalkApi.kt:182–185` | identisch | geplant |

## 3. Monitor / Hintergrund

| # | Funktion | Android-Nachweis | Windows-Umsetzung | Status |
| --- | --- | --- | --- | --- |
| 3.1 | Monitor-Loop mit Intervall-Delay | `service/MonitorService.kt:297–469` | `Monitor/MonitorEngine.cs` | geplant |
| 3.2 | Defaults 850 MB / 60 s | `MonitorService.kt:56–57` | `MonitorDefaults` | geplant |
| 3.3 | 3 Verbindungsfehler ⇒ dauerhafte Pause | `MonitorService.kt:41,312–323,451–460` | `RecordConnectionFailure` | geplant |
| 3.4 | Ausnahmen zählen als Verbindungsfehler | `MonitorService.kt:446–451` | `catch` im Loop | geplant |
| 3.5 | Re-Login-Cap 5 ohne erfolgreiche Abfrage | `MonitorService.kt:47,372–380` | `ReloginsWithoutPoll` | geplant |
| 3.6 | Persistente Pause überlebt Neustart | `MonitorService.kt:500–521` | `Data/MonitorStateStore.cs` (JSON) | geplant |
| 3.7 | AlarmManager-Fallback (Watchdog) | `MonitorService.kt:257–295` | `System.Threading.Timer`-Watchdog | Abweichung (Plattform) |
| 3.8 | Pause-Flag blockiert jeden Auto-Neustart | `service/MonitorWakeReceiver.kt:37–41` | Watchdog prüft Pause-Flag | geplant |
| 3.9 | PARTIAL_WAKE_LOCK + 9-min-Guard | `MonitorService.kt:191–245` | `SetThreadExecutionState(ES_SYSTEM_REQUIRED)` | Abweichung (Plattform) |
| 3.10 | Boot-Completed-Neustart | `MonitorWakeReceiver.kt:25–27`, Manifest:62 | Autostart-Registrierung (Run-Key) | Abweichung (siehe 6.3) |
| 3.11 | Foreground-Notification (ID 1, ongoing, LOW) | `MonitorService.kt:610–631` | `NotifyIcon.Text` + Balloon | Abweichung (Plattform) |
| 3.12 | Alarm-Benachrichtigung (ID 2, HIGH, überlebt Stop) | `MonitorService.kt:540–578` | Balloon `Warning` + dauerhafter Tray-Text | Abweichung (Plattform) |
| 3.13 | Status-Broadcast zur Activity | `MonitorService.kt:633–640`, `MainActivity.kt:93–99` | .NET-Event | geplant |
| 3.14 | Exakte Meldungstexte inkl. Tippfehler „Anmelde...“ | `MonitorService.kt:306` | unverändert übernommen | geplant |
| 3.15 | Log-Cleanup 7 Tage + 5000 Zeilen pro Iteration | `MonitorService.kt:346–352` | `LogStore`-Aufrufe | geplant |
| 3.16 | Buchungstexte ✅/❌ mit `take(100)` | `MonitorService.kt:431–435` | identisch | geplant |

## 4. Datenhaltung

| # | Funktion | Android-Nachweis | Windows-Umsetzung | Status |
| --- | --- | --- | --- | --- |
| 4.1 | Room-Tabelle `log_entries` | `data/LogEntry.kt`, `data/AppDatabase.kt` | `Data/LogStore.cs` (JSON-Datei) | Abweichung (Engine) |
| 4.2 | `getRecent(200)` DESC | `data/LogDao.kt:21–22`, `MainActivity.kt:55,160` | `GetRecent(limit)` | geplant |
| 4.3 | `deleteOlderThan`, `deleteBeyondLimit`, `getAll`, `count` | `LogDao.kt:24–35` | gleiche Signaturen | geplant |
| 4.4 | Einstellungen als Strings (getippter Wert bleibt) | `MainActivity.kt:191–198` | `Data/AppSettings.cs` | geplant |
| 4.5 | `SharedPreferences` = Klartext | `MainActivity.kt:184` | JSON + DPAPI fürs Passwort | **Abweichung (Sicherheit)** |
| 4.6 | Monitor-State-Prefs | `MonitorService.kt:500–519` | `Data/MonitorStateStore.cs` | geplant |

## 5. UI

| # | Funktion | Android-Nachweis | Windows-Umsetzung | Status |
| --- | --- | --- | --- | --- |
| 5.1 | Karten-Reihenfolge + Beschriftungen | `res/layout/activity_main.xml` | `Ui/MainForm.cs` gleiche Reihenfolge | geplant |
| 5.2 | Rufnummer-/Passwortfeld, Passwort-Sichtbarkeit | `activity_main.xml:48–72` | TextBox + Auge-Button | geplant |
| 5.3 | Speichern + Toast-Text | `MainActivity.kt:124–127` | Button + Balloon | geplant |
| 5.4 | Schwelle/Intervall rechtsbündig, `number`-Input | `activity_main.xml:108–159` | TextBox right-aligned, Ziffern-Filter | geplant |
| 5.5 | Statuszeile mit `(x,y MB)` ab `remaining >= 0` | `MainActivity.kt:93–99` | `OnStatus` | geplant |
| 5.6 | Toggle Start/Stop inkl. Beschriftung | `MainActivity.kt:129–136,376–392` | `OnToggle` | geplant |
| 5.7 | Pause aufheben braucht **zwei** Tipps | `MainActivity.kt:339–357` | identisch | geplant |
| 5.8 | Verlauf als Monospace-Liste, feste Höhe | `activity_main.xml:287–290`, `item_log.xml` | ListBox, Consolas, fixe Höhe | geplant |
| 5.9 | Log-Zeilenformat `dd.MM HH:mm:ss  📡/📦  msg` | `MainActivity.kt:402–420` | identisch | geplant |
| 5.10 | Cache leeren + Toast | `MainActivity.kt:139–142,226–241` | Cache-Ordner leeren | geplant |
| 5.11 | Log-Export über Dateidialog, Format, älteste zuerst | `MainActivity.kt:145–148,250–300` | `SaveFileDialog` | geplant |
| 5.12 | Batterie-Whitelist-Dialog + Fallback-Toasts | `MainActivity.kt:151–153,309–335` | Energieoptionen + Keep-Awake | Abweichung (Plattform) |
| 5.13 | Monochromes Dark-Theme | `res/values/colors.xml`, `themes.xml` | `Ui/Theme.cs` gleiche Hexwerte | geplant |
| 5.14 | `singleTask` (eine Instanz) | `AndroidManifest.xml:39` | `Mutex` in `Program.cs` | geplant |
| 5.15 | `configChanges` (kein Neuaufbau bei Drehung) | `AndroidManifest.xml:40` | entfällt (kein Rotations-Rebuild) | Abweichung (plattformlos) |
| 5.16 | POST_NOTIFICATIONS-Permission | `AndroidManifest.xml:7` | entfällt | Abweichung (plattformlos) |

## 6. Dokumentierte Abweichungen

### 6.1 Build-Umgebung
Android: Gradle + AGP, Signierung durch CI. Windows: `dotnet publish -c Release -r win-x64
--self-contained true -p:PublishSingleFile=true` in GitHub Actions. Kein lokaler Build.

### 6.2 Log-Speicher: JSON statt Room/SQLite
Room zieht einen nativen SQLite-Provider in eine Single-File-Exe. Die Windows-Version nutzt eine
atomar geschriebene JSON-Datei mit **identischen Operationen und identischen Löschregeln**
(7 Tage, 5000 Zeilen, `getRecent(200)` DESC). Verhalten nach außen gleich, Speicherformat anders.

### 6.3 Autostart statt BOOT_COMPLETED
Der Android-Boot-Receiver startet den Service **ohne** Credentials-Extras; `MonitorService` bricht
mit leeren Parametern sofort ab (`MonitorService.kt:126–129`) — der Boot-Neustart ist faktisch ein
No-Op. Die Windows-Version bietet daher einen Autostart-Schalter, der **nur das Fenster** startet;
der Monitor beginnt nicht von selbst. Das entspricht dem beobachteten Android-Verhalten.

### 6.4 Passwort nicht im Klartext
Android legt `password` unverschlüsselt in `SharedPreferences` ab. Die Windows-Version verschlüsselt
mit DPAPI (CurrentUser). Grund: Klartext-Passwörter auf der Festplatte wären ein neuer, auf Windows
vermeidbarer Risiko-Zustand. Folge: die Einstellungsdatei ist **nicht** auf einen anderen Rechner
oder ein anderes Benutzerkonto übertragbar.

### 6.5 Keep-Awake statt WakeLock
`SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` verhindert System-Sleep, solange der
Monitor läuft; beim Stoppen und beim Beenden wird der Zustand zurückgesetzt. Ein 9-Minuten-Guard
wie auf Android ist nicht nötig, weil die Flag bis zum `release` bestehen bleibt.

### 6.6 Header-Reihenfolge auf dem Draht
Reihenfolge der per Code gesetzten Header wird beibehalten; transportseitige Header
(`Host`, `Accept-Encoding`, `Connection`, `Content-Length`) ordnet die Runtime. OkHttp verhandelt
HTTP/2 via ALPN, .NET nutzt HTTP/1.1. Falls das Portal darauf reagiert, ist das der erste
Prüfpunkt (siehe `API-CONTRACT.md` 1.4).

### 6.7 Timeout-Semantik
OkHttp: 30 s Connect + 30 s **Read** (pro Datenchunk). .NET: `ConnectTimeout=30s` +
`ReadDataTimeout=30s`, `HttpClient.Timeout = Infinite` — bewusste Wahl, damit die
Gesamtdauer-Kappe von .NET nicht wie ein zusätzlicher Fehler wirkt.

### 6.8 `%.1f` mit Geräte-Lokalisierung
Android formatiert mit Default-Locale (deutsch ⇒ Komma). Windows nutzt
`CultureInfo.CurrentCulture`, auf deutschen Systemen ebenfalls Komma.

## 7. Ungeprüft / Vorbehalte

- Kein Live-Test gegen das ALDI-Talk-Portal aus diesem Repo heraus (Accountdaten gehören nicht ins
  Repo, CI hat keine Secrets). Verifiziert ist Code-Parität gegen `API-CONTRACT.md` und ein grüner
  Build, nicht ein erfolgreicher Login.
- PoW-Nonce-Suche, Redirect-Kette und `isUpdated`-Semantik hängen an der echten Serverantwort.
- `getAll()` und `count()` sind im Android-Code ungenutzt; sie sind implementiert, haben aber kein
  Referenzverhalten.
