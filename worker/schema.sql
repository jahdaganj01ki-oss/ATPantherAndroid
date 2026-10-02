-- AT Panther – Monitor-Freigabe (eine einzige Zeile, id = 1)
--
-- Die Zeile ist die "Lease": wer sie haelt, darf das ALDI-Talk-Portal
-- abfragen. expires_at ist der Ablaufzeitpunkt; danach darf ein anderes
-- Geraet die Lease uebernehmen (atomar via bedingtes UPSERT).
CREATE TABLE IF NOT EXISTS monitor_lock (
    id          INTEGER PRIMARY KEY CHECK (id = 1),
    owner       TEXT    NOT NULL,           -- Varianten-ID, z. B. "windows"
    device_id   TEXT    NOT NULL,           -- Geraete-ID, verhindert Gleichstand
    acquired_at INTEGER NOT NULL,           -- ms seit Epoch, bei takeover neu
    expires_at  INTEGER NOT NULL,           -- ms seit Epoch, Ablauf der Lease
    updated_at  INTEGER NOT NULL,           -- ms seit Epoch, letzte Aenderung
    not_before  INTEGER NOT NULL DEFAULT 0  -- Sperrzeit nach frischer Uebernahme
);

CREATE INDEX IF NOT EXISTS idx_monitor_lock_owner ON monitor_lock(owner);

-- ── Bestandsdatenbanken nachtruecken ─────────────────────────────────────────
-- ALTER TABLE steht bewusst NICHT hier: SQLite kann DDL nicht in einen
-- bedingten Ausdruck einbetten (Syntaxfehler), und "ADD COLUMN IF NOT EXISTS"
-- gibt es nicht. Die Spalte `not_before` wird deshalb ueber
-- migrations/001_add_not_before.sql nachgetragen – deploy.ps1 und der
-- Migrationsschritt der Anleitung pruefen vorher mit
--   SELECT name FROM pragma_table_info('monitor_lock') WHERE name='not_before'
-- ab, ob sie schon existiert, und fuehren das ALTER nur dann aus.
-- Fuer NEUE Datenbanken ist die Spalte durch das CREATE TABLE oben enthalten.
