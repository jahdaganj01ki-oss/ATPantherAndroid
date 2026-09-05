# API-CONTRACT — AT Panther Windows-Port

Verbindliche Spezifikation für die Implementierung. Jeder Punkt ist **dem Kotlin-Quellcode
`app/src/main/java/com/alditalk/panther/` (Stand `590e226`) entnommen**, Zeilenbeleg inklusive.
Was hier nicht steht, wird nicht erfunden — Abweichungen landen in `PARITY.md`.

Referenzdateien:

| Kurzname | Datei |
| --- | --- |
| AUTH | `auth/AuthService.kt` |
| API | `api/AldiTalkApi.kt` |
| MON | `service/MonitorService.kt` |
| UI | `MainActivity.kt` |
| CRYPT | `util/CryptoExtensions.kt` |
| PKCE | `util/PkceUtil.kt` |
| JAR | `util/OkHttpCookieJar.kt` |
| DAO | `data/LogDao.kt`, `data/LogEntry.kt` |

---

## 0. Konstanten (AUTH:21–32)

```
PORTAL          = https://www.alditalk-kundenportal.de
AUTH            = https://login.alditalk-kundenbetreuung.de
CLIENT_ID       = U-621-Varnish
REDIRECT_URI    = {PORTAL}/logged-in-home-page/
AUTH_EP         = {AUTH}/signin/json/realms/alditalk/authenticate?authIndexType=service&authIndexValue=Login
UA              = Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36
POW_DIFFICULTY  = 3            (nur Default; real genutzt wird der Server-Wert, siehe 2.2)
JSON_MEDIA      = application/json   (ohne charset)
```

Der UA ist ein Windows-Chrome-UA und wird **unverändert** übernommen.

## 1. HTTP-Client-Setup

### 1.1 Auth-Client (AUTH:53–60)

| Eigenschaft | Wert |
| --- | --- |
| CookieJar | `MemoryCookieJar` (Speicher, siehe 1.3) |
| followRedirects | `false` |
| followSslRedirects | `true` |
| connectTimeout | 30 s |
| readTimeout | 30 s |

### 1.2 API-Client (AUTH:233–236)

`client.newBuilder().followRedirects(true).followSslRedirects(true)` — **teilt sich denselben
CookieJar** wie der Auth-Client (gleiche Instanz, keine neuen Cookies, keine neuen Timeouts).
Er wird erst nach erfolgreicher Redirect-Kette erzeugt.

### 1.3 CookieJar-Semantik (JAR:15–31)

- Ablage **pro Host-Name** (`url.host`), nicht pro Domain.
- `saveFromResponse`: entfernt alle vorhandenen Cookies mit gleichem **Namen** und fügt die neuen hinzu.
- `loadForRequest`: alle Cookies des Hosts, die `cookie.matches(url)` bestehen.
- **Kein Ablauf, kein Purging** — Einträge leben, solange der Client lebt.
- Manuell gesetztes Cookie (AUTH:151–159): `iPlanetDirectoryPro` = `tokenId`, Domain
  `login.alditalk-kundenbetreuung.de`, Path `/`, **nicht** secure, **nicht** hostOnly.

`Cookie.matches(url)` (OkHttp-Semantik, 1:1 nachzubauen):
Domain-Match (case-insensitive; `host == domain` oder `host` endet auf `"." + domain`; bei
host-only exakte Gleichheit), Path-Match (Präfix mit Segmentgrenze, `/` passt immer),
`secure` ⇒ URL muss HTTPS sein.

### 1.4 Wire-Ebene

OkHttp ergänzt selbst `Accept-Encoding: gzip` (und entpackt transparent), `Connection: keep-alive`,
`Host`, `Content-Type`/`Content-Length` bei Body. Header-Reihenfolge im Request: **zuerst die per
`header()` gesetzten in Aufrufreihenfolge, dann Content-Type/Content-Length**.
.NET-Äquivalent: `SocketsHttpHandler{AllowAutoRedirect=false, ConnectTimeout=30s, ReadDataTimeout=30s,
AutomaticDecompression=Gzip}`, `HttpClient.Timeout = Infinite` (OkHttp kennt kein Gesamt-Timeout).
Siehe `PARITY.md` (Header-Reihenfolge, HTTP-Version).

---

## 2. Login-Kette (AUTH:52–244)

Fehlschlag-Pfade liefern immer `LoginResult(success=false, error=…)`; jede Exception wird gefangen
(AUTH:240–243) und als `error = e.message ?: "Unbekannter Fehler"` ausgegeben.

### 2.1 Schritt 1 — Challenge holen (AUTH:68–82)

```
POST {AUTH_EP}
User-Agent: {UA}
Accept-Language: de-DE,de;q=0.9
Accept: application/json
Content-Type: application/json
Body: 0 Bytes (wirklich leer, NICHT "{}")
```

Der leere Body ist dokumentierter Fix (AUTH:64–67): `"{}"` (2 Bytes) bringt ForgeRock dazu, die
PoW-Challenge als JS-Funktion statt `var`-Zuweisung zu liefern — die Regex findet dann nichts.

- HTTP != 2xx ⇒ Fehler `"Step 1 failed: {code}"`.
- Response wird als JSON geparst; **das geparste Objekt bleibt unverändert erhalten** und wird in
  Schritt 2 als Body wiederverwendet (Schlüsselreihenfolge bleibt, Android-`JSONObject` = LinkedHashMap).

### 2.2 Schritt 1b — PoW-Parameter extrahieren (AUTH:84–111)

`callbacks[]` durchlaufen, Typ `TextOutputCallback`, in `output[]` das Element mit
`name == "message"` ⇒ `value` = `powMessage`.

```
Regex 1: var work = "([^"]+)"      → workUuid
Regex 2: var difficulty = (\d+)    → difficulty (Integer)
```

Fehlt einer der Matches ⇒ Fehler `"PoW-Parameter nicht gefunden"`.

### 2.3 Schritt 1c — Nonce suchen (AUTH:43–49)

```
target = "0" * difficulty
for nonce in 0 .. 10_000_000 (inklusive):
    if sha1hex(workUuid + nonce).startsWith(target) -> return nonce
sonst -> RuntimeException("PoW nicht gelöst (10M Versuche)")
```

`sha1hex` (CRYPT:8–11, 20): UTF-8-Bytes → SHA-1 → **Kleinbuchstaben-Hex** (`"%02x"`).

### 2.4 Schritt 2 — Credentials senden (AUTH:114–148)

Im **selben** JSON-Objekt werden in `callbacks[].input[]` gesetzt — **alle Werte als String**:

| `input.name` | Wert |
| --- | --- |
| `IDToken1` | `nonce.toString()` (String!) |
| `IDToken3` | Rufnummer |
| `IDToken4` | Passwort |
| `IDToken5` | `"2"` (String!) |

```
POST {AUTH_EP}
User-Agent: {UA}
Accept: application/json
Content-Type: application/json
Body: das modifizierte JSON, kompakt ohne Leerzeichen (JSONObject.toString())
```

- HTTP != 2xx ⇒ `"Step 2 failed: {code}"`.
- `tokenId = body.optString("tokenId")` — fehlt das Feld ⇒ `""`, kein Exception-Abbruch.
- `tokenId` leer/null ⇒ Fehler `"Login fehlgeschlagen: {body.take(300)}"` (nur 300 Zeichen).
- Sonst Cookie `iPlanetDirectoryPro` = `tokenId` setzen (siehe 1.3).

### 2.5 Schritt 3 — OAuth2 Authorize mit PKCE (AUTH:162–193)

```
GET {AUTH}/signin/oauth2/authorize
    ?client_id=U-621-Varnish
    &response_type=code
    &scope=openid
    &redirect_uri={PORTAL}/logged-in-home-page/
    &code_challenge={challenge}
    &code_challenge_method=S256
    &nonce={nonce}
    &state={state}
    &ui_locales=de
    &acr_values=password
    &prompt=none
    &realm=/alditalk
User-Agent: {UA}
```

**Reihenfolge der Query-Parameter ist verbindlich** (AUTH:167–180). `state` und `nonce` sind
`UUID.randomUUID()` **ohne Bindestriche**, kleingeschrieben (AUTH:164–165).

PKCE (PKCE:6–10, CRYPT:23–28):
- `code_verifier` = 32 Zufallsbytes (`SecureRandom`) → Base64Url **ohne Padding** → 43 Zeichen.
- `code_challenge` = SHA-256(UTF-8-Bytes des Verifiers) → Base64Url ohne Padding.
- Base64Url-Zeichensatz: `-` und `_`, kein `=`.

Kein `Location`-Header ⇒ Fehler `"Kein Location-Header im OAuth-Response"`.

### 2.6 Schritt 4 — Redirect-Kette von Hand (AUTH:195–230)

Start: `nextUrl = Location` von 2.5, `baseUrl = authUrl`.

```
hop = 0
while (nextUrl != null && hop < 8):
    resolved = resolveUrl(nextUrl, baseUrl)
    GET {resolved}   Header: User-Agent: {UA}
    if code in 301..308:
        Location leer -> Fehler "Hop {hop}: kein Location"
        nextUrl = Location
        baseUrl = resolved          // Basis mit jedem Hop aktualisieren!
    else:
        break                       // Kette beendet
    hop++
```

`resolveUrl(rel, base)` (AUTH:251–261):
1. beginnt mit `http://` / `https://` ⇒ unverändert.
2. beginnt mit `//` ⇒ `https://{host(base)}/{rel ohne die beiden Schrägstriche}` —
   **bewusst RFC-widrig**: `//user/…` ist hier ein Pfad, kein protokollrelativer Host (AUTH:246–250).
3. sonst: normale relative Auflösung gegen `base`.

Response-Bodies der Hops werden verworfen (`close()`), Cookies bleiben im Jar.

---

## 3. Business-Endpunkte (API:33–186)

### 3.1 Gemeinsame Header `bffHeaders()` (API:35–40)

In **dieser** Reihenfolge, danach immer `User-Agent` (API:57–58, 102–103, 164–165):

```
Accept: application/json, text/plain, */*
Referer: {PORTAL}/portal/auth/uebersicht/
X-CORRELATION-ID: C_{UUID mit Bindestrichen, kleingeschrieben}
X-TRANSACTION-ID: T_{UUID mit Bindestrichen, kleingeschrieben}
User-Agent: {UA}
```

Beide UUIDs werden **pro Aufruf neu** erzeugt. Authentifikation ausschließlich über Session-Cookies.

### 3.2 `resolveContractId(msisdn)` (API:50–92)

```
GET {PORTAL}/scs/bff/scs-207-customer-master-data-bff/customer-master-data/v1/navigation-list
```

Keine Query-Parameter. Parsing:
- `userDetails.subscriptions[]`; fehlt das Array oder ist es leer ⇒ `null`.
- Bevorzuge Eintrag mit `msisdn == phone` (`optString("msisdn")`, exakter Vergleich) ⇒ `contractId`.
- Sonst Fallback: `subscriptions[0].contractId`.
- `optString` liefert `""` bei fehlendem Feld ⇒ Aufrufer prüft `isNullOrEmpty()`.
- HTTP != 2xx ⇒ `null`; Exception ⇒ `null`.

### 3.3 `getRemainingData(contractId)` (API:95–146)

```
GET {PORTAL}/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offers?contractId={contractId}
```

Der Parameter wird **als Teil der URL-Zeichenkette** gebaut (kein `addQueryParameter`), also ohne
zusätzliche Kodierung durchgereicht.

- HTTP != 2xx ⇒ `null`.
- `subscribedOffers` **muss** ein Array sein (`getJSONArray` wirft sonst ⇒ `null`), leer ⇒ `null`.
- `offer = subscribedOffers[0]`, `pack = offer.pack` (Muss-Array).
- Über `pack[]`: Element mit `balanceAttributeReference == "dataGrantAmount"` ⇒
  `remainingKb = optLong("allocated", 0) - optLong("used", 0)`. Kein Treffer ⇒ `0`.
  Bei mehreren Treffern gewinnt der **letzte**.
- `remainingMb = remainingKb / 1024.0` (Double, MB aus KB).
- Pflichtfelder via `getString` (fehlend ⇒ Exception ⇒ `null`):
  `offerId`, `subscriptionId`, `resourceId`, `onDemandAmountValueUid`, `refillThresholdValueUid`.

### 3.4 `book1Gb(status)` (API:149–186)

```
POST {PORTAL}/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offer/updateUnlimited
Accept / Referer / X-CORRELATION-ID / X-TRANSACTION-ID / User-Agent
Content-Type: application/json
Body (kompakt, Schlüssel in dieser Reihenfolge):
{"offerId":"…","subscriptionId":"…","updateOfferResourceID":"…","amount":"…","refillThresholdValue":"…"}
```

Achtung: `updateOfferResourceID` schreibt sich mit **großem `ID`**. Alle Werte sind Strings
(Quelle: `getString` aus 3.3).

- Response-Body leer ⇒ `"{}"` als Ersatz (API:171).
- `isUpdated = optBoolean("isUpdated", false)`.
- `success = HTTP-2xx && isUpdated`; `statusCode` = HTTP-Code; `message` = **Rohtext der Antwort**.
- Exception ⇒ `BookingResult(false, false, -1, e.message ?: "Unbekannter Fehler")`.
- Ein nicht-2xx-Status ist **kein** Exception-Pfad: die Antwort wird trotzdem geparst.

---

## 4. Monitor-Loop (MON:297–469)

Konstanten (MON:34–57):

```
MAX_CONSECUTIVE_CONNECTION_FAILURES = 3
MAX_RELOGINS_WITHOUT_POLL           = 5
MAX_LOG_ROWS                        = 5000
DEFAULT_THRESHOLD_MB                = 850f
DEFAULT_INTERVAL_SEC                = 60
PREFS_NAME                          = at_panther_monitor_state
PREF_CONNECTION_FAILURES            = consecutive_connection_failures
PREF_PAUSED_AFTER_FAILURES          = paused_after_connection_failures
```

Ablauf:

1. `updateNotification("Anmelde...")` — **Original-Tippfehler**, `broadcastStatus("Anmelden...")`.
2. `performLogin()` = Login (Abschnitt 2) + `resolveContractId(phone)` (3.2); `contractId`
   leer/null ⇒ Misserfolg.
3. Login fehl ⇒ `recordConnectionFailure()`. Bei `>= 3`: Log `⛔ Verbindung pausiert: {n} Fehler —
   bitte Monitor manuell neu starten`, Alarm anzeigen, **dauerhaft pausieren**, Ende.
   Sonst: Log `Login fehlgeschlagen (Verbindungsfehler {n}/3)`, Service stoppen (Fallback-Wecker
   startet im nächsten Intervall neu), Ende.
4. Login ok ⇒ Zähler zurücksetzen, Logs `Login erfolgreich` und `Vertrags-ID erkannt: {id}`.
5. Schleife (`while isRunning`), pro Iteration im `try`:
   1. `deleteOlderThan(now - 7*24*3600*1000)`, `deleteBeyondLimit(5000)`.
   2. `getRemainingData(contractId)`.
   3. **`null`** ⇒ Log/Status `Datenvolumen konnte nicht abgefragt werden — re-login...`, dann
      `performLogin()`:
      - Erfolg: `reloginsWithoutPoll++`. Bei `>= 5`:
        `⛔ {n} Re-Logins ohne erfolgreiche Abfrage — Monitor pausiert, bitte manuell neu starten`,
        dauerhaft pausieren, Ende. Sonst: Verbindungsfehler löschen, `Re-Login erfolgreich`
        (Log + Notification), **Intervall abwarten**, nächste Iteration.
      - Misserfolg: `consecutiveLoginFailures++`, `recordConnectionFailure()`; bei `>= 3`
        dauerhaft pausieren (Text wie in 3.). Sonst Log
        `Re-Login fehlgeschlagen (Versuch {k}; Verbindungsfehler {n}/3)`, Intervall abwarten.
   4. **Erfolg**: `consecutiveLoginFailures = 0`, `reloginsWithoutPoll = 0`, Verbindungsfehler löschen.
      `msg = "Verbleibend: {remainingMb:F1} MB"`.
      - `remainingMb < thresholdMb` ⇒ Log CHECK, dann `Buche 1 GB...` (Notification+Status),
        `book1Gb(status)`, Ergebnis-Log Typ **BOOKING**:
        Erfolg `"✅ 1 GB erfolgreich gebucht"`, sonst
        `"❌ Buchung fehlgeschlagen ({statusCode}): {message.take(100)}"`.
      - sonst ⇒ nur CHECK-Log + Status.
   5. `catch (Exception)`: `recordConnectionFailure()`; bei `>= 3` dauerhaft pausieren; sonst Log
      `Fehler: {e.message.take(80)} (Verbindungsfehler {n}/3)`.
   6. `delay(intervalSec * 1000)`.

**Zählregeln:** Ausnahmen zählen als Verbindungsfehler mit; jeder erfolgreiche Datenabruf löscht den
persistierten Zähler; `recordConnectionFailure()` setzt das Pause-Flag **automatisch**, sobald der
Zähler 3 erreicht (MON:505–512).

**Dauerhafte Pause** (MON:533–538): Fallback-Wecker canceln, Foreground-Notification entfernen,
Alarm-Benachrichtigung (ID 2) zeigen, Service stoppen. Fortsetzung **nur** manuell (UI, 2× Tipp).

**Boot-/Wecker-Neustart** (Receiver:36–63): Bei gesetztem Pause-Flag wird jeder automatische
Neustart übersprungen.

---

## 5. Persistenz

### 5.1 Einstellungen (UI:184–211)

Prefs `at_panther_secure`, Schlüssel `phone`, `password`, `threshold_mb`, `interval_sec` —
**alle als String**, `threshold_mb`/`interval_sec` exakt so, wie der Nutzer sie getippt hat
(kommentierter Grund UI:186–189). Defaults beim ersten Start: `850` und `60`
(`defaultThresholdMb.toInt().toString()`).

### 5.2 Monitor-Zustand (MON:500–521)

Prefs `at_panther_monitor_state`: `consecutive_connection_failures` (Int),
`paused_after_connection_failures` (Boolean). Überlebt App-Neustart.

### 5.3 Log (DAO + LogEntry)

Tabelle `log_entries`: `id` (autoGenerate, PK), `timestamp` (Long, ms), `type`
(`"CHECK"`/`"BOOKING"`), `remainingMb` (Float, Default 0, `-1` = unbekannt), `message` (String).

Abfragen: `getRecent(limit)` = `ORDER BY timestamp DESC LIMIT n` (UI nutzt **200**),
`deleteOlderThan(ts)`, `deleteBeyondLimit(5000)`, `getAll()`, `count()` (die letzten beiden
sind im Android-Code ungenutzt).

---

## 6. UI-Verhalten (UI)

Reihenfolge der Karten (Layout `activity_main.xml`): Titel `AT Panther` → **Login-Daten**
(`Rufnummer (z.B. 491637805298)`, `Passwort` mit Sichtbar-Schalter, Button `Speichern`) →
**Einstellungen** (`Schwelle (MB):`, `Intervall (Sek.):`, rechtsbündige Zahlenfelder) →
**Monitor** (Status `Gestoppt`, `Monitor starten`, `Batterie-Optimierung ignorieren`) →
**Wartung** (`Cache leeren`, `Log exportieren`) → **Verlauf** (Liste, feste Höhe 260 dp).

- `Speichern` ⇒ Toast `Login-Daten und Einstellungen gespeichert`.
- Toggle: läuft der Monitor ⇒ stoppen (`Monitor stoppen` → `Monitor starten`, Status `Gestoppt`);
  sonst starten.
- Start mit aktiver Verbindungspause ⇒ **erster** Tipp hebt nur die Pause auf
  (Toast `⛔ Pause aufgehoben — tippe erneut auf Start, um den Monitor neu zu starten`,
  Status `Pausiert — Start zum Fortsetzen`), **zweiter** Tipp startet.
- Start ohne Rufnummer/Passwort ⇒ Toast `Bitte Rufnummer und Passwort eingeben`.
- Statusanzeige aus Broadcast: `"{status}  ({remaining:F1} MB)"` nur wenn `remaining >= 0`.
- Log-Zeile: `"{dd.MM HH:mm:ss}  {📦|📡}  {message}"`, Monospace, Icon 📦 bei `BOOKING` sonst 📡.
- `Cache leeren` ⇒ Toast `Cache geleert`.
- `Log exportieren` ⇒ Dateidialog `at_panther_log_{yyyyMMdd_HHmmss}.txt`; leerer Verlauf ⇒
  Toast `Kein Log-Verlauf vorhanden`; Format:

```
AT Panther – Log-Export
Erstellt am: {dd.MM.yyyy HH:mm:ss}
Anzahl Einträge: {n}
────────────────────────────────────────
{dd.MM.yyyy HH:mm:ss}  {📦|📡}  {message}  [{remaining:F1} MB]   (MB nur wenn >= 0)
```

Zeilen im Export **älteste zuerst** (`sortedBy { timestamp }`), UTF-8.
- `Batterie-Optimierung ignorieren` ⇒ Systemdialog; bereits gesetzt ⇒ Toast
  `App ist bereits auf der Whitelist`; Fallback-Einstellungen ⇒ Toast
  `Bitte manuell unter Einstellungen > Batterie hinzufügen`.
- Farbschema monochrom (`res/values/colors.xml`): Grund `#000000`, Karten `#141414`,
  Text `#FFFFFF`/`#B0B0B0`, Akzent `#E0E0E0`, Status warn `#D9D9D9`.

## 7. Benachrichtigungen (MON:580–631, strings.xml)

| Kanal | ID | Titel/Text | Priorität |
| --- | --- | --- | --- |
| `at_panther_monitor` „Daten-Monitor“ | 1 | „AT Panther“ + Statustext, ongoing | LOW |
| `at_panther_alerts` „Monitor-Alarme“ | 2 | „AT Panther pausiert“ / „Login/Verbindung 3x fehlgeschlagen — Automatik gestoppt“ | HIGH, BigText, autoCancel |

BigText: „Login/Verbindung ist 3x hintereinander fehlgeschlagen — der Monitor versucht es NICHT
weiter automatisch. Zum Fortsetzen App öffnen und Monitor neu starten.“
