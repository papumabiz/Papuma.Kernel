# ADR-002: Dokument als Source of Truth, Change Feed abgeleitet

## Status

Accepted (2026-06-11)

## Kontext

v1 nutzte relationale Tabellen plus Outbox: Änderungen mussten explizit als Events
formuliert werden, Change Detection war Handarbeit. Event Sourcing (à la Marten) löst
das, erzwingt aber "Event ist Wahrheit" mit allen Folgekosten (Replay-Pflicht,
DSGVO-Konflikt mit unveränderlichen Streams, fachliche Event-Modellierung ab Tag 1).

Das eigentliche Ziel von Papuma ist: **Änderungen als First-Class-Konzept ohne
Event-Sourcing-Zwang.**

## Entscheidung

1. Die Wahrheit ist das **JSON-Dokument** eines Aggregats (eine C#-Klasse, serialisiert
   nach JSONB).
2. Der **Change Feed wird abgeleitet**: Bei jedem Save erzeugt der Kernel automatisch
   einen `ChangeRecord` (Operation, Version, Diff, Metadata) — in derselben Transaktion
   wie das Dokument-Update.
3. Es gibt **genau einen technischen Change-Typ** (`DocumentChanged` mit
   Insert/Update/Delete als Operation), keine fachlichen Event-Typen im Storage (ADR-011).
4. Lesen des aktuellen Zustands ist ein einfacher Dokument-Load — kein Replay nötig.

## Konsequenzen

- Change Detection ist trivial und vollständig: Diff zweier JSON-Dokumente, nie wieder
  vergessene Outbox-Einträge.
- Der Zustand ist nie "abgeleitet falsch" — es gibt keinen Drift zwischen Events und State.
- Historische Zustände sind über das Diff-Log rekonstruierbar (ADR-004), aber das ist ein
  Audit-/Replay-Feature, keine Lade-Voraussetzung.
- Der Verlust relationaler Constraints wird nicht ignoriert, sondern kontrolliert
  zurückgeholt (ADR-006).
- Modell-Semantik (Typen, Attribute) steht dem Kernel zur Verfügung — Grundlage der
  Datenschutz-Policies (ADR-007).
