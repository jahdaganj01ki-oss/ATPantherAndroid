# AT Panther — Kompletter Login- / Datenvolumen- / Nachbuchen-Workflow für Windows (.exe)

Stand: 2026-09-13 — Quelle: Android-App in diesem Repo (`app/src/main/...`) + fertiger Windows-Port (`windows/`).
Zweck: **Alles, was nötig ist, um den Workflow 1:1 in einem Windows-Programm (C#, .NET 8, .exe) nachzubauen, sodass es direkt funktioniert.**

Android-Referenzdateien:

- `app/src/main/java/com/alditalk/panther/auth/AuthService.kt` (Login)
- `app/src/main/java/com/alditalk/panther/api/AldiTalkApi.kt` (Abfrage + Buchung)
- `app/src/main/java/com/alditalk/panther/util/CryptoExtensions.kt` + `PkceUtil.kt` (Krypto)
- `app/src/main/java/com/alditalk/panther/util/OkHttpCookieJar.kt` (Cookies)
- `app/src/main/java/com/alditalk/panther/service/MonitorService.kt` (Loop, Schwellen, Pausen-Logik)
- `app/src/main/java/com/alditalk/panther/MainActivity.kt` (Defaults: 850 MB / 60 s)

Fertiger Windows-Port (bereits 1:1 vorhanden, kann direkt gebaut werden):

- `windows/ATPanther.Core/Auth/AuthConfig.cs`
- `windows/ATPanther.Core/Auth/AldiTalkAuthenticator.cs`
- `windows/ATPanther.Core/Auth/PoWSolver.cs`, `PkcePair.cs`
- `windows/ATPanther.Core/Api/AldiTalkApi.cs`
- `windows/ATPanther.Windows/MonitorController.cs`

---

## 1. Konstanten (1:1 übernehmen)

```csharp
PORTAL  = "https://www.alditalk-kundenportal.de"
AUTH    = "https://login.alditalk-kundenbetreuung.de"
CLIENT_ID = "U-621-Varnish"
REDIRECT_URI = "https://www.alditalk-kundenportal.de/logged-in-home-page/"
AUTH_EP = "https://login.alditalk-kundenbetreuung.de/signin/json/realms/alditalk/authenticate?authIndexType=service&authIndexValue=Login"
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

DEFAULT_THRESHOLD_MB = 850.0
DEFAULT_INTERVAL_SEC = 60
MAX_CONSECUTIVE_CONNECTION_FAILURES = 3   // danach dauerhaft pausieren
MAX_RELOGINS_WITHOUT_POLL = 5             // Re-Logins ohne erfolgreiche Abfrage -> pausieren
MAX_LOG_ROWS = 5000
MAX_REDIRECT_HOPS = 8
POW_MAX_NONCE = 10_000_000
TIMEOUT = 30 Sekunden (connect + read)
```

---

## 2. HTTP-Client-Setup (wichtig!)

Es werden **zwei Phasen** mit unterschiedlichem Redirect-Verhalten gebraucht:

```csharp
var cookies = new CookieContainer();

// Phase A: Login / Redirect-Kette — KEINE Auto-Redirects (wie OkHttp followRedirects=false)
var authHandler = new HttpClientHandler {
    AllowAutoRedirect = false,
    UseCookies = true,
    CookieContainer = cookies,
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
};
var authClient = new HttpClient(authHandler) { Timeout = TimeSpan.FromSeconds(30) };
authClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);

// Phase B: API-Calls nach erfolgreichem Login — MIT Auto-Redirects
var apiHandler = new HttpClientHandler {
    AllowAutoRedirect = true,
    UseCookies = true,
    CookieContainer = cookies,   // derselbe Container! Session-Cookies weiterverwenden
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
};
var apiClient = new HttpClient(apiHandler) { Timeout = TimeSpan.FromSeconds(30) };
apiClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
```

Android nutzt dafür einen `MemoryCookieJar` (ConcurrentHashMap). Unter Windows entspricht das `CookieContainer`.

---

## 3. LOGIN — Schritt für Schritt

### Schritt 1: Login-Callbacks holen (PoW-Challenge)

```http
POST {AUTH_EP}
User-Agent: {UA}
Accept: application/json
Accept-Language: de-DE,de;q=0.9
Content-Type: application/json
Body: <0 Bytes, wirklich leer!>
```

**Kritisch:** Der Body muss **0 Bytes** lang sein (in C#: `new ByteArrayContent(Array.Empty<byte>())` + Content-Type `application/json`).
`{}` (2 Bytes) darf NICHT gesendet werden — dann liefert ForgeRock die PoW-Challenge als JavaScript-Funktion statt als `var`-Zuweisung und die Regex findet nichts.

Antwort ist JSON mit `callbacks`-Array. Darin `TextOutputCallback` → `output[]` → Eintrag mit `name == "message"` → `value` = `powMessage` (enthält JS-Code).

PoW-Parameter extrahieren:

```csharp
var workMatch = Regex.Match(powMessage, "var work = \"([^\"]+)\"");
var diffMatch = Regex.Match(powMessage, "var difficulty = (\\d+)");
string workUuid = workMatch.Groups[1].Value;
int difficulty = int.Parse(diffMatch.Groups[1].Value);
// wenn kein Match -> Fehler "PoW-Parameter nicht gefunden"
```

### Schritt 2: Proof-of-Work lösen (SHA-1)

Suche `nonce` (ab 0, max 10 Mio), sodass gilt:

```
SHA1_Hex(workUuid + nonce) beginnt mit "0" * difficulty
```

- `SHA1_Hex` = SHA-1 über UTF-8-Bytes, hex kleingeschrieben (z. B. `Convert.ToHexString(...).ToLowerInvariant()`).
- `workUuid + nonce` = String-Konkatenation, z. B. `"abc123..." + "4711"`.
- Rückgabe als **String** (nicht int!).

```csharp
public static string Solve(string workUuid, int difficulty) {
    var target = new string('0', difficulty);
    for (var nonce = 0; nonce <= 10_000_000; nonce++)
        if (Sha1Hex(workUuid + nonce).StartsWith(target, StringComparison.Ordinal))
            return nonce.ToString();
    throw new InvalidOperationException("PoW nicht gelöst (10M Versuche)");
}
```

### Schritt 3: Credentials einreichen

Das JSON aus Schritt 1 **wiederverwenden** und alle `callbacks[].input[]`-Werte füllen — **alle als String**:

| input-Name | Wert |
|---|---|
| `IDToken1` | `nonce` (PoW-Ergebnis als String) |
| `IDToken3` | Rufnummer (MSISDN, z. B. `0151...`) |
| `IDToken4` | Passwort |
| `IDToken5` | `"2"` (Login-Button) |

```http
POST {AUTH_EP}
User-Agent: {UA}
Accept: application/json
Content-Type: application/json
Body: {das modifizierte JSON aus Schritt 1 als UTF-8}
```

Antwort-JSON muss `tokenId` enthalten. Wenn leer → Login fehlgeschlagen (falsche Daten oder geänderter ForgeRock-Tree; die ersten 300 Zeichen der Antwort als Fehlermeldung zeigen).

Danach Cookie **explizit** setzen (Android macht das manuell, zusätzlich zu automatischen Cookies):

```csharp
cookies.Add(new Uri("https://login.alditalk-kundenbetreuung.de"),
    new Cookie("iPlanetDirectoryPro", tokenId, "/", "login.alditalk-kundenbetreuung.de"));
```

### Schritt 4: OAuth2-Authorize mit PKCE

PKCE erzeugen:

```csharp
// 32 Zufallsbytes -> Base64URL ohne Padding (43 Zeichen) = code_verifier
byte[] rnd = RandomNumberGenerator.GetBytes(32);
string verifier = Convert.ToBase64String(rnd).TrimEnd('=').Replace('+','-').Replace('/','_');
// code_challenge = Base64URL_noPad(SHA256(verifier))
string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)))
    .TrimEnd('=').Replace('+','-').Replace('/','_');
string state = Guid.NewGuid().ToString("N");       // 32 Hex-Zeichen ohne Bindestriche
string nonceParam = Guid.NewGuid().ToString("N");
```

Authorize-URL bauen (alle Werte URL-encodieren):

```http
GET https://login.alditalk-kundenbetreuung.de/signin/oauth2/authorize
  ?client_id=U-621-Varnish
  &response_type=code
  &scope=openid
  &redirect_uri=https%3A%2F%2Fwww.alditalk-kundenportal.de%2Flogged-in-home-page%2F
  &code_challenge={challenge}
  &code_challenge_method=S256
  &nonce={nonceParam}
  &state={state}
  &ui_locales=de
  &acr_values=password
  &prompt=none
  &realm=%2Falditalk
Header: User-Agent: {UA}
```

Der Client folgt **nicht** automatisch (Phase A). Der `Location`-Header der Antwort ist der Einstieg in die Redirect-Kette. Wenn kein `Location`-Header → Fehler `"Kein Location-Header im OAuth-Response"`.

### Schritt 5: Redirect-Kette manuell folgen (max. 8 Hops)

```csharp
string? nextUrl = location;      // aus Schritt 4
string baseUrl = authorizeUrl;   // wird pro Hop aktualisiert!
int hop = 0;
while (nextUrl != null && hop < 8) {
    string resolved = ResolveUrl(nextUrl, baseUrl);
    using var resp = await authClient.GetAsync(resolved);
    int code = (int)resp.StatusCode;
    string? loc = resp.Headers.TryGetValues("Location", out var v) ? v.FirstOrDefault() : null;
    if (code >= 301 && code <= 308) {
        if (string.IsNullOrEmpty(loc)) return Fehler($"Hop {hop}: kein Location");
        nextUrl = loc;
        baseUrl = resolved;   // WICHTIG: Basis für nächsten Hop aktualisieren
    } else break;             // Kette fertig (i. d. R. 200 am Ende)
    hop++;
}
// danach: neuen HttpClient mit AllowAutoRedirect=true + gleichem CookieContainer (Phase B)
```

`ResolveUrl`-Sonderfall (exakt so übernehmen — das ist der häufigste Windows-Port-Bug):

```csharp
static string ResolveUrl(string possiblyRelative, string baseUrl) {
    if (possiblyRelative.StartsWith("http://") || possiblyRelative.StartsWith("https://"))
        return possiblyRelative;
    if (possiblyRelative.StartsWith("//")) {
        // Obwohl RFC-protokoll-relativ, ist 'user' hier ein PFAD-Segment, kein Host!
        // '//user/auth/...' -> 'https://<basis-host>/user/auth/...'
        string host = new Uri(baseUrl).Host;
        return $"https://{host}/{possiblyRelative.Substring(2)}";
    }
    return new Uri(new Uri(baseUrl), possiblyRelative).ToString();
}
```

→ Bei Erfolg: `LoginResult(success=true, apiClient)`. Dieser `apiClient` (mit Session-Cookies) wird für alle BFF-Calls benutzt.

---

## 4. BFF-Header (für alle 3 API-Calls identisch)

```csharp
Accept: application/json, text/plain, */*
Referer: https://www.alditalk-kundenportal.de/portal/auth/uebersicht/
X-CORRELATION-ID: C_{Guid.NewGuid()}   // pro Request neu!
X-TRANSACTION-ID: T_{Guid.NewGuid()}   // pro Request neu!
User-Agent: {UA}
// nur bei POST zusätzlich:
Content-Type: application/json
```

---

## 5. Vertrags-ID ermitteln (resolveContractId)

```http
GET https://www.alditalk-kundenportal.de/scs/bff/scs-207-customer-master-data-bff/customer-master-data/v1/navigation-list
+ BFF-Header
```

- Braucht **keine Parameter** — Auth läuft über Session-Cookies.
- Antwort-JSON: `userDetails.subscriptions[]` mit Feldern `msisdn`, `contractId`.
- Auswahl: **1)** Eintrag, dessen `msisdn` == eingegebene Rufnummer → dessen `contractId`. **2)** Fallback: `subscriptions[0].contractId`.
- Keine Subscriptions oder HTTP-Fehler → `null` (→ Re-Login / Fehlerzähler, siehe §8).

---

## 6. Datenvolumen abfragen (getRemainingData)

```http
GET https://www.alditalk-kundenportal.de/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offers?contractId={contractId (URL-encodiert)}
+ BFF-Header
```

Parsing (`subscribedOffers[0]`):

```
pack[] durchlaufen; dort wo balanceAttributeReference == "dataGrantAmount":
    remainingKb = allocated - used     // beide optLong (fehlend = 0)
remainingMb = remainingKb / 1024.0
```

Gemerkte Felder für die Buchung (alle Pflicht, sonst Fehler):

```
offerId                 = offer["offerId"]
subscriptionId          = offer["subscriptionId"]
resourceId              = offer["resourceId"]
onDemandAmount          = offer["onDemandAmountValueUid"]
refillThreshold         = offer["refillThresholdValueUid"]
```

→ `DataStatus(remainingMb, offerId, subscriptionId, resourceId, onDemandAmount, refillThreshold)`.
Leeres `subscribedOffers` oder HTTP-Fehler → `null` (→ Re-Login-Pfad).

---

## 7. 1 GB nachbuchen (book1Gb)

```http
POST https://www.alditalk-kundenportal.de/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offer/updateUnlimited
+ BFF-Header + Content-Type: application/json
Body:
{
  "offerId":               "{aus §6}",
  "subscriptionId":        "{aus §6}",
  "updateOfferResourceID": "{resourceId aus §6}",
  "amount":                "{onDemandAmount aus §6}",
  "refillThresholdValue":  "{refillThreshold aus §6}"
}
```

Erfolg = `HTTP 2xx UND Antwort-JSON isUpdated == true`.

```csharp
bool isUpdated = JsonNode.Parse(body)?["isUpdated"]?.GetValue<bool>() ?? false;
bool success = response.IsSuccessStatusCode && isUpdated;
```

Antwort-Body (max. 100 Zeichen) bei Misserfolg anzeigen/loggen. Exceptions → `BookingResult(false, false, -1, ex.Message)`.

---

## 8. Monitor-Loop (Automatik: Schwelle → Buchung → Pause-Schutz)

Exakte Android-Logik aus `MonitorService.monitorLoop()`:

```
1. Status "Anmelden..." → performLogin() = Login (§3) + resolveContractId (§5)
   - Fehlschlag: Verbindungsfehlerzähler +1.
     Bei >= 3: dauerhaft PAUSIEREN (kein Auto-Neustart mehr, nur manueller Start).
     Sonst: nach Intervall erneut versuchen.
2. Bei Erfolg: "Login erfolgreich" + "Vertrags-ID erkannt: {id}" loggen, Zähler zurücksetzen.
3. Schleife alle {intervalSec} (Default 60 s):
   a. getRemainingData(contractId) (§6)
   b. null (Session abgelaufen / Netzfehler):
      → Re-Login versuchen.
      → Re-Login OK: reloginsWithoutPoll++. Bei >= 5: PAUSIEREN
        ("N Re-Logins ohne erfolgreiche Abfrage"). Sonst Intervall warten, weiter.
      → Re-Login FAIL: connectionFailures++. Bei >= 3: PAUSIEREN. Sonst Intervall warten.
   c. Erfolg: Zähler zurücksetzen. remainingMb formatiert "%.1f" ("Verbleibend: X.X MB").
      → remainingMb < thresholdMb (Default 850): Status "Buche 1 GB...",
        book1Gb (§7), Ergebnis loggen ("✅ 1 GB erfolgreich gebucht" /
        "❌ Buchung fehlgeschlagen ({code}): {msg[..100]}").
      → sonst nur CHECK-Log + Status.
   d. Exception im Durchlauf: zählt ebenfalls als Verbindungsfehler (+1, bei >= 3 pausieren).
4. Log-Rotation pro Durchlauf: Einträge älter 7 Tage löschen + Tabelle auf 5000 Zeilen deckeln.
```

Pause-Regeln (Account-Sperr-Schutz — unbedingt übernehmen):

- `3` aufeinanderfolgende Verbindungs-/Login-Fehler → Pause.
- `5` erfolgreiche Re-Logins **ohne eine einzige erfolgreiche Abfrage** → Pause.
- Pause-Zustand **persistent** speichern (Android: SharedPreferences `at_panther_monitor_state` mit `consecutive_connection_failures` + `paused_after_connection_failures`; Windows: `%LocalAppData%\ATPanther\monitor_state.json` mit denselben Feldern).
- Während Pause: **keine** weiteren Requests. Erster manueller Start hebt nur die Pause auf (`"⛔ Pause aufgehoben — tippe erneut auf Start"`), erst der zweite startet den Monitor — verhindert versehentliche Sofort-Logins.

Windows-Äquivalente zu Android-Services:

- Android `PARTIAL_WAKE_LOCK` + `AlarmManager`-Fallback → Windows: `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` während der Loop läuft, danach zurücksetzen; Single-Instance-Mutex (`Local\ATPanther_SingleInstance`), damit nicht zwei Prozesse gleichzeitig pollen/buchen.
- Android Room-DB → Windows: `%LocalAppData%\ATPanther\history.log` (Format `yyyy-MM-dd HH:mm:ss|TYP|restMb|nachricht`), UI zeigt nur letzte 200, Export alle.

---

## 9. Windows-.exe bauen (direkt lauffähig)

Voraussetzung: .NET 8 SDK (`dotnet --version` ≥ 8).

```powershell
cd windows
dotnet publish ATPanther.Windows\ATPanther.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
```

Ergebnis: `publish\win-x64\ATPanther.Windows.exe` — per Doppelklick startbar, kein .NET auf dem Ziel-PC nötig (Self-Contained Single-File).

Alternative ohne Single-File:

```powershell
dotnet build ATPanther.Windows\ATPanther.Windows.csproj -c Release
# -> ATPanther.Windows\bin\Release\net8.0-windows\ATPanther.Windows.exe (braucht .NET 8 Runtime)
```

NuGet-Pakete: nur .NET-BCL (`System.Net.Http`, `System.Text.Json`, `System.Security.Cryptography`) — keine externen Abhängigkeiten, kein OkHttp nötig.

Smoke-Test nach dem Bau:

1. `.exe` starten → Rufnummer + Passwort → Start.
2. Diagnose-Log prüfen: `%LocalAppData%\ATPanther\diagnostics.log` (enthält Step1-Callbacks, PoW-Nonce, Redirect-Hops).
3. Erwartete Statusfolge: `Anmelden...` → `Login erfolgreich` → `Vertrags-ID erkannt` → `Verbleibend: X.X MB` → ggf. `Buche 1 GB...` → `✅ 1 GB erfolgreich gebucht`.

---

## 10. Fehler-Tabelle (aus dem Android-Code)

| Symptom | Ursache / Fix |
|---|---|
| `PoW-Parameter nicht gefunden` | Step-1-Body war nicht 0 Bytes ( `{}` gesendet) oder Header `Accept-Language` fehlt → exakt wie §3 senden |
| `Step 1 failed: xxx` | Portal/Auth-Domain down oder blockiert → Status anzeigen, Fehlerzähler +1 |
| `Step 2 failed` / kein `tokenId` | Falsche Nummer/Passwort oder ForgeRock-Tree geändert → ersten 300 Zeichen der Antwort zeigen; Input-Namen (`IDToken1/3/4/5`) prüfen |
| `Kein Location-Header im OAuth-Response` | Authorize-Parameter falsch (client_id, redirect_uri, realm `/alditalk`, `prompt=none`) → Query aus §3 vergleichen |
| `Hop N: kein Location` | `AllowAutoRedirect` war `true` während der Kette oder `ResolveUrl`-Sonderfall `//...` fehlt |
| `navigation-list` ohne Subscriptions | Session-Cookies verloren (falscher CookieContainer) oder Vertrag inaktiv |
| `Keine subscribedOffers` | Prepaid ohne aktives Datenpaket |
| Buchung `isUpdated=false` trotz 200 | Schwelle/UIDs veraltet → `getRemainingData` erneut holen, dann `book1Gb` mit frischen UIDs |
| Endlos-Logins → Account-Sperre | Zähler aus §8 fehlen! Unbedingt 3er- und 5er-Cap + Pause einbauen |

---

## 11. Minimaler C#-Einstieg (Pseudocode, läuft mit obigen Snippets)

```csharp
var auth = new AldiTalkAuthenticator();                       // §3
var login = await auth.LoginAsync(phone, password);
if (!login.Success) { Show(login.Error); return; }

using var api = new AldiTalkApi(login.ApiClient);             // §4–§7
string? contractId = await api.ResolveContractIdAsync(phone); // §5
var status = await api.GetRemainingDataAsync(contractId!);    // §6
if (status != null && status.RemainingMb < thresholdMb) {
    var booking = await api.Book1GbAsync(status);             // §7
    Show(booking.Success ? "✅ 1 GB erfolgreich gebucht" : $"❌ {booking.StatusCode}: {booking.Message}");
}
```

Für die Dauerlösung den Loop aus `windows/ATPanther.Windows/MonitorController.cs` unverändert übernehmen (enthält Zähler, Rotation, Persistenz, Sleep-Lock, Export bereits fertig).
