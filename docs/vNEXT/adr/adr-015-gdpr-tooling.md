# ADR-015: DSGVO-Werkzeuge — Mechanismen im Kernel, Rechtsentscheidungen in der Anwendung

## Status

Accepted (2026-06-12) · Umsetzung geplant als Phase 12

## Kontext

Über die Policies (ADR-007) hinaus stellen sich drei Betroffenenrechte-Fragen:
Export/Auskunft (Art. 15/20), Löschung (Art. 17) und der Konflikt mit
Aufbewahrungspflichten (Art. 17 Abs. 3 / Art. 18 — z. B. HGB/AO-Fristen von 6–10
Jahren bei bestehendem Geschäftsverhältnis), der **pro Tenant unterschiedlich**
ausfällt: Tenant A darf wirklich löschen, Tenant B hat berechtigtes Interesse bzw.
gesetzliche Pflicht zur Aufbewahrung.

Zusätzlich existiert eine Lücke: Dokument-Löschung bereinigt den Feed nur für
**policy-geschützte** Felder. Ein getracktes Feld mit personenbezogenen Daten
(z. B. `Name` ohne Attribut) bleibt nach dem Delete im Klartext in historischen
Diffs stehen.

## Entscheidung

Die Trennlinie folgt dem Projektions-Prinzip (ADR-009): **Der Kernel liefert
ausführende Mechanismen über das, was nur er kennt (Metamodell, Historie, Scopes);
die Anwendung trifft die fachlich-juristischen Entscheidungen.**

### Kernel-Mechanismen (Phase 12)

1. **Export-Assembly** (Art. 15/20): Gegeben Dokument-Referenzen und
   Event-Selektoren (Payload-Pfad = Wert, z. B. `userId = X`) erzeugt der Kernel
   einen strukturierten JSON-Export: aktueller Zustand + Änderungshistorie
   (Diffs/Metadata) + Events. Policy-geschützte Felder erscheinen als
   Änderungsmarker, nie als Wertverlauf — die Minimierung aus ADR-007 wirkt
   automatisch auch im Export.
2. **Daten-Inventar** (Art.-30-Unterstützung): Report aus dem Metamodell — welche
   Typen/Felder welche Policies tragen, welche Event-Typen welche Retention haben.
3. **Lösch-Primitiv pro Scope**: Dokument-Hard-Delete (existiert) **plus
   `RedactHistoryAsync(documentRef, paths?)`** — nachträgliches Umschreiben
   historischer Diff-Einträge und Event-Payload-Felder auf Redacted-Marker, mit
   Audit-Metadaten (wer/wann/warum). Schließt die Lücke der getrackten PII-Felder.
   Das verletzt bewusst die Append-only-Reinheit — Art. 17 schlägt
   Architekturästhetik. Konsequenz bleibt konsistent zu ADR-008: Rollback über
   redactete Historie scheitert typisiert.

### Anwendungssache (bewusst keine Framework-Magie)

1. **Subjekt → Daten-Mapping**: Welche Dokumente/Events zu einer Person gehören,
   ist Domänenwissen — die Anwendung liefert die Referenzen (über ihre Keys und
   Projektionen).
2. **Rechtsgrundlagen-Entscheidung pro Tenant/Datenkategorie**: Löschen vs.
   Einschränken vs. Aufbewahren ist juristische Konfiguration. Der Kernel
   exekutiert pro Scope; die Scope-Isolation garantiert strukturell, dass die
   Löschung in Tenant A den aufbewahrungspflichtigen Tenant B nicht berührt.
   Für den Aufbewahrungsfall ist das Muster **Einschränkung statt Löschung**
   (Art. 18): Sperrstatus als Dokumentfeld, Verarbeitung anwendungsseitig
   einschränken, Lösch-Fälligkeit vormerken.
3. **Fristen-Scheduling** ("nach 10 Jahren wirklich löschen"): Anwendungs-Workflow,
   der terminiert das Kernel-Primitiv aufruft.

## Konsequenzen

- Auskunfts- und Löschersuchen werden mit wenigen Zeilen App-Code bedienbar, ohne
  dass das Framework juristische Annahmen einbacken muss, die pro Organisation
  falsch wären.
- Die PII-Disziplin bleibt erste Verteidigungslinie: **alle** personenbezogenen
  Felder gehören unter Policy — dann ist Delete von Haus aus sauber und
  `RedactHistoryAsync` nur das Sicherheitsnetz für Versäumnisse und Altbestände.
- `RedactHistoryAsync` ist ein scharfes Werkzeug (irreversibel): Audit-Metadaten
  verpflichtend, kein Bestandteil normaler Anwendungsabläufe.
- Das Inventar macht Policy-Lücken sichtbar (Review-Werkzeug: "welche Felder sind
  *nicht* geschützt?") — präventiv gegen genau die Lücke, die dieses ADR schließt.
