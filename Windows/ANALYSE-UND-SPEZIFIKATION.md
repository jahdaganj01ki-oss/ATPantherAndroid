# AT Panther – Windows-Port (WPF, .NET 8) – Analyse & Spezifikation

Quelle: `Ulefone Power Armor X11Pro/` (funktionsfaehig, Referenz).
Ziel: `Windows/` 1:1-Verhalten: Login, Abfrage, automatisches Nachbuchen.

## 1. Login-Kette (ForgeRock + PKCE + Redirects)

Konstanten:
- PORTAL = https://www.alditalk-kundenportal.de
- AUTH = https://login.alditalk-kundenbetreuung.de
- AUTH_EP = {AUTH}/signin/json/realms/alditalk/authenticate?authIndexType=service&authIndexValue=Login
- CLIENT_ID = U-621-Varnish
- REDIRECT_URI = {PORTAL}/logged-in-home-page/
- UA = Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36
- POW_DIFFICULTY Default 3

Step 1: POST {AUTH_EP}, Header User-Agent, Accept: application/json, Accept-Language: de-DE,de;q=0.9,
  Body 0 Bytes mit Content-Type application/json (NICHT "{}"!).
  Antwort: JSON callbacks[] -> TextOutputCallback.output[] (name==message) enthaelt
  `var work = "UUID"` und `var difficulty = N`.
  Regex: `var work = "([^"]+)"`, `var difficulty = (\d+)`.

PoW: SHA1(workUuid + nonce) beginnt mit "0"*difficulty, nonce 0..10 Mio, Hex kleingeschrieben.
  Auf Windows in Task.Run (nicht UI blockieren).

Step 2: Gleiches JSON zurueck, input[].value ALLE als String:
  IDToken1=nonce.ToString(), IDToken3=phone, IDToken4=password, IDToken5="2".
  POST gleiche URL. Antwort muss tokenId nicht-leer enthalten.
  Cookie: iPlanetDirectoryPro={tokenId}, Domain login.alditalk-kundenbetreuung.de, Path /.

Step 3+4: PKCE: verifier = 32 Random-Bytes -> Base64Url ohne Padding (43 Zeichen),
  challenge = BASE64URL(SHA256(verifier)).
  GET {AUTH}/signin/oauth2/authorize?client_id=U-621-Varnish&response_type=code&scope=openid
    &redirect_uri={UrlEncode(REDIRECT_URI)}&code_challenge={c}&code_challenge_method=S256
    &nonce={guid}&state={guid}&ui_locales=de&acr_values=password&prompt=none&realm=/alditalk
  mit AllowAutoRedirect=false. Location-Header nehmen, bis 8 Hops manuell folgen.
  ResolveUrl-Fix: "//user/..." -> "https://<basis-host>/user/..." (Pfad, kein Host).
  Relative URLs gegen AKTUELLEN Hop aufloesen. Danach Client mit AllowAutoRedirect=true.

Timeouts: 30s connect/read. CookieContainer (thread-safe).

## 2. API (BFF)

Header je Call: Accept: application/json, text/plain, */*, Referer: {PORTAL}/portal/auth/uebersicht/,
  X-CORRELATION-ID: C_{guid}, X-TRANSACTION-ID: T_{guid}, User-Agent.

- resolveContractId(msisdn): GET {PORTAL}/scs/bff/scs-207-customer-master-data-bff/customer-master-data/v1/navigation-list
  -> userDetails.subscriptions[] {msisdn, contractId}; Auswahl msisdn==phone sonst [0].
- getRemainingData(contractId): GET {PORTAL}/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offers?contractId={id}
  -> subscribedOffers[0].pack[] wo balanceAttributeReference==dataGrantAmount:
     remainingKb = allocated - used; remainingMb = /1024.0.
     Merken: offerId, subscriptionId, resourceId, onDemandAmountValueUid, refillThresholdValueUid.
- book1Gb: POST .../selfcare-dashboard/v1/offer/updateUnlimited
  Body {offerId, subscriptionId, updateOfferResourceID=resourceId, amount=onDemandAmount, refillThresholdValue=refillThreshold}
  Erfolg = HTTP 2xx UND isUpdated==true.

## 3. Monitor-Loop

Defaults: Schwelle 850 MB, Intervall 60s.
Caps: 3 Verbindungsfehler hintereinander ODER 5 Re-Logins ohne erfolgreiche Abfrage -> voll pausieren.
Sonderfall 0,0 MB (<0.05 MB): bei erfolgreicher Erstbuchung -> 3s warten -> frisch abfragen -> 2. Buchung.
Re-Login: sofort erneut abfragen (kein Delay, continue).
Pause: Flag persistent, kein Auto-Restart, Alarm-Toast. Entsperrung manuell: 1. Start hebt Pause auf (KEIN Login), 2. Start startet.
DB: log_entries(id, timestamp indexed, type CHECK/BOOKING, remainingMb, message), UI 200, max 5000, Retention 7 Tage.

## 4. Persistenz Windows

- %AppData%/ATPanther/credentials.dat (DPAPI CurrentUser + JSON: phone, password, threshold, interval)
- %AppData%/ATPanther/monitor_state.json (failures, paused)
- %AppData%/ATPanther/logs.db (SQLite, gleiche Tabelle + Index)
- Export .txt: Header + chronologisch, Format dd.MM.yyyy HH:mm:ss  Icon Nachricht [x.x MB]

## 5. Build via GitHub Actions

Workflow .github/workflows/windows.yml, runs-on windows-latest, setup-dotnet 8.0.x,
restore/build/test/publish win-x64 self-contained single-file, Artifact AT-Panther-Windows-win-x64.
