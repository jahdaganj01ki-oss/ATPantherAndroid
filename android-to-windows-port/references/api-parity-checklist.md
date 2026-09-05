# API-Vertrag: vollständig erfassen und vergleichen

Der häufigste Port-Fehler ist nicht falscher Code, sondern ein unvollständiger Vertrag: Ein Header
fehlt, ein Body ist `"{}"` statt leer, eine Zahl ist eine Zahl statt eines Strings. Diese Checkliste
erzwingt Vollständigkeit.

## Pro Aufruf erfassen

| Feld | Was notieren | Warum es oft kaputtgeht |
| --- | --- | --- |
| Methode | `GET`/`POST`/… | POST mit leerem Body wird zu GET "optimiert" |
| URL-Vorlage | Basis + Pfad, exakt, inkl. trailing `/` | Trailing Slash entscheidet über Redirect oder 404 |
| Query-Parameter | Name, Wert, **Reihenfolge**, Kodierung | Some-Server prüfen Signatur/Reihenfolge; `+` vs `%20` |
| Header | exakter Name (Groß-/Kleinschreibung), Wert oder Wertmuster | `X-CORRELATION-ID` ≠ `x-correlation-id` für manche Gateways |
| Body | Rohbytes-Länge, Content-Type, Schlüsselreihenfolge | `"{}"` (2 Bytes) vs `""` (0 Bytes) ist verhaltensrelevant |
| Auth | Cookie-Name, Domain, Path, Token-Header | Domain/Path des Cookies falsch gesetzt → Session weg |
| Redirects | folgen oder nicht, max Hops, relative Auflösung | Auto-Redirect zerstört mehrstufige Auth-Ketten |
| Timeouts | Connect, Read, Write | Android-Defaults ≠ .NET-Defaults |
| Fehlerpfade | Statuscodes, Exception-Behandlung, Zähler | Fehlerzähler steuern oft Sperren/Pausen |
| Response-Parsing | JSON-Pfade, Default-Werte, Typumwandlung | `optString` mit Default vs Exception in .NET |

## Byte-Ebene-Fallen

- **Leerer Body:** Content-Type setzen und Body mit 0 Bytes senden, nicht `"{}"`.
- **JSON-Typen:** `"2"` ist nicht `2`. ForgeRock-artige APIs erwarten durchgängig Strings.
- **Hex-Potenziale:** SHA-1/SHA-256-Ausgabe in der gleichen Schreibweise (klein/groß) wie im
  Original erzeugen; `.ToString("x")` statt default `ToString()` verwenden.
- **Base64Url:** kein Padding, `-`/`_` statt `+`/`/` (PKCE `code_challenge`).
- **UUID-Formate:** mit oder ohne Bindestriche, mit oder ohne Präfix (`C_`, `T_`).
- **User-Agent:** exakt kopieren, auch wenn er nach einem anderen Betriebssystem klingt.
- **Relative Redirects:** `//pfad/x` ist nicht automatisch protokollrelativ. Wenn das Original
  `//user/…` als Pfad der Basis-Domain interpretiert, muss die Windows-Version das genauso tun.
- **Kompression und HTTP-Version:** Accept-Encoding und TLS/HTTP2-Verhalten können Antworten
  verändern; bei Abweichungen zuerst dort vergleichen.

## Golden Vectors

Für jede Antwort, die geparst wird, mindestens einen echten Beispiel-Body sichern (ohne Secrets,
ohne personenbezogene Daten — ggf. maskieren) und daraus Parsing-Tests ableiten:

1. Normalfall
2. Leere Liste / fehlendes Feld
3. Fehlerstatus
4. Mehrdeutiger Fall (mehrere Einträge, davon einer passend)

## Vergleichsharness

Eine kleine Konsolen-/Testroutine, die pro Aufruf die **tatsächlich gesendete** Anfrage
Zeile für Zeile ausgibt (Methode, URL, Header in Reihenfolge, Body-Länge, Body), und dieselbe
Ausgabe für die Android-Referenz gegenüberstellt. Unterschiede sind dann Beweise, keine Vermutungen.

## Abnahmefrage

Beantworte für jeden Aufruf: *Könnte jemand, der nur `API-CONTRACT.md` liest, die Windows-Version
schreiben, ohne den Kotlin-Code zu öffnen?* Wenn nein, ist Phase 2 nicht fertig.
