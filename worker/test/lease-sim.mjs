/**
 * Lokaler Test der Freigabe-Logik – laeuft OHNE Cloudflare.
 *
 *   node test/lease-sim.mjs
 *
 * Getestet wird exakt der Code, der auch deployed wird: handleSync() /
 * handleRelease() aus src/index.js ausgeführt gegen eine echte SQLite-Datenbank
 * (D1 ist SQLite, die bedingten UPSERTs verhalten sich identisch). Der einzige
 * Unterschied ist ein duennes D1-Adapter-Objekt statt der Cloudflare-Plattform.
 *
 * Abgedeckt sind die properties, auf die es ankommt:
 *  - nur ein Geraet ist Inhaber
 *  - ein abgelaufener Inhaber gibt die Freigabe automatisch frei
 *  - "Uebernehmen" verdraengt sofort, der alte Inhaber sieht es beim
 *    naechsten /sync und geht selbst in den Bereitschaftsmodus
 *  - ein fremdes Geraet kann die Lease nicht loeschen
 */

import { DatabaseSync } from "node:sqlite";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

import { handleSync, handleRelease } from "../src/index.js";

const here = dirname(fileURLToPath(import.meta.url));
const db = new DatabaseSync(":memory:");
db.exec(readFileSync(join(here, "..", "schema.sql"), "utf8"));

/** Minimaler D1-Ersatz: dieselbe API-Oberflaeche, die der Worker erwartet. */
const env = {
  DB: {
    prepare(sql) {
      return {
        bind(...values) {
          this._values = values;
          return this;
        },
        async first() {
          const row = db.prepare(sql).get(...(this._values || []));
          return row === undefined ? null : row;
        },
        async run() {
          const r = db.prepare(sql).run(...(this._values || []));
          return { meta: { changes: Number(r.changes) } };
        },
      };
    },
  },
};

const post = (handler, body) =>
  handler(
    env,
    body,
  ).then(async (res) => ({ status: res.status, data: await res.json() }));

let passed = 0;
let failed = 0;

function check(label, actual, expected) {
  const ok = JSON.stringify(actual) === JSON.stringify(expected);
  if (ok) {
    passed++;
    console.log(`  ok    ${label}`);
  } else {
    failed++;
    console.log(`  FAIL  ${label}\n          erwartet: ${JSON.stringify(expected)}\n          ist:      ${JSON.stringify(actual)}`);
  }
}

const A = { deviceId: "windows-1a2b3c4d", variant: "windows", ttlSeconds: 900 };
const B = { deviceId: "ulefone-9f8e7d6c", variant: "ulefone", ttlSeconds: 900 };
const C = { deviceId: "moto-5566aabb", variant: "motog84", ttlSeconds: 900 };

console.log("\n1) Leerer Zustand");
{
  const res = await post(handleSync, { ...A, claimIfFree: true });
  check("erste Uebernahme wird gewaehrt", res.data.granted, true);
  check("Inhaber ist windows", res.data.state.owner, "windows");
  const acquired = res.data.state.acquiredAt;
  check("acquiredAt gesetzt", acquired > 0, true);

  console.log("\n2) Zweites Geraet wird abgewiesen (kein Doppelt-Inhaber)");
  const res2 = await post(handleSync, { ...B, claimIfFree: true });
  check("B bekommt die Freigabe nicht", res2.data.granted, false);
  check("Inhaber unveraendert", res2.data.state.owner, "windows");

  console.log("\n3) Inhaber verlaengert, Startzeit bleibt erhalten");
  await new Promise((r) => setTimeout(r, 5));
  const res3 = await post(handleSync, A);
  check("verlaengert", res3.data.renewed, true);
  check("acquiredAt unveraendert", res3.data.state.acquiredAt, acquired);
  check("expiresAt gewachsen", res3.data.state.expiresAt > res.data.state.expiresAt, true);

  console.log("\n4) Uebernehmen verdraengt – alter Inhaber geht selbst in Bereitschaft");
  const steal = await post(handleSync, { ...B, steal: true });
  check("B hat uebernommen", steal.data.granted, true);
  check("Inhaber ist jetzt ulefone", steal.data.state.owner, "ulefone");
  const backToA = await post(handleSync, A);
  check("A erkennt den Verlust", backToA.data.granted, false);
  check("A sieht ulefone als Inhaber", backToA.data.state.owner, "ulefone");
  check("A startet keine Lease", backToA.data.renewed, false);

  console.log("\n5) Fremdes Geraet kann die Lease nicht loeschen");
  const fakeRelease = await post(handleRelease, { deviceId: A.deviceId });
  check("Fremd-Release abgelehnt", fakeRelease.data.released, false);
  check("Inhaber bleibt ulefone", fakeRelease.data.state.owner, "ulefone");

  console.log("\n6) Inhaber gibt frei, danach ist die Freigabe wieder frei");
  const realRelease = await post(handleRelease, { deviceId: B.deviceId });
  check("Release erfolgreich", realRelease.data.released, true);
  check("Inhaber leer", realRelease.data.state.owner, null);

  console.log("\n7) Abgelaufene Lease wird automatisch uebernommen (ohne 'Uebernehmen')");
  const claimA = await post(handleSync, { ...A, claimIfFree: true });
  check("A hat die Freigabe", claimA.data.granted, true);
  // Lease des A kuenstlich ablaufen lassen (statt 15 min zu warten)
  db.prepare("UPDATE monitor_lock SET expires_at = ? WHERE id = 1").run(Date.now() - 1);
  const takeOver = await post(handleSync, { ...C, claimIfFree: true });
  check("Moto uebernimmt ohne 'Uebernehmen'", takeOver.data.granted, true);
  check("Inhaber ist motog84", takeOver.data.state.owner, "motog84");
  const seeA = await post(handleSync, A);
  check("A sieht den neuen Inhaber", seeA.data.granted, false);

  console.log("\n8) Abgelaufene Inhaber werden nach aussen als 'frei' gemeldet");
  db.prepare("UPDATE monitor_lock SET expires_at = ? WHERE id = 1").run(Date.now() - 1);
  const seen = await post(handleSync, { ...A, claimIfFree: false });
  check("Owner wird als leer gemeldet", seen.data.state.owner, null);
  check("nicht freigegeben ohne Uebernahme", seen.data.granted, false);

  console.log("\n9) Cooldown nach frischer Uebernahme (verhindert zu enge Abfragen)");
  db.exec("DELETE FROM monitor_lock");
  const coolClaim = await post(handleSync, { ...A, claimIfFree: true });
  check("A hat die Freigabe", coolClaim.data.granted, true);
  check(
    "Sperrzeit gesetzt",
    coolClaim.data.state.notBefore > Date.now(),
    true
  );
  // Uebernehmen verdraengt -> neuer Inhaber bekommt ebenfalls eine Sperrzeit
  const coolSteal = await post(handleSync, { ...B, steal: true });
  check("B hat uebernommen", coolSteal.data.granted, true);
  check(
    "B bekommt eigene Sperrzeit",
    coolSteal.data.state.notBefore > Date.now(),
    true
  );
  // Abgelaufene Cooldown wird als 0 gemeldet (kein Warten mehr noetig)
  db.prepare("UPDATE monitor_lock SET not_before = ? WHERE id = 1").run(Date.now() - 1);
  const expiredCool = await post(handleSync, { ...B });
  check(
    "verstrichene Sperrzeit -> 0",
    expiredCool.data.state.notBefore,
    0
  );
  // Verlaengerung raeumt die Sperrzeit ab
  db.prepare("UPDATE monitor_lock SET not_before = ? WHERE id = 1").run(Date.now() + 600_000);
  const renewClears = await post(handleSync, { ...B });
  check("Verlaengern raeumt Cooldown ab", renewClears.data.state.notBefore, 0);

  console.log("\n10) Cooldown laesst sich abschalten (cooldownMs = 0)");
  db.exec("DELETE FROM monitor_lock");
  const noCool = await post(handleSync, { ...C, claimIfFree: true, cooldownMs: 0 });
  check("keine Sperrzeit bei 0", noCool.data.state.notBefore, 0);
  const coolCustom = await post(handleSync, { ...A, steal: true, cooldownMs: 60_000 });
  check(
    "eigene Cooldown-Dauer wird uebernommen",
    coolCustom.data.state.notBefore > Date.now() &&
      coolCustom.data.state.notBefore <= Date.now() + 60_000,
    true
  );

  console.log("\n11) Eingabevalidierung");
  // Freigabe leeren, damit der folgende Aufruf wirklich uebernehmen kann
  db.exec("DELETE FROM monitor_lock");
  const noDevice = await post(handleSync, { variant: "windows" });
  check("fehlende deviceId -> 400", noDevice.status, 400);
  const weird = await post(handleSync, { deviceId: "  win  dows  ", variant: "windows", claimIfFree: true });
  check("deviceId getrimmt", weird.data.state.deviceId, "windows");
  db.exec("DELETE FROM monitor_lock");
}

console.log(`\n${failed === 0 ? "BESTANDEN" : "FEHLGESCHLAGEN"}: ${passed} ok, ${failed} fehlgeschlagen\n`);
process.exit(failed === 0 ? 0 : 1);
