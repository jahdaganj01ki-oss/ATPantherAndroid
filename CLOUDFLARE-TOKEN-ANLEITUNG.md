# Cloudflare-Token mit den richtigen Berechtigungen erstellen

Anleitung für die Monitor-Freigabe von AT Panther. Ziel: ein API-Token, mit
dem `worker/deploy.ps1` die D1-Datenbank und den Worker anlegt.

**Dauer: ca. 5 Minuten. Konto: kostenlos, keine Zahlung nötig.**

---

## Kurzfassung

| # | Was | Wert |
|---|---|---|
| 1 | Seite öffnen | <https://dash.cloudflare.com/profile/api-tokens> |
| 2 | Klick auf | **Create Token** |
| 3 | Vorlage | **Edit Cloudflare Workers** |
| 4 | Zusätzlich ergänzen | **Account · D1 · Edit** |
| 5 | Account Resources | **Include → Specific account → dein Konto** |
| 6 | Client IP | **leer lassen** |
| 7 | Erstellen, Token kopieren | `cfut_…` |
| 8 | Testen | `curl … /accounts` → Konto muss erscheinen |
| 9 | Deploy | `pwsh -File worker/deploy.ps1 -Token "<TOKEN>"` |

Der wichtigste Stolperstein steht in Schritt 5 – dazu unten mehr.

---

## Warum das so knifflig ist

Ein Cloudflare-Token kann **`active` sein und trotzdem nichts sehen**. Beide
hier getesteten Tokens lieferten auf `/user/tokens/verify` sauber
`"status": "active"` – und sahen dann null Konten:

```jsonc
// Token ist gueltig …
{ "result": { "id": "…", "status": "active" }, "success": true }

// … sieht aber kein einziges Konto
{ "result": [], "success": true }
```

Erst das Konto-Endpunkt zeigt das Problem:

```
GET /accounts/<ACCOUNT-ID>   →  9109 Unauthorized to access requested resource
GET /accounts/<ACCOUNT-ID>/d1/database → 10000 Authentication error
```

**Ursache ist fast immer „Account Resources“:** Ist dort „All accounts“/
„Specific account“ nicht (oder das falsche Konto) gewählt, darf der Token
zwar „User-Read“-Sachen, aber keine Account-Sachen. Deshalb ist der
Selbsttest in Schritt 8 Pflicht, nicht optional.

---

## Schritt für Schritt

### 1. API-Token-Seite öffnen

Im Cloudflare-Dashboard oben rechts auf das **Nutzer-Symbol** klicken →
**My Profile** → Reiter **API Tokens**.

Direktlink: <https://dash.cloudflare.com/profile/api-tokens>

### 2. „Create Token“ klicken

Oben rechts auf der Token-Seite auf **Create Token** klicken. Es öffnet sich
**Use a Template** mit einer Liste fertiger Vorlagen.

### 3. Vorlage „Edit Cloudflare Workers“ wählen

Das ist die Vorlage, die Worker-Skripte hochladen darf. Sie bringt bereits
mit:

- `Account · Workers Scripts · Edit`  ← **das brauchen wir**
- `Account · Workers KV Storage · Edit`
- `Account · Workers R2 Storage · Edit`
- `Account · Account Settings · Read`  ← **das brauchen wir** (Subdomain)
- `User · User Details · Read`
- `User · User API Tokens · Read`

**D1 fehlt in dieser Vorlage** – das kommt in Schritt 4 dazu.

Unter der Vorlagenliste gibt es **Create Custom Token**. Wer lieber direkt
alles selbst zusammenklickt, nimmt den Weg in Schritt 3b.

### 4. D1-Berechtigung ergänzen

Unter der gewählten Vorlage auf **Edit Permissions** klicken. Dort stehen
die Zeilen der Vorlage. Eine neue Zeile hinzufügen:

| Spalte | Wert |
|---|---|
| 1. Spalte (Bereich) | **Account** |
| 2. Spalte (Gruppe) | **D1** |
| 3. Spalte (Zugriff) | **Edit** |

> Steht **D1** nicht zur Auswahl, ist D1 für dieses Konto nicht verfügbar.
> Siehe „Wenn es hakt“ am Ende.

### 4b. Alternative: komplett selbst zusammenklicken

Statt der Vorlage auf **Create Custom Token** klicken und genau diese drei
Zeilen anlegen:

| Bereich | Gruppe | Zugriff | Zweck |
|---|---|---|---|
| Account | **D1** | **Edit** | Datenbank anlegen, Schema ausführen |
| Account | **Workers Scripts** | **Edit** | Worker hochladen |
| Account | **Account Settings** | **Read** | `workers.dev`-Subdomain verwalten |

Mehr ist nicht nötig. Weniger führt zu den Fehlern unten.

### 5. Account Resources prüfen ← wichtigster Schritt

Im Abschnitt **Account Resources** steht ein Feld mit:

```
Include ▾   Specific account ▾   [ Konto auswählen ]
```

Dort muss das Konto stehen, das zur Konto-ID
`dc36919cd74227d24176872483647bf1` gehört. Steht dort „All accounts“, passt
das auch – „Specific account“ ist nur die engere Variante.

**So kommst du an die Konto-ID:** Dashboard-Startseite → oben der
Kontoname → rechts in der Seitenleiste bzw. unter **Account Home** steht die
**Account ID**. Sie muss mit `dc36919cd74227d24176872483647bf1` übereinstimmen.
Ist sie eine andere, läuft alles auf einem anderen Konto – dann stimmt die
oben angegebene ID nicht und es muss der andere Wert verwendet werden.

Feld **Zone Resources** darunter kann leer bleiben – AT Panther braucht
keine Zone, nur den Account.

### 6. Optionale Felder

- **Client IP address filtering:** **leer lassen**. Ein Filter auf eine
  IP würde das Token unbrauchbar machen, sobald man das WLAN wechselt.
- **TTL (Gültigkeit):** ebenfalls leer lassen (= unbefristet). Läuft der
  Token ab, funktioniert das Monitoring still nicht mehr – bei einer
  persönlichen Freigabe ist ein unbefristeter Token mit den drei
  Berechtigungen oben das vernünftigste.
- **Token name:** z. B. `AT Panther Monitor-Freigabe`.

### 7. Token erstellen und kopieren

Auf **Continue to summary** → **Create Token** klicken. Der Token wird
**einmalig** angezeigt und lässt sich danach nicht mehr auslesen – also
jetzt kopieren. Er sieht so aus:

```
cfut_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
```

### 8. Testen, bevor du ihn benutzt

**In PowerShell** (das ist deine Umgebung):

```powershell
curl.exe -s -H "Authorization: Bearer <TOKEN>" https://api.cloudflare.com/client/v4/accounts
```

**Richtig** – das Konto erscheint:

```json
{"success":true,"errors":[],"messages":[],"result":[
  {"id":"dc36919cd74227d24176872483647bf1","name":"<Dein Konto>"}]}
```

**Falsch** – leere Liste, Token ist unbrauchbar:

```json
{"success":true,...,"result":[]}
```

Zusatzprüfung, ob wirklich D1-Rechte drinstecken:

```powershell
curl.exe -s -o NUL -w "D1-Status: %{http_code}`n" `
  -H "Authorization: Bearer <TOKEN>" `
  https://api.cloudflare.com/client/v4/accounts/dc36919cd74227d24176872483647bf1/d1/database
```

`200` = gut. `401`/`403` oder ein JSON mit `"Authentication error"` =
D1-Berechtigung fehlt noch.

### 9. Deploy ausführen

```powershell
cd C:\Coding\ATPantherAndroid\worker
pwsh -File .\deploy.ps1 -Token "<TOKEN>" -AccountId "dc36919cd74227d24176872483647bf1"
```

Das Skript erledigt alles der Reihe nach und bricht bei jedem Problem mit
einer klaren Meldung ab:

1. Zugang prüfen (erkennt fehlende Permissions, Schritt 5)
2. D1-Datenbank anlegen bzw. finden
3. `schema.sql` ausführen
4. Worker hochladen
5. `workers.dev`-Subdomain aktivieren
6. Smoke-Test – inklusive der Probe, dass zwei Geräte **nie** gleichzeitig
   Inhaber werden

Am Ende steht die URL, zum Beispiel:

```
https://at-panther-lock.<subdomain>.workers.dev
```

### 10. In den Apps eintragen

Diese URL in **jeder** verwendeten Variante unter *Monitor-Freigabe*
eintragen und speichern – danach einmal auf **Übernehmen** tippen (Vorgabe:
Windows). Erst dann fragt genau ein Gerät das Portal ab, die anderen gehen
in den Bereitschaftsmodus.

---

## Wenn es hakt

| Meldung | Ursache | Behebung |
|---|---|---|
| `/accounts` liefert `[]` | Token sieht kein Konto | Schritt 5: Account Resources prüfen |
| `9109 Unauthorized to access requested resource` | Falsches Konto oder keine Account-Permission | Konto-ID prüfen, Permissions ergänzen |
| `10000 Authentication error` bei `/d1/database` | `D1: Edit` fehlt | Schritt 4 |
| `workers.dev`-Subdomain schlägt fehl | `Account Settings: Read` fehlt | Schritt 4b |
| `D1` nicht in der Permission-Liste | D1 für dieses Konto nicht verfügbar | Siehe unten |
| Script bricht mit „Der Token sieht KEIN Konto" ab | wie Zeile 1 | wie Zeile 1 |

### Alternative ganz ohne Token: Wrangler

Wenn die Token-Erstellung nicht klappt, geht es auch ohne API-Token –
Wrangler meldet sich interaktiv im Browser an:

```powershell
cd C:\Coding\ATPantherAndroid\worker
npm install
npx wrangler login                       # öffnet den Browser
npx wrangler d1 create at-panther-lock   # database_id notieren
# database_id in wrangler.toml eintragen
npx wrangler d1 execute at-panther-lock --remote --file=schema.sql
npx wrangler deploy
```

Der Vorteil: kein Token, keine Permissions-Probleme. Der Nachteil: der
Deploy läuft am Rechner, nicht aus CI heraus.

### Wenn D1 für das Konto nicht verfügbar ist

Sollte die D1-Zeile in der Permission-Liste fehlen, gibt es einen kostenlosen
Ersatz ohne Datenbank: eine einzelne JSON-Datei im Repo, gelesen über
`raw.githubusercontent.com` (das Repo ist öffentlich). Der Worker entfällt
dann komplett, dafür ist die Übernahme nicht mehr atomar – das Umschalten
muss über einen GitHub-Klick laufen. Sag Bescheid, dann baue ich das um.

---

## Sicherheit

- **Token nicht ins Repo committen.** Er gehört in keine Datei, auch nicht in
  eine `.env`, die du committest.
- **Token nach dem Deploy widerrufen.** Ist er einmal im Chat, im Verlauf
  oder im Terminal-Mitschnitt gelandet, gilt er als bekannt. In der
  Token-Liste auf **Roll** klicken und einen neuen erstellen, falls er
  erneut gebraucht wird. Die Freigabe selbst funktioniert danach weiter –
  der Worker braucht den Token zur Laufzeit nicht.
- **Was im Worker steht, ist harmlos:** ein Variantenname, eine Geräte-ID und
  Zeitstempel. Keine Rufnummer, kein Passwort.
- **Optionaler Extra-Schutz:** `npx wrangler secret put LOCK_TOKEN` setzt
  ein Secret, das jeder Client mitsenden muss. Der Token steht dann in den
  Apps im Feld „Token“. Für den reinen Privatbetrieb nicht nötig.
- **Die beiden Tokens aus diesem Chat bitte löschen** – sie haben zwar kein
  Konto gesehen, gehören aber trotzdem nicht in einen Chatverlauf.

---

## Kurzreferenz

| Begriff | Bedeutung |
|---|---|
| Account ID | `dc36919cd74227d24176872483647bf1` |
| D1 | kostenlose SQL-Datenbank in Cloudflare (SQLite) |
| Workers | kostenlose Script-Ausführung, hier die Freigabe-Logik |
| `workers.dev` | kostenlose Subdomain für den Worker |
| `Account Resources` | Feld, das den Token an ein Konto bindet – häufigste Fehlerquelle |
