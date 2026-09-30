/**
 * AT Panther – Monitor-Freigabe (Cloudflare Worker + D1)
 * ======================================================
 *
 * Zweck: Es laufen mehrere AT-Panther-Varianten (Windows, Ulefone, Moto)
 * mit demselben ALDI-Talk-Login. Fragt jede Variante eigenstaendig alle
 * 60 s das Portal ab, steigt die Last um den Faktor Anzahl und das Konto
 * riskiert eine Sperre. Dieser Worker verwaltet deshalb genau EINE Freigabe
 * ("Lease"): nur das Geraet, das sie haelt, darf das Portal abfragen.
 *
 * Eigenschaften:
 *  - Atomar: das Uebernehmen passiert in einem bedingten UPSERT. Zwei
 *    Geraete koennen nie gleichzeitig Inhaber werden.
 *  - Selbstheilend: der Inhaber verlaengert die Lease alle 5 Minuten.
 *    Faellt er aus, laeuft sie nach `ttlSeconds` ab und ein anderes Geraet
 *    darf uebernehmen – ohne dass am anderen Geraet etwas getan werden muss.
 *  - Kostenlos: Workers-Free-Tier (100.000 Requests/Tag) und D1 kosten
 *    nichts. Bedarf dieser Loesung: ~290 Requests/Tag/Geraet.
 *  - Harmlose Daten: in der Datenbank stehen nur ein Variantenname, eine
 *    Geraete-ID und Zeitstempel – KEINE Zugangsdaten.
 *
 * Bewusst plain ESM-JavaScript ohne Build-Step: die Datei laesst sich
 * unveraendert per Cloudflare-API (PUT /workers/scripts) oder per
 * `npx wrangler deploy` veroeffentlichen.
 *
 * API (alle Antworten JSON):
 *   GET  /health       -> { ok, service, now }
 *   GET  /state        -> { ok, state }
 *   POST /sync         -> { ok, granted, renewed, state }
 *        Body: { deviceId, variant, ttlSeconds?, claimIfFree?, steal? }
 *   POST /release      -> { ok, released, state }
 *        Body: { deviceId }
 *
 * `state` sieht immer so aus:
 *   { owner, deviceId, acquiredAt, expiresAt, updatedAt, now }
 * und ist "frei", wenn owner === null.
 */

// ── Konstanten ─────────────────────────────────────────────────────────────

export const DEFAULT_TTL_SECONDS = 900; // 15 min Lease
export const MIN_TTL_SECONDS = 120;
export const MAX_TTL_SECONDS = 3600;
const MAX_FIELD_LENGTH = 64;

/** Steuerzeichen ausschneiden (Werte landen in der DB und in Antworten). */
const CONTROL_CHARS = new RegExp("[\\u0000-\\u001F\\u007F]", "g");

/** Lease verlaengern: nur wirksam, wenn die Zeile uns gehoert. */
export const RENEW_SQL =
  "UPDATE monitor_lock SET expires_at = ?, updated_at = ? " +
  "WHERE id = 1 AND device_id = ?";

/** Freigeben: nur der Inhaber darf loeschen. */
export const RELEASE_SQL = "DELETE FROM monitor_lock WHERE id = 1 AND device_id = ?";

/** Zeile lesen. */
export const SELECT_SQL =
  "SELECT owner, device_id, acquired_at, expires_at, updated_at FROM monitor_lock WHERE id = 1";

/**
 * Bedingtes UPSERT = atomare Lease-Uebernahme.
 *
 * Die abschliessende WHERE-Klausel entscheidet, ob die Zeile ueberhaupt
 * ersetzt wird:
 *   - `?steal = 1`             -> "Uebernehmen"-Button, immer
 *   - abgelaufen / nie gesetzt -> regulaere Uebernahme
 *   - gleiche device_id        -> erneutes Uebernehmen desselben Geraetes
 *
 * `acquired_at` bleibt erhalten, wenn dasselbe Geraet seine Lease verlaengert –
 * nur ein echter Geraetewechsel setzt die Startzeit neu.
 */
export const UPSERT_SQL = `
INSERT INTO monitor_lock (id, owner, device_id, acquired_at, expires_at, updated_at)
VALUES (1, ?, ?, ?, ?, ?)
ON CONFLICT(id) DO UPDATE SET
  owner       = excluded.owner,
  device_id   = excluded.device_id,
  acquired_at = CASE
                  WHEN monitor_lock.device_id = excluded.device_id
                       AND monitor_lock.expires_at > ?
                  THEN monitor_lock.acquired_at
                  ELSE excluded.acquired_at
                END,
  expires_at  = excluded.expires_at,
  updated_at  = excluded.updated_at
WHERE ? = 1
   OR monitor_lock.expires_at IS NULL
   OR monitor_lock.expires_at <= ?
   OR monitor_lock.device_id = excluded.device_id
`;

// ── Helfer (auch vom lokalen Test in test/lease-sim.mjs genutzt) ───────────

export function json(data, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
    },
  });
}

export function clean(value) {
  if (typeof value !== "string") return "";
  // Auch Leerzeichen im Inneren fliegen raus: "win dows" und "windows" muessen
  // zwingend dasselbe Geraet sein, sonst koennte ein zweites Geraet eine
  // vermeintlich "andere" device_id anmelden und die Lease faelschlich behalten.
  return value.replace(CONTROL_CHARS, "").replace(/\s+/g, "").slice(0, MAX_FIELD_LENGTH);
}

export function clampTtl(value) {
  const n = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(n)) return DEFAULT_TTL_SECONDS;
  return Math.min(MAX_TTL_SECONDS, Math.max(MIN_TTL_SECONDS, Math.round(n)));
}

/**
 * Abgelaufene Leases werden nach aussen hin als "frei" gemeldet – so sieht
 * ein Client niemals einen Besitzer, der die Freigabe faktisch nicht mehr hat.
 */
export function toState(row, now) {
  const r = row || {};
  const expiresAt = r.expires_at ?? 0;
  const alive = !!r.owner && expiresAt > now;
  return {
    owner: alive ? r.owner : null,
    deviceId: alive ? r.device_id : null,
    acquiredAt: alive ? r.acquired_at ?? 0 : 0,
    expiresAt: alive ? expiresAt : 0,
    updatedAt: r.updated_at ?? 0,
    now,
  };
}

async function readBody(request) {
  try {
    const parsed = await request.json();
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch {
    return {};
  }
}

/** Optionaler Token-Schutz: nur aktiv, wenn LOCK_TOKEN als Secret gesetzt ist. */
function authorized(request, env) {
  const expected = env && env.LOCK_TOKEN;
  if (!expected) return true;
  return request.headers.get("X-Lock-Token") === expected;
}

// ── Kernlogik ──────────────────────────────────────────────────────────────

/**
 * /sync – der einzige Aufruf, den die Apps regelmaessig (alle 5 min) machen.
 *
 * Reihenfolge:
 *  1. Lease verlaengern, sofern die Zeile uns gehoert (bedingtes UPDATE,
 *     atomar – eine fremde Uebernahme macht es wirkungslos).
 *  2. Sonst uebernehmen, wenn die Lease frei/abgelaufen ist oder `steal`
 *     gesetzt wurde (bedingtes UPSERT, atomar).
 *  3. Zustand zurueckgeben – der Client entscheidet daraus, ob er das
 *     Portal abfragen darf. Ein "Doppelt-Inhaber"-Fenster kann nicht
 *     entstehen, weil jede Operation ihre Bedingung in derselben
 *     SQL-Anweisung prueft.
 */
export async function handleSync(env, body) {
  const deviceId = clean(body.deviceId);
  const variant = clean(body.variant) || "unknown";
  const ttlSeconds = clampTtl(body.ttlSeconds);
  const claimIfFree = body.claimIfFree === true;
  const steal = body.steal === true;

  if (!deviceId) {
    return json({ ok: false, error: "deviceId fehlt" }, 400);
  }

  const now = Date.now();
  const expiresAt = now + ttlSeconds * 1000;
  let renewed = false;

  if (!steal) {
    // (1) Verlaengern – WHERE device_id = uns.
    const result = await env.DB.prepare(RENEW_SQL)
      .bind(expiresAt, now, deviceId)
      .run();
    renewed = (result.meta && result.meta.changes ? result.meta.changes : 0) > 0;
  }

  if (!renewed && (claimIfFree || steal)) {
    // (2) Uebernehmen – nur wenn frei/abgelaufen bzw. ausdruecklich erzwungen.
    //     Reihenfolge der Platzhalter: owner, device, acquired, expires,
    //     updated, now (acquired_at-Erhalt), steal, now (Ablaufvergleich).
    await env.DB.prepare(UPSERT_SQL)
      .bind(variant, deviceId, now, expiresAt, now, now, steal ? 1 : 0, now)
      .run();
  }

  const row = await env.DB.prepare(SELECT_SQL).first();
  const state = toState(row, Date.now());
  return json({ ok: true, granted: state.deviceId === deviceId, renewed, state });
}

export async function handleRelease(env, body) {
  const deviceId = clean(body.deviceId);
  if (!deviceId) {
    return json({ ok: false, error: "deviceId fehlt" }, 400);
  }
  // Nur der Inhaber gibt frei – sonst koennte ein spaeter hinzugekommenes
  // Geraet die Lease eines aktiven Monitors einfach loeschen.
  const result = await env.DB.prepare(RELEASE_SQL).bind(deviceId).run();
  const row = await env.DB.prepare(SELECT_SQL).first();
  const state = toState(row, Date.now());
  return json({
    ok: true,
    released: (result.meta && result.meta.changes ? result.meta.changes : 0) > 0,
    state,
  });
}

// ── Worker-Einstieg ────────────────────────────────────────────────────────

export default {
  async fetch(request, env) {
    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204 });
    }
    if (!authorized(request, env)) {
      return json({ ok: false, error: "unauthorized" }, 401);
    }

    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, "") || "/";

    try {
      if (path === "/health" && request.method === "GET") {
        return json({ ok: true, service: "at-panther-lock", now: Date.now() });
      }
      if (path === "/state" && request.method === "GET") {
        const row = await env.DB.prepare(SELECT_SQL).first();
        return json({ ok: true, state: toState(row, Date.now()) });
      }
      if (path === "/sync" && request.method === "POST") {
        return await handleSync(env, await readBody(request));
      }
      if (path === "/release" && request.method === "POST") {
        return await handleRelease(env, await readBody(request));
      }
      return json({ ok: false, error: "unknown endpoint" }, 404);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      return json({ ok: false, error: message }, 500);
    }
  },
};
