# ADR-013: Fachliche Events — Translator, Event-Log und die Grenze dazwischen

## Status

Accepted (2026-06-11)

## Kontext

ADR-011 hält fachliche Events aus dem Storage Layer heraus: Der Kernel kennt nur
`DocumentChanged`. Das deckt aber nur Events ab, die **Zustandsübergänge** sind
(`OrderPaid` aus `status: Pending → Paid`). Es gibt eine zweite Kategorie: **Fakten
ohne Zustandswahrheit** — `UserLoggedIn`, `EmailSent`, `ExportDownloaded`. Für sie
existiert kein Diff, aus dem ein Translator etwas ableiten könnte; das Faktum selbst
ist die Information (Audit, Fraud Detection, Verhaltensanalyse).

v1 trennte dafür bereits `change_feed` und `business_event_log` — diese Trennung kehrt
als bewusstes vNEXT-Konzept zurück.

## Entscheidung

Fachliche Events werden nach drei Fällen modelliert:

| Fall | Beispiel | Modellierung |
|------|----------|--------------|
| **Zustandsübergang** | `OrderPlaced`, `OrderPaid` | Dokumentänderung ist die Wahrheit; Translator-Handler leitet das Event aus dem Diff ab (ADR-011). Rückwirkend per Rebuild erzeugbar. |
| **Faktum ohne Zustand** | `UserLoggedIn`, `EmailSent` | Explizites `session.Append(...)` in das **append-only Event-Log** (unten). |
| **Trigger** ("danach X auslösen") | Bestätigungsmail nach Bestellung | Kein gespeichertes Event — Handler-Subscription auf Fall 1 oder 2 (ADR-009). |

Entscheidungsregel: *Muss sich das System einen Zustand merken → Dokument. Muss es sich
ein Vorkommnis merken → Event-Log. Soll nur etwas passieren → Handler.*

### Das Event-Log

1. **Eigene Tabelle**, gleiche Infrastruktur-Muster wie der Change Feed:

   ```sql
   CREATE TABLE papuma.event
   (
       seq         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
       tenant_id   text        NOT NULL,
       event_type  text        NOT NULL,
       payload     jsonb       NOT NULL,   -- policy-bereinigt
       metadata    jsonb       NOT NULL,   -- CorrelationId, Actor, ...
       occurred_at timestamptz NOT NULL DEFAULT now(),
       txid        xid8        NOT NULL DEFAULT pg_current_xact_id()
   );
   ```

2. **Append läuft in der Session-Transaktion**: `session.Append(new UserLoggedIn(...))`
   committet atomar mit etwaigen Saves/Patches derselben Session — Faktum und
   Zustandsänderung (z. B. `lastLoginAt`-Patch) sind nie inkonsistent und teilen die
   `correlationId`.
3. **Event-Typen sind registrierte C#-Typen im Metamodell** — damit gelten die
   Datenschutz-Policies (ADR-007) auch hier: `[SensitiveData]` auf einer IP-Adresse
   wirkt im Payload genauso wie im Diff. Schema-Evolution folgt den additiven Regeln
   aus ADR-005; transformierende Änderungen erfordern einen neuen Event-Typ
   (Events sind unveränderliche Fakten, es gibt kein Upcasting beim Lesen alter Events).
4. **Konsum über dieselbe Processing-Engine** (ADR-009/010): Handler abonnieren den
   Change Feed, das Event-Log oder beides; Checkpoints, Retry, snapshot-basiertes
   Polling und Wakeup funktionieren identisch (eigene Checkpoint-Position pro Feed).
   Es gibt **keine globale Ordnung über beide Feeds hinweg** — wer Zusammenhänge
   braucht, korreliert über `correlationId`.
5. **Retention ist hier legitim**: Anders als ChangeRecords (an Dokumentversionen
   gebunden) dürfen Events nach Typ-spezifischen Fristen gelöscht werden
   (`UserLoggedIn` nach 90 Tagen). Das Event-Log ist Faktenspeicher, kein
   Versionsspeicher.

### Die rote Linie

Das Event-Log ist **niemals Replay-Quelle für Zustand**. Kein Dokument wird aus Events
rekonstruiert; kein Upcaster, kein Aggregat-Rebuild hängt daran. Wer dorthin will,
will Event Sourcing — und damit ein anderes Produkt (vgl. ADR-002).

## Konsequenzen

- `UserLoggedIn`-artige Fakten haben einen first-class Platz, ohne das Dokumentmodell
  zu verbiegen (kein Missbrauch von Dokumenten als Event-Container, keine
  Version-Explosion durch hochfrequente Pseudo-Patches).
- ADR-011 bleibt unangetastet: Der Kernel interpretiert weiterhin nichts — bei Fall 1
  leitet die Processing-Schicht ab, bei Fall 2 spricht die Anwendung das Faktum
  explizit aus.
- Zwei Feeds bedeuten zwei Checkpoint-Räume; das ist bewusst einfacher als eine
  vereinheitlichte Sequenz (v1s `kind`-Spalte im unified Feed), kostet aber globale
  Ordnung zwischen Changes und Events — akzeptiert, Korrelation statt Ordnung.
- Fall-1-Events bleiben die erste Wahl, wo immer ein Zustandsübergang existiert:
  Sie sind rückwirkend erzeugbar und können nicht vergessen werden (der Diff entsteht
  immer); `Append` dagegen kann ein Entwickler vergessen — Code-Review-Thema.
