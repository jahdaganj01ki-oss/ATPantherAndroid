# Preset: ATPantherAndroid → natives Windows-Programm

Konkrete Fakten aus `github.com/jahdaganj00ki-eng/ATPantherAndroid`, Stand `main` @ `590e226`
(geprüft am 2026-09-05). Alles hier Markierte als *zu verifizieren* vor Verwendung im Code nachlesen.

## Rahmen

| Punkt | Wert |
| --- | --- |
| Lokaler Klon | `C:/Coding/ATPantherAndroid-Local` |
| Port-Quelle | Root-Modul `app/` (Identität `com.alditalk.panther`, v1.1 / versionCode 2) |
| Geräte-Varianten | `Ulefone Power Armor X11Pro/`, `Redmi Note 9 Pro/` — Android-spezifisch, **nicht** portieren |
| Zielordner im Repo | `ATPanther Windows` (mit Leerzeichen — in jeder Pfadangabe quoten) |
| Zielplattform | .NET 8, WinForms, `net8.0-windows`, win-x64 |
| Build | ausschließlich GitHub Actions, kein lokales SDK vorhanden (und nicht gewünscht) |
| Design | monochrom dunkel (Schwarz/Grau/Weiß, helle Schrift) |

Der Code in `app/src` aller drei Varianten ist identisch; nur `app_name` unterscheidet sich.
Portiert wird daher **ein** Windows-Programm.

## Auth-Konfiguration (`auth/AuthService.kt` → `object AuthConfig`) — verifiziert

```
PORTAL        = https://www.alditalk-kundenportal.de
AUTH          = https://login.alditalk-kundenbetreuung.de
CLIENT_ID     = U-621-Varnish
REDIRECT_URI  = {PORTAL}/logged-in-home-page/
AUTH_EP       = {AUTH}/signin/json/realms/alditalk/authenticate?authIndexType=service&authIndexValue=Login
UA            = Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36
POW_DIFFICULTY = 3
```

Der User-Agent ist bereits ein Windows-Chrome-UA — **unverändert übernehmen**, er ist Teil des
vermutlichen Erkennungsmusters des Portals.

## Login-Kette (4 Stufen) — verifiziert, mit den eingebauten Fallen

Client-Setup: `followRedirects(false)`, `followSslRedirects(true)`, Connect- und Read-Timeout je
**30 s**, Cookie-Jar im Speicher.

1. **Challenge holen** — `POST {AUTH_EP}`, Header `User-Agent`, `Accept-Language: de-DE,de;q=0.9`,
   `Accept: application/json`, Content-Type `application/json` und ein **wirklich leerer Body
   (0 Bytes)**. `"{}"` (2 Bytes) bringt ForgeRock dazu, die PoW-Challenge als JavaScript-Funktion
   statt als `var`-Zuweisung zu liefern — dann findet die Regex nichts. Das ist im Quellcode als
   bewusster Fix dokumentiert.
2. **PoW lösen** — `TextOutputCallback` → `output[].name == "message"` → Regex
   `var work = "([^"]+)"` und `var difficulty = (\d+)`. Nonce-Suche `0..10_000_000` bis
   `sha1(work + nonce)` mit `"0" * difficulty` beginnt.
3. **Credentials senden** — dieselben Callbacks, **alle Werte als String**: `IDToken1` = Nonce,
   `IDToken3` = Rufnummer, `IDToken4` = Passwort, `IDToken5` = `"2"`. `POST {AUTH_EP}` mit
   `User-Agent`, `Accept: application/json`, `Content-Type: application/json`. Danach `tokenId`
   prüfen und den Cookie `iPlanetDirectoryPro` manuell setzen: Domain
   `login.alditalk-kundenbetreuung.de`, Path `/`.
4. **OAuth2 + Redirect-Kette** — `GET {AUTH}/signin/oauth2/authorize` mit Query-Parametern **in
   dieser Reihenfolge**: `client_id`, `response_type=code`, `scope=openid`, `redirect_uri`,
   `code_challenge`, `code_challenge_method=S256`, `nonce`, `state`, `ui_locales=de`,
   `acr_values=password`, `prompt=none`, `realm=/alditalk`. `state` und `nonce` sind UUIDs **ohne
   Bindestriche**. Dann Kette von Hand folgen, **max. 8 Hops**, `baseUrl` mit jedem Hop aktualisieren.
   Sonderfall: `//user/...` wird als **Pfad der Basis-Domain** behandelt
   (`https://<basis-host>/user/...`), nicht als protokollrelativer Host — RFC-widrig, aber nötig.
   Zum Schluss einen API-Client mit `followRedirects(true)` ableiten.

## Business-Endpunkte (`api/AldiTalkApi.kt`) — verifiziert

Gemeinsame Header (`bffHeaders()`), in dieser Reihenfolge, plus `User-Agent`:

```
Accept: application/json, text/plain, */*
Referer: {PORTAL}/portal/auth/uebersicht/
X-CORRELATION-ID: C_<UUID>
X-TRANSACTION-ID: T_<UUID>
```

1. `GET {PORTAL}/scs/bff/scs-207-customer-master-data-bff/customer-master-data/v1/navigation-list`
   → `userDetails.subscriptions[]`; bevorzuge Eintrag mit `msisdn == <Rufnummer>`, sonst Erster;
   `contractId` liefern. Keine Query-Parameter, Authentifikation nur über Session-Cookies.
2. `GET {PORTAL}/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offers?contractId={id}`
   → `subscribedOffers[0]`; in `pack[]` das Element mit
   `balanceAttributeReference == "dataGrantAmount"`; Rest = `allocated - used` **in KB**, durch 1024
   → MB. Außerdem merken: `offerId`, `subscriptionId`, `resourceId`,
   `onDemandAmountValueUid`, `refillThresholdValueUid`.
3. `POST {PORTAL}/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offer/updateUnlimited`
   mit zusätzlich `Content-Type: application/json` und Body-Schlüsseln in dieser Reihenfolge:
   `offerId`, `subscriptionId`, `updateOfferResourceID` (Achtung: Großschreibung von `ID`), `amount`,
   `refillThresholdValue`. Erfolg nur, wenn HTTP 2xx **und** `isUpdated == true`.

## Noch zu verifizieren (Phase 1/2 Pflicht)

- `util/CryptoExtensions.kt`: Hex-Ausgabe von `sha1()` klein oder groß, welcher Charset.
- `util/PkceUtil.kt`: Länge/Zeichensatz des `code_verifier`, Base64Url ohne Padding?
- `util/OkHttpCookieJar.kt`: `MemoryCookieJar`-Verhalten (Domain-Matching, Ablauf).
- `service/MonitorService.kt` + `MonitorWakeReceiver.kt`: Prüfintervall, Schwellwerte,
  Buchungsautomatik, Benachrichtigungstexte.
- **Login-Pause** (erinnert, am Code zu bestätigen): nach 3 fehlgeschlagenen
  Verbindungs-/Login-Versuchen automatische Pause mit persistenter Alarm-Benachrichtigung, Kanal
  „Monitor-Alarme"; Ausnahmen zählen mit; Re-Login-Obergrenze 5; manueller Neustart per 2×
  Start-Tipp.
- `data/` (Room: `AppDatabase`, `LogDao`, `LogEntry`): Logformat, Aufbewahrung, Anzeige.
- `MainActivity.kt` + `PantherApp.kt`: UI-Zustände, Felder, Buttons, `singleTask`/`configChanges`.

## Alt-Port: nur Lesematerial

Ein früherer Windows-Port (C#/.NET WinForms, ~2.331 Zeilen) wurde in `d7a836d` gelöscht.
Einzelne Dateien einsehbar ohne Wiederherstellung:

```powershell
git -C 'C:/Coding/ATPantherAndroid-Local' show 'd7a836d^:windows/AT.Panther/AldiTalk/AuthService.cs'
```

Damals enthalten: `AldiTalkApi.cs`, `AuthService.cs`, `AuthConfig.cs`, `Pkce.cs`, `Models.cs`,
`Monitor/MonitorEngine.cs`, `UI/MainForm.cs` (784 Zeilen), `SecureStore.cs`, `LogStore.cs`,
`AppSettings.cs`, `AppPaths.cs`, `Program.cs`, `PublishProfiles/win-x64-portable.pubxml`,
`assets/app.ico`, `tools/generate-icon.mjs`.

Nutzung: Ideen und Solutions-Struktur ansehen. **Nicht** zurückkopieren — der neue Port entsteht aus
`API-CONTRACT.md`. Der Alt-Port kann veraltete Endpunkte oder Header enthalten; er ist Quelle von
Vermutungen, nicht von Wahrheiten.

## Repo-Konventionen

- Workflows liegen in `.github/workflows/` (`android.yml`, `build.yml`, `x11pro.yml`,
  `redmi-note-9-pro.yml`); Artefakt-Namen wie `AT-Panther-debug-apk`, Retention 7–14 Tage,
  Trigger mit `paths:` auf den jeweiligen Ordner beschränkt, `workflow_dispatch` vorhanden.
- Der frühere `windows.yml`-Workflow wurde mit dem Port entfernt; ein neuer Windows-Workflow heißt
  wieder `windows.yml` und folgt dem Muster in `ci-github-actions.md`.
- Git-Identität repo-scoped: `jahdaganj00ki-eng@users.noreply.github.com`.
- `gh`-CLI-Token sieht das private Repo nicht → Push/API über den Credential-Manager-Token,
  Prozedur in `ci-github-actions.md`.
