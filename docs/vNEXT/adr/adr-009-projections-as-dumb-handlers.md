# ADR-009: Projektionen als dumme Change Handler

## Status

Accepted (2026-06-11)

## Kontext

CQRS-/Projection-Frameworks abstrahieren oft an der falschen Stelle: Die Write-Seite ist
generisch (lohnt Abstraktion), die Read-Seite ist es fast nie — Suchindex, Dashboard-SQL
und Reporting-Insert derselben Änderung haben nichts gemeinsam. Read-Model-DSLs enden
regelmäßig bei "okay, ich brauche doch SQL".

## Entscheidung

1. **Ein Interface, mehr nicht:**

   ```csharp
   public interface IChangeHandler
   {
       string Name { get; }
       Task HandleAsync(ChangeRecord change, CancellationToken ct);
   }
   ```

   Ein Handler kann SQL-Projektion, Elasticsearch-Update, Webhook, Kafka-Publish,
   Audit-Log oder Event-Translator (ADR-011) sein. Der Kernel generiert kein SQL und
   kennt keine Read Models.

2. **Die Engine liefert ausschließlich Infrastruktur:**
   - Reihenfolge: pro Handler strikt nach `seq` (damit pro Dokument nach `version`),
   - persistierte Checkpoints pro Handler (`papuma.checkpoint`),
   - Retry mit Backoff, Poison-Handling (Change überspringen + Alarm statt Endlosschleife),
   - Rebuild: Checkpoint zurücksetzen, Feed-Replay,
   - Parallelisierung über Handler hinweg (nie innerhalb eines Handlers).

3. **At-least-once-Semantik.** Handler müssen idempotent sein; die Engine liefert dafür
   `(handler, seq)` als natürlichen Idempotenz-Schlüssel.

4. **Komfort als Zucker, nicht als Schicht:** Filter-Helfer wie

   ```csharp
   WhenFieldChanged<User>(x => x.Email)
   ```

   sind dünne Wrapper über `ChangeRecord.Diff` und erzeugen gewöhnliche Handler.

## Konsequenzen

- Projektionen nutzen das jeweils beste Werkzeug direkt (SQL, Client-SDKs) — keine
  Leaky Abstraction, kein "ja, aber dieser Fall ist speziell".
- Die Qualität des Kernels entscheidet sich an Checkpoints/Retry/Rebuild — genau dort
  wird investiert.
- Idempotenz ist Handler-Pflicht und wird in der Doku mit Mustern belegt
  (Upsert, `ON CONFLICT`, Checkpoint-vergleichendes Schreiben).
