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
    updated_at  INTEGER NOT NULL            -- ms seit Epoch, letzte Aenderung
);

CREATE INDEX IF NOT EXISTS idx_monitor_lock_owner ON monitor_lock(owner);
