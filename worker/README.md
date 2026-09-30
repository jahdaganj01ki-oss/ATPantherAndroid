# Monitor-Freigabe (Cloudflare Worker + D1)

Zentrale Freigabe für die AT-Panther-Varianten: **nur ein Gerät darf zur
Zeit das ALDI-Talk-Portal abfragen.** Alle Varianten teilen sich denselben
Login; ohne diese Sperre fragen z. B. Windows, Ulefone und Moto parallel alle
60 s ab und das Konto riskiert eine Sperre.

Kosten: **0 €** (Workers-Free-Tier: 100.000 Requests/Tag, D1 kostenlos).
Bedarf dieser Lösung: ~290 Requests/Tag/Gerät, also rund 870/Tag insgesamt.

## Wie es funktioniert

```
    Windows ─┐
  Ulefone ──┼──▶  GET/POST  ──▶  Worker  ──▶  D1: genau EINE Lease
     Moto ──┘                     │
                                  └──▶ nur der Inhaber darf pollen
```

- **Atomar**: Das Übernehmen passiert in einem bedingten `UPSERT`. Zwei Geräte
  können nie gleichzeitig Inhaber werden.
- **Selbstheilend**: Der Inhaber verlängert seine Lease alle 5 Minuten
  (TTL 15 min). Fällt er aus, läuft sie ab und ein anderes Gerät darf
  automatisch übernehmen – ohne dass am alten Gerät etwas getan werden muss.
- **Umschalten**: Auf dem gewünschten Gerät „Übernehmen" tippen. Das alte
  Gerät bemerkt es beim nächsten Check (≤ 5 min) und schaltet sich selbst in
  den Bereitschaftsmodus.
- **In der Datenbank stehen nur** Variantenname, Geräte-ID und Zeitstempel –
  **keine** Zugangsdaten.

## Dateien

| Datei | Inhalt |
|---|---|
| `src/index.js` | Worker (plain ESM, kein Build-Step) |
| `schema.sql` | D1-Tabelle (idempotent) |
| `test/lease-sim.mjs` | Test der Lease-Logik gegen lokales SQLite |
| `wrangler.toml` | Wrangler-Konfiguration |

## Erstinrichtung (einmalig, ca. 10 Minuten)

Voraussetzung: kostenloses Cloudflare-Konto.

```bash
cd worker
npm install                      # installiert nur wrangler (CLI)
npx wrangler login

npx wrangler d1 create at-panther-lock
# -> database_id in wrangler.toml unter [[d1_databases]] eintragen

npx wrangler d1 execute at-panther-lock --remote --file=schema.sql
npx wrangler deploy
```

`wrangler deploy` schreibt am Ende die URL in die Konsole, z. B.

```
https://at-panther-lock.<deine-subdomain>.workers.dev
```

Diese URL wird in **jeder** App unter *Monitor-Freigabe* eingetragen und
gespeichert. Danach auf dem Gerät, das überwachen soll, einmal auf
**Übernehmen** tippen.

### Ohne Wrangler (direkt per API)

Der Worker ist bewusst ohne Build-Step geschrieben und kann direkt
hochgeladen werden – ohne `npm install`:

```bash
# 1) Datenbank anlegen -> database_id notieren
curl -X POST "https://api.cloudflare.com/client/v4/accounts/<ACCOUNT_ID>/d1/database" \
  -H "Authorization: Bearer <API_TOKEN>" -H "Content-Type: application/json" \
  -d '{"name":"at-panther-lock"}'

# 2) Schema ausführen
curl -X POST "https://api.cloudflare.com/client/v4/accounts/<ACCOUNT_ID>/d1/database/<DB_ID>/query" \
  -H "Authorization: Bearer <API_TOKEN>" -H "Content-Type: application/json" \
  -d "{\"sql\":$(jq -Rs . < schema.sql)}"

# 3) Worker hochladen (D1-Binding als Metadata mitschicken)
curl -X PUT "https://api.cloudflare.com/client/v4/accounts/<ACCOUNT_ID>/workers/scripts/at-panther-lock" \
  -H "Authorization: Bearer <API_TOKEN>" \
  --data-binary @<(jq -Rs --arg db "<DB_ID>" \
    '{main_module:"index.js",compatibility_date:"2025-01-01",bindings:[{type:"d1",name:"DB",id:$db}]}' \
    && printf '\n' && cat src/index.js) \
  -H "Content-Type: application/javascript"
```

Der API-Token braucht dafür: **Account → D1: Edit** und
**Account → Workers Scripts: Edit**.

## Optional: Zugriffsschutz

Standardmäßig ist die Freigabe-URL offen lesbar. Das ist unkritisch – dort
steht nur ein Variantenname. Wer es trotzdem absichern will:

```bash
npx wrangler secret put LOCK_TOKEN
```

Dann muss in den Apps zusätzlich der Token eingetragen werden (Feld
„Token" im Freigabe-Dialog).

## API

| Endpunkt | Zweck |
|---|---|
| `GET /health` | Erreichbarkeit prüfen |
| `GET /state` | aktueller Stand, ohne Änderung |
| `POST /sync` | verlängern / übernehmen (der Normalfall, alle 5 min) |
| `POST /release` | eigene Freigabe abgeben |

`POST /sync` mit `{"deviceId": "...", "variant": "windows", "ttlSeconds": 900,
"claimIfFree": true}` liefert z. B.:

```json
{ "ok": true, "granted": true, "renewed": false,
  "state": { "owner": "windows", "deviceId": "windows-pc1",
             "acquiredAt": 1770000000000, "expiresAt": 1770000900000,
             "updatedAt": 1770000000000, "now": 1770000000123 } }
```

`state.owner === null` bedeutet: die Freigabe ist frei.

## Tests

```bash
cd worker
node test/lease-sim.mjs      # laeuft in CI (.github/workflows/worker.yml)
```

Deckt ab: kein Doppelt-Inhaber, Verlängern, Verdrängen, fremdes Freigeben
wird abgelehnt, automatische Übernahme nach Ablauf, Eingabevalidierung.
