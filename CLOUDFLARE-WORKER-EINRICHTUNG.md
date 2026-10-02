# Cloudflare Worker einrichten — Monitor-Freigabe für AT Panther

Vollständige Schritt-für-Schritt-Anleitung, von einem leeren Cloudflare-Konto
bis zu einer fertig laufenden Freigabe, die **genau einer** deiner beiden
Varianten erlaubt, das ALDI-Talk-Portal abzufragen.

| | |
|---|---|
| **Was du brauchst** | ca. **15 Minuten**, ein kostenloses Cloudflare-Konto, 0 € |
| **Ziel** | `https://at-panther-lock.<subdomain>.workers.dev` |
| **Zahlung** | **0 €** — keine Kreditkarte nötig |
| **Voraussetzung** | Die Apps sind gebaut (GitHub Actions, Kapitel 11) |

---

## Inhalt

1. [Warum das überhaupt nötig ist](#1-warum-das-überhaupt-nötig-ist)
2. [Wie die Freigabe funktioniert](#2-wie-die-freigabe-funktioniert)
3. [Voraussetzungen](#3-voraussetzungen)
4. [Cloudflare-Konto anlegen](#4-cloudflare-konto-anlegen)
5. [API-Token erstellen](#5-api-token-erstellen)
6. [Deploy Variante A — Skript](#6-deploy-variante-a--skript-empfohlen)
7. [Deploy Variante B — Wrangler](#7-deploy-variante-b--wrangler-ohne-token)
8. [Cooldown nachrüsten](#8-cooldown-nachrüsten-bei-bestehender-installation)
9. [Verifikation](#9-verifikation-beweisen-dass-nur-einer-fragt)
10. [URL in beiden Apps eintragen](#10-url-in-beiden-apps-eintragen)
11. [Täglicher Betrieb](#11-täglicher-betrieb)
12. [Cooldown erklärt](#12-die-sperrzeit-nach-dem-umschalten)
13. [Optional: LOCK_TOKEN](#13-optional-lock_token-setzen)
14. [Wenn etwas nicht klappt](#14-wenn-etwas-nicht-klappt)
<a name="1-warum-das-überhaupt-nötig-ist"></a>
## 1. Warum das überhaupt nötig ist

Du nutzt zwei Varianten mit **demselben** ALDI-Talk-Login:

- 📱 **Moto G84 5G** (Handy, Android 15)
- 💻 **Windows 10 Home** (Laptop)

Beide prüfen alle 60 Sekunden das Portal. Laufen beide gleichzeitig, passiert
das:

| Ohne Freigabe | Mit Freigabe |
|---|---|
| 2 parallele Logins alle 60 s | 1 Login alle 60 s |
| Doppelte Login-Ketten (PoW usw.) | einmalige Belastung |
| **Risiko: Kontosperre** | kein Sperr-Risiko |

Die Freigabe ist eine einfache **Leihgabe („Lease“)**: Nur wer sie gerade hält,
darf das Portal anfragen. Die anderen laufen sichtbar im **Bereitschaftsmodus**.

> **Wichtig:** Es werden **keine Zugangsdaten** gespeichert. In der Datenbank
> stehen nur ein Variantenname, eine Geräte-ID und Zeitstempel.

<a name="2-wie-die-freigabe-funktioniert"></a>
## 2. Wie die Freigabe funktioniert

```
   Moto G84 5G ─┐
                ├──▶  POST /sync  ──▶  Cloudflare Worker ──▶  D1
   Windows 10  ─┘        (alle 5 min)        │                  │
                                            │            genau EINE Lease
                                            │            (id = 1)
                                            ▼
                                   nur der Inhaber darf pollen
```

**Kernregeln**

| Regel | Bedeutung |
|---|---|
| **Atomar** | Das Übernehmen passiert in einem bedingten `UPSERT`. Zwei Geräte können **nie** gleichzeitig Inhaber werden. |
| **Selbstheilend** | Der Inhaber verlängert alle 5 min (TTL 15 min). Fällt er aus, läuft die Lease ab und ein anderes Gerät darf übernehmen. |
| **Sichtbar** | Beide Apps zeigen jederzeit an, welche Variante aktiv ist. |
| **Umschaltbar** | Ein Tipp auf „Übernehmen" — die andere Variante geht automatisch in Bereitschaft. |
| **Sperrzeit** | Nach dem Umschalten wartet die neue Variante 120 s, bevor sie das Portal zum ersten Mal anfasst. |
| **Fail-closed** | Server nicht erreichbar = **nicht** abfragen. Doppelt gefährlich ist doppeltes Abfragen. |

**Vier Endpunkte**

| Endpunkt | Zweck |
|---|---|
| `GET /health` | Erreichbarkeit prüfen |
| `GET /state` | aktueller Stand, ohne Änderung |
| `POST /sync` | verlängern / übernehmen (der Normalfall) |
| `POST /release` | eigene Freigabe abgeben |

<a name="3-voraussetzungen"></a>
## 3. Voraussetzungen

- [ ] **Cloudflare-Konto** (kostenlos, Schritt 4)
- [ ] **API-Token** mit den richtigen Rechten (Schritt 5) — *oder* Wrangler (Schritt 7)
- [ ] **PowerShell 7** (`pwsh`) für Variante A — *oder* **Node.js 20+** für Variante B
- [ ] Der Ordner `worker/` aus dem Repository

> 💡 Die **Account-ID** hat das Deploy-Skript bereits als Vorgabe eingetragen.
> Du musst sie nicht suchen — nur kontrollieren, dass sie zu **deinem** Konto gehört.

15. [Kosten und Kontingente](#15-kosten-und-kontingente)
<a name="4-cloudflare-konto-anlegen"></a>
## 4. Cloudflare-Konto anlegen

1. Öffne <https://dash.cloudflare.com/sign-up>
2. E-Mail und Passwort eingeben, **Weiter**
3. **Kostenlosen Tarif** („Free") wählen — **keine** Kreditkarte nötig
4. Bestätigungsmail öffnen und Adresse bestätigen
5. Du bist im Dashboard eingeloggt

**Account-ID auslesen** (für Schritt 6 nötig):

- Nutzer-Symbol oben rechts → **My Profile** → **API Tokens**
- Direkt: <https://dash.cloudflare.com/profile/api-tokens>
- Ganz rechts steht die **Account ID** — 32 Hex-Zeichen

Notiere sie, z. B. `dc36919cd74227d24176872483647bf1`.

<a name="5-api-token-erstellen"></a>
## 5. API-Token erstellen

> ⚠️ **Der häufigste Fehler in diesem ganzen Setup.** Ein Token kann „aktiv"
> sein und trotzdem kein einziges Konto sehen. Deshalb ist der Selbsttest in
> **5.8 Pflicht**, nicht optional.

### 5.1 Token-Seite öffnen
Nutzer-Symbol → **My Profile** → Reiter **API Tokens**
(<https://dash.cloudflare.com/profile/api-tokens>).

### 5.2 „Create Token"
Oben rechts → **Create Token** → „Use a Template" erscheint.

### 5.3 Vorlage wählen
**Edit Cloudflare Workers**. Sie bringt mit:

- `Account · Workers Scripts · Edit` ← **gebraucht**
- `Account · Account Settings · Read` ← **gebraucht** (Subdomain)
- `Account · Workers KV Storage · Edit`
- `Account · Workers R2 Storage · Edit`
- `User · User Details · Read`

**D1 fehlt hier** — kommt in 5.4 dazu.

### 5.4 D1-Berechtigung ergänzen
Auf **Edit Permissions** klicken, dann eine Zeile hinzufügen:

| Spalte | Wert |
|---|---|
| Bereich | **Account** |
| Gruppe | **D1** |
| Zugriff | **Edit** |

> Steht **D1** nicht zur Auswahl, ist D1 für dieses Konto nicht verfügbar.

### 5.5 Resources festlegen (der Klassiker)
Scrolle zu **Account Resources**:

- **Include** → **Specific account** → **dein Konto** wählen

> ❗ Steht dort „All accounts" oder das **falsche** Konto, darf der Token
> User-Sachen lesen aber keine Account-Sachen. Genau das erzeugt später `[]`.

### 5.6 Client-IP
**Leer lassen** (wird von zu vielen Netzen genutzt).

### 5.7 Erstellen
**Continue to summary** → **Create Token**.

Der Token wird **einmalig** angezeigt und beginnt mit `cfut_`. Sofort kopieren.

### 5.8 Selbsttest (nicht überspringen!)

```powershell
$TOKEN = "cfut_DEIN_TOKEN"
curl.exe -H "Authorization: Bearer $TOKEN" `
  "https://api.cloudflare.com/client/v4/accounts"
```

**✅ Richtig** — dein Konto erscheint:

```json
{ "success": true, "result": [ { "id": "dc36919...", "name": "Dein Name" } ] }
```

**❌ Falsch** — leere Liste:

```json
{ "success": true, "result": [] }
```

<a name="6-deploy-variante-a--skript-empfohlen"></a>
## 6. Deploy Variante A — Skript (empfohlen)

Ein Durchgang erledigt alles: Datenbank, Schema, Worker, Subdomain, Test.

### 6.1 Ausführen

```powershell
cd C:\Pfad\zu\ATPantherAndroid\worker
pwsh -File .\deploy.ps1 -Token "cfut_DEIN_TOKEN"
```

Weicht deine Account-ID vom Standard ab:

```powershell
pwsh -File .\deploy.ps1 -Token "cfut_DEIN_TOKEN" -AccountId "deine-account-id"
```

### 6.2 Erwartete Ausgabe

```
== 1) Zugang pruefen
   Token aktiv.
   Konto: Dein Name

== 2) D1-Datenbank
   gefunden: at-panther-lock (id: xxxxxxxx-…)
   schema.sql angewendet.

== 3) Worker-Script hochladen
   hochgeladen: at-panther-lock

== 4) workers.dev-Subdomain
   Subdomain: atp-lock-12345

== 5) workers.dev-Subdomain
   URL: https://at-panther-lock.atp-lock-12345.workers.dev

== 6) Smoke-Test
   /health ok
   /state: owner=
   A (windows) hat die Freigabe: windows
   B (ulefone) abgewiesen: windows fragt ab
   B hat uebernommen: ulefone
   Freigabe wieder frei.

FERTIG - diese URL in den Apps eintragen (Monitor-Freigabe):
  https://at-panther-lock.atp-lock-12345.workers.dev
```

> 🔒 Das Skript prüft zum Schluss selbst, dass **zwei Geräte nie gleichzeitig
> Inhaber werden**. Bricht es genau dort ab, stimmt die Datenbank nicht.

### 6.3 URL notieren

```
https://at-panther-lock.atp-lock-12345.workers.dev
```

Diese URL brauchst du in Kapitel 10.

> Läuft dein Worker **ohne** `not_before`-Spalte (Bestandsinstallation), kurz
> zu [Kapitel 8](#8-cooldown-nachrüsten-bei-bestehender-installation).

<a name="7-deploy-variante-b--wrangler-ohne-token"></a>
## 7. Deploy Variante B — Wrangler (ohne Token)

Wenn die Token-Erstellung nicht klappt: Wrangler meldet sich interaktiv an.

```bash
cd worker
npm install                      # installiert nur die wrangler-CLI
npx wrangler login              # öffnet den Browser

npx wrangler d1 create at-panther-lock
# -> database_id notieren

# database_id in wrangler.toml unter [[d1_databases]] eintragen

npx wrangler d1 execute at-panther-lock --remote --file=schema.sql
npx wrangler deploy
```

`wrangler deploy` schreibt die URL in die Konsole:

```
https://at-panther-lock.<deine-subdomain>.workers.dev
```

| | Variante A (Skript) | Variante B (Wrangler) |
|---|---|---|
| Token nötig | ja | **nein** |
| Ein Durchgang | ja | nein, 5 Befehle |
| Smoke-Test inklusive | ja | manuell (Kapitel 9) |
| Wiederholbar | gut | mittel |

<a name="8-cooldown-nachrüsten-bei-bestehender-installation"></a>
## 8. Cooldown nachrüsten (Bestandsinstallation)

Falls dein Worker **vor** dem Cooldown-Feature deployed wurde, fehlt die Spalte
`not_before`. Das Skript aus Kapitel 6 legt sie bei **neuen** Datenbanken
automatisch an — eine alte braucht einen Nachtrag.

### 8.1 Prüfen, ob die Spalte fehlt

```bash
npx wrangler d1 execute at-panther-lock --remote \
  --command "SELECT name FROM pragma_table_info('monitor_lock') WHERE name='not_before'"
```

| Ergebnis | Bedeutung |
|---|---|
| Eine Zeile (`not_before`) | ✅ vorhanden → **nichts tun** |
| „No results" / leer | ❌ fehlt → Schritt 8.2 |

### 8.2 Nachrüsten

```bash
npx wrangler d1 execute at-panther-lock --remote \
  --file=migrations/001_add_not_before.sql
```

Erwartet:

```
🌀 Executing on remote D1 database at-panther-lock.
🌀 Executing 1 statements on at-panther-lock.
🌀 Success.
```

### 8.3 Gegenprobe

```bash
npx wrangler d1 execute at-panther-lock --remote \
  --command "SELECT name FROM pragma_table_info('monitor_lock')"
```

Jetzt müssen **alle sieben** Spalten da sein:

```
name          type      notnull  dflt_value  pk
id            INTEGER   1        NULL        1
owner         TEXT      1        NULL        0
device_id     TEXT      1        NULL        0
acquired_at   INTEGER   1        NULL        0
expires_at    INTEGER   1        NULL        0
updated_at    INTEGER   1        NULL        0
not_before    INTEGER   1        0           0
```

Bestandszeilen bekommen `0` = „keine Sperrzeit" — die Freigabe verhält sich
also exakt wie vorher.

> ⚠️ **Nur ausführen, wenn die Spalte wirklich fehlt.** Ein zweites `ALTER`
> bricht mit `duplicate column name` ab. Die Prüfung in 8.1 ist ernst gemeint.

<a name="9-verifikation-beweisen-dass-nur-einer-fragt"></a>
## 9. Verifikation: beweisen, dass nur einer fragt

Der wichtigste Test der ganzen Einrichtung.

### 9.1 Health

```bash
curl https://at-panther-lock.DEINE-SUBDOMAIN.workers.dev/health
```

```json
{ "ok": true, "service": "at-panther-lock", "now": 1770000000000 }
```

### 9.2 Aktueller Stand

```bash
curl https://at-panther-lock.DEINE-SUBDOMAIN.workers.dev/state
```

```json
{ "ok": true,
  "state": { "owner": "windows", "deviceId": "windows-pc1",
             "acquiredAt": 1770000000000, "expiresAt": 1770000900000,
             "updatedAt": 1770000000000, "notBefore": 0,
             "now": 1770000000123 } }
```

`"owner": null` = die Freigabe ist gerade frei.

### 9.3 Exklusivitätstest (zwei Geräte, 10 Sekunden)

**Gerät B darf die Freigabe nicht bekommen:**

```bash
URL=https://at-panther-lock.DEINE-SUBDOMAIN.workers.dev

# A übernimmt
curl -s -X POST $URL/sync -H "Content-Type: application/json" \
  -d '{"deviceId":"test-a","variant":"windows","ttlSeconds":900,"claimIfFree":true}'
#   -> "granted": true

# B versucht es – MUSS "granted": false ergeben!
curl -s -X POST $URL/sync -H "Content-Type: application/json" \
  -d '{"deviceId":"test-b","variant":"motog84","ttlSeconds":900,"claimIfFree":true}'
#   -> "granted": false,  "owner": "windows"

# Aufräumen
curl -s -X POST $URL/release -H "Content-Type: application/json" \
  -d '{"deviceId":"test-a"}'
```

| Prüfung | Erwartet |
|---|---|
| B bekommt die Freigabe **nicht** | ✅ `granted: false` |
| Inhaber bleibt A | ✅ `owner: windows` |
| Nach Release wieder frei | ✅ `owner: null` |

> ❌ **Bekommt B sie trotzdem**, ist der Exklusivitätsschutz kaputt. Nicht
<a name="10-url-in-beiden-apps-eintragen"></a>
## 10. URL in beiden Apps eintragen

### 10.1 Windows (Laptop)

1. `ATPanther.exe` starten
2. Das Fenster zeigt **eine** Seite — oben die Karte
   **„Monitor-Freigabe (nur eine Variante fragt ab)"**
3. In **„URL des Cloudflare Workers"** die URL aus 6.3 einfügen
4. **„Token"** leer lassen (Kapitel 13)
5. **„Freigabe speichern"**
6. In der Karte erscheint:

```
✅ Freigabe aktiv: windows
Freigabe aktiv: windows
```

7. Erst jetzt ist der Monitor startbar: **„Monitor starten"**

> 🛑 **Ohne eingetragene URL startet der Monitor absichtlich nicht.** Das ist
> der Schutz gegen parallele Abfragen — bewusst *fail-closed*. Du siehst dann
> „⚠ Freigabe nicht konfiguriert".

### 10.2 Moto G84 5G (Handy)

1. **`AT Panther Moto G84 5G.apk`** auf das Handy übertragen
2. Antippen, ggf. „Installation von unbekannten Quellen" erlauben
3. App öffnen → die **eine** Seite, oben die Karte **„Monitor-Freigabe"**
4. Auf **„Freigabe-Einstellungen (URL/Token)"** tippen
5. URL einfügen, **Token** leer lassen
6. **„Freigabe speichern"**, Dialog schließen
7. Unten in der Karte **„Akku-Schutz"** antippen und bestätigen — wichtig, damit
   Android 15 den Dienst nicht beendet

> Auf dem Handy **nicht** auf „Hier übernehmen" tippen, solange Windows
> überwachen soll — sonst wandert die Freigabe aufs Handy.

<a name="11-täglicher-betrieb"></a>
## 11. Täglicher Betrieb

### Umschalten

| Du willst … | Dann |
|---|---|
| **Moto → Windows** | In Windows: „Auf diesem Gerät übernehmen" |
| **Windows → Moto** | In Moto: „Hier übernehmen" |
| **Beide aus** | In der aktiven Variante: „Freigeben" |

**Was danach passiert:**

1. Die neue Variante meldet sich beim Worker → **bekommt die Freigabe**
2. Die alte Variante prüft spätestens nach **5 Minuten** (ihr Cache-Fenster)
   und geht selbst in Bereitschaft — du musst dort nichts tun
3. Die neue Variante wartet **120 s** (Cooldown), dann startet sie

> 💡 Das funktioniert auch, wenn das alte Gerät ausgeschaltet ist: Nach
> 15 Minuten läuft die Lease ab und die neue Variante nimmt sie automatisch.

### Was du in beiden Fenstern siehst

| Anzeige | Bedeutung |
|---|---|
| 🟢 `✅ Freigabe aktiv: <name>` | Diese Variante fragt ab |
| 🟠 `⏸ Bereitschaft – aktiv: <name>` | Die andere fragt ab |
| ⚪ `⚪ Freigabe frei` | Niemand fragt ab — „Übernehmen" tippen |
| 🔴 `⛔ Server nicht erreichbar` | Kein Netz → pausiert (fail-closed) |
| ⚠ `⚠ Freigabe nicht konfiguriert` | URL fehlt |

Die Anzeige aktualisiert sich **alle 60 Sekunden** von selbst.

> weiterbauen — das würde genau die Kontosperre auslösen, die du vermeiden willst.

### 9.4 Ende-zu-Ende-Test am echten Gerät

| Schritt | Erwartung |
|---|---|
| Windows: „Auf diesem Gerät übernehmen" | „✅ Freigabe aktiv: windows" |
| Moto: Status prüfen | „⏸ Bereitschaft – aktiv: windows" |
| Moto: Monitor starten | startet nicht |
<a name="12-die-sperrzeit-nach-dem-umschalten"></a>
## 12. Die Sperrzeit nach dem Umschalten (Cooldown)

Nach einer **frischen** Übernahme wartet die neue Variante **120 Sekunden**,
bevor sie das Portal zum ersten Mal anfasst.

**Warum?** Beim Umschalten kann das alte Gerät noch mitten in einem Durchlauf
stehen (Login, PoW, Datenabfrage). Ohne Wartezeit lägen die erste Abfrage der
neuen und die letzte der alten Variante direkt nebeneinander — genau das Muster,
das zu einer Kontosperre führt.

**Was du siehst:**

```
✅ Freigabe aktiv: motog84
Freigabe aktiv: motog84
⏳ Erste Abfrage in 118 s
```

Im Log steht dann:

```
14:23:01  📡  Freigabe uebernommen – warte 120 s bevor das Portal abgefragt wird
14:25:01  📡  Login erfolgreich
```

**Anpassen** (optional, pro Gerät über `POST /sync`):

| Wert | Wirkung |
|---|---|
| `cooldownMs: 0` | keine Wartezeit |
| `cooldownMs: 300000` | 5 Minuten (vorsichtiger) |
| *(nicht senden)* | Standard 120 s |

> Die Sperrzeit gilt **nur** direkt nach einem Wechsel. Ab dem zweiten Poll
> läuft wieder das normale Intervall — der Monitor ist also nicht dauerhaft
> langsamer.

<a name="13-optional-lock_token-setzen"></a>
## 13. Optional: LOCK_TOKEN setzen

Standardmäßig kann jeder die URL lesen und den Status verändern. Das ist
**unkritisch** (dort steht nur ein Variantenname), aber absichern geht so:

```bash
cd worker
npx wrangler secret put LOCK_TOKEN
```

Wrangler fragt den Wert ab, z. B. `geheim-9f3a2b`. Danach muss **jede** App
zusätzlich den Header `X-Lock-Token` mitsenden, also in den Apps unter
*Freigabe-Einstellungen* ins Feld **„Token"** eintragen.

Vergisst du das, antwortet der Worker mit `401 unauthorized`.

> Für den Privatbetrieb **nicht nötig** — und du müsstest den Token in beiden
> Geräten pflegen.

<a name="14-wenn-etwas-nicht-klappt"></a>
## 14. Wenn etwas nicht klappt

### Der Große Klassiker: „Der Token sieht KEIN Konto"

| Meldung | Ursache | Behebung |
|---|---|---|
| `/accounts` liefert `[]` | `Account Resources` falsch | Schritt 5.5 wiederholen |
| `9109 Unauthorized to access requested resource` | falsches Konto / keine Permission | Account-ID prüfen |
| `10000 Authentication error` bei `/d1/database` | `D1: Edit` fehlt | Schritt 5.4 |
| `workers.dev`-Subdomain schlägt fehl | `Account Settings: Read` fehlt | Schritt 5.3 |
| `D1` fehlt in der Permission-Liste | D1 nicht verfügbar | siehe unten |

**Details:** [`CLOUDFLARE-TOKEN-ANLEITUNG.md`](CLOUDFLARE-TOKEN-ANLEITUNG.md)

### Worker läuft, aber die App findet ihn

| Symptom | Prüfen |
|---|---|
| `⚠ Freigabe nicht konfiguriert` | „Freigabe speichern" geklickt? URL darf **nicht** mit `/` enden |
| `⛔ Server nicht erreichbar` | URL im Browser öffnen — kommt `{"ok":true,…}`? |
| `401 unauthorized` | `LOCK_TOKEN` gesetzt, aber nicht in der App eingetragen |
| `HTTP 400` | Token leer oder D1-Berechtigung fehlt |

### Wenn D1 nicht verfügbar ist

Sollte die D1-Zeile fehlen, gibt es einen kostenlosen Ersatz **ohne** Datenbank
und **ohne** Worker: eine JSON-Datei im Repository, gelesen über
`raw.githubusercontent.com`. Der Preis: das Umschalten ist **nicht mehr
atomar** und läuft über einen GitHub-Klick. Sag Bescheid, dann wird das umgebaut.
<a name="15-kosten-und-kontingente"></a>
## 15. Kosten und Kontingente

**Der gesamte Betrieb kostet 0 €.** Die Free-Tier-Grenzen liegen sehr weit weg
von dem, was diese App braucht:

| Ressource | Free-Limit | Dein Bedarf (2 Geräte) | Auslastung |
|---|---|---|---|
| Workers-Requests | 100.000 / Tag | ~580 / Tag | **0,6 %** |
| D1 Zeilen gelesen | 5 Mio. / Tag | ~580 / Tag | **0,01 %** |
| D1 Zeilen geschrieben | 100.000 / Tag | ~290 / Tag | **0,3 %** |
| D1 Speicher | 5 GB | ~10 KB | ~0 % |

**Rechnung:** ein Check alle 5 min = 288/Tag/Gerät ≈ **290/Tag**. Cooldown und
Umschalten kommen hinzu, bleiben aber im Bereich einiger Zehn Abrufe.

> ⚠️ **Wichtige Änderung seit 1. September 2026:** Cloudflare **erzwingt** die
> D1-Tageslimits im Free-Tier. Bei Überschreitung schlagen *alle* Abfragen bis
> **00:00 UTC** fehl — und weil die Freigabe bewusst *fail-closed* ist, würde
> der Monitor dann pausieren. Bei 0,3 % ist das praktisch ausgeschlossen.

Verbrauch ansehen: <https://dash.cloudflare.com> → Workers & Pages → D1 →
deine Datenbank → **Metrics → Row Metrics**.

<a name="16-sicherheit"></a>
## 16. Sicherheit

| Punkt | Regel |
|---|---|
| **Token** | **Nicht** ins Repository committen — auch nicht in eine `.env`, die du committest. |
| **Nach dem Deploy** | Token **widerrufen**. Zur Laufzeit wird er nicht gebraucht, die Freigabe funktioniert weiter. |
| **Im Chat/Verlauf** | Ein Token im Chat gilt als bekannt. Auf **Roll** klicken und neu erstellen. |
| **Was gespeichert wird** | Nur Variantenname, Geräte-ID, Zeitstempel. **Keine** Rufnummer, **kein** Passwort. |
| **Offene URL** | Unkritisch — dort steht nur ein Name. Wer es sicherer will: Kapitel 13. |
| **GitHub Actions** | Der Token wird **nie** in die CI gegeben. Nur das Deploy läuft manuell. |

<a name="17-update-auf-eine-neue-version"></a>
## 17. Update auf eine neue Version

```bash
cd worker
# Erst testen – läuft ohne Cloudflare, prüft die Logik in SQLite
node --no-warnings test/lease-sim.mjs
```

Erwartet: `BESTANDEN: 33 ok, 0 fehlgeschlagen`

Dann deployen:

```powershell
pwsh -File .\deploy.ps1 -Token "cfut_DEIN_TOKEN"
```

> 🔒 Wurde der Token in Kapitel 16 widerrufen, vorher einen neuen erstellen
> (Kapitel 5) — oder Wrangler nutzen (Kapitel 7), das braucht keinen Token.

<a name="18-kurzreferenz"></a>
## 18. Kurzreferenz

### Alle Befehle

```bash
# Status
curl $URL/health
curl $URL/state

# Cooldown-Spalte prüfen
npx wrangler d1 execute at-panther-lock --remote \
  --command "SELECT name FROM pragma_table_info('monitor_lock') WHERE name='not_before'"

# Spalte nachrüsten (nur wenn sie fehlt!)
npx wrangler d1 execute at-panther-lock --remote \
  --file=migrations/001_add_not_before.sql

# Logik testen (ohne Cloudflare)
node --no-warnings test/lease-sim.mjs

# Deploy
pwsh -File ./deploy.ps1 -Token "cfut_..."
```

### Zeiten auf einen Blick

| Wert | Dauer |
|---|---|
| Prüfintervall (Cache-Fenster) | 5 min |
| Lease-Gültigkeit (TTL) | 15 min |
| Cooldown nach Übernahme | 2 min |
| Cache bei Server-Ausfall | 30 min |
| Polling-Intervall Portal | 60 s |

### Begriffe

| Begriff | Bedeutung |
|---|---|
| **Lease** | Die Leihgabe: wer sie hält, darf das Portal abfragen |
| **Inhaber / owner** | Die aktuell berechtigte Variante |
| **TTL** | 900 s — so lange gilt die Lease ohne Verlängerung |
| **Cooldown** | 120 s Wartezeit direkt nach der Übernahme |
| **fail-closed** | Bei Server-Ausfall: lieber nicht abfragen |
| **Bereitschaft** | Zustand einer nicht berechtigten Variante |
| **Account ID** | `dc36919cd74227d24176872483647bf1` |
| **D1** | kostenlose SQLite-Datenbank in Cloudflare |
| **Workers** | kostenlose Script-Ausführung (hier: die Freigabe-Logik) |

### Notfall: alles zurücksetzen

```bash
npx wrangler d1 execute at-panther-lock --remote \
  --command "DELETE FROM monitor_lock"
```

Danach steht die Freigabe wieder auf „frei" und jede Variante kann neu übernehmen.

---

**Siehe auch:**
- [`worker/README.md`](worker/README.md) — technische Kurzfassung
- [`CLOUDFLARE-TOKEN-ANLEITUNG.md`](CLOUDFLARE-TOKEN-ANLEITUNG.md) — Token-Rechte im Detail
- [`Moto G84 5G/README.md`](Moto%20G84%205G/README.md) — Geräte-Variante
- [`Windows/README.md`](Windows/README.md) — Windows-Variante
