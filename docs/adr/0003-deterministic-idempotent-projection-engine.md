# ADR 0003: Deterministische und idempotente Projection Engine

## Status
Vorgeschlagen

## Kontext
Read Models müssen zuverlässig aus dem Change-Feed aufgebaut werden. Dabei müssen Race Conditions, Doppelverarbeitungen (Duplikate) und partielle Updates vermieden werden. Das System muss zudem in der Lage sein, Read Models bei Bedarf sicher neu aufzubauen (Rebuild).

## Entscheidung
Wir implementieren eine Projection Engine mit folgenden Garantien:
1. **Batching/Grouping:** Änderungen werden logisch gruppiert (z. B. über Transaction-ID oder explizite `BatchId`), um atomare Business-Events statt roher Tabellen-Changes zu verarbeiten.
2. **Idempotenz:** Projektionen verwenden idempotente SQL-Operationen (z. B. `INSERT ... ON DUPLICATE KEY UPDATE` oder gezielte `UPDATE`-Statements).
3. **Checkpointing:** Jede Projection verwaltet ihren eigenen Fortschritt in einer `ProjectionState`-Tabelle (`LastProcessedEventId` oder `BatchId`).
4. **Determinismus:** Projektoren enthalten keine Business-Logik, sondern nur deterministische Transformationen.

## Konsequenzen
- **Positiv:** Garantiert "At-Least-Once" Delivery mit "Exactly-Once" Semantik auf Projection-Ebene. Ermöglicht sichere Full- und Partial-Rebuilds von Read Models ohne Downtime oder Inkonsistenzen.
- **Negativ:** Erfordert strikte Disziplin beim Schreiben der Projection-Logik (keine nicht-deterministischen Funktionen wie `GETDATE()` im Projector).
- **Neutral:** Führt eine `ProjectionState`-Tabelle pro Read Model oder eine zentrale State-Tabelle ein.
