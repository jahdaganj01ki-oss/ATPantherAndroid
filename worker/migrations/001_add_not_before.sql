-- Migration 001: Cooldown-Spalte nachruesten
--
-- Erforderlich fuer Datenbanken, die VOR der Cooldown-Erweiterung mit
-- schema.sql angelegt wurden. Fuer eine NEUE Datenbank ist die Spalte bereits
-- im CREATE TABLE enthalten und diese Datei wird NICHT gebraucht.
--
-- Vorher pruefen, ob die Spalte schon da ist (liefert 0 Zeilen = fehlt):
--
--   npx wrangler d1 execute at-panther-lock --remote \
--     --command "SELECT name FROM pragma_table_info('monitor_lock') WHERE name='not_before'"
--
-- Liefert die Abfrage eine Zeile, ist nichts zu tun. Ansonsten diese Datei
-- ausfuehren:
--
--   npx wrangler d1 execute at-panther-lock --remote --file=migrations/001_add_not_before.sql
--
-- Bestehende Zeilen bekommen 0 = "keine Sperrzeit", die Freigabe verhaelt
-- sich also exakt wie vorher.

ALTER TABLE monitor_lock ADD COLUMN not_before INTEGER NOT NULL DEFAULT 0;