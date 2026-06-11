# ADR-011: Keine fachlichen Events im Storage Layer

## Status

Accepted (2026-06-11)

## Kontext

Ist `Status: Pending → Paid` ein `Update` oder ein `OrderPaid`? Beides — aber auf
verschiedenen Ebenen. Würde der Storage Layer fachliche Events erzeugen, müsste das
Datenmodell jede fachliche Interpretation vorwegnehmen, und der Kernel wäre wieder beim
Event-Sourcing-Zwang, den vNEXT gerade vermeidet.

v1 trennte bereits `change_feed` (technisch) von `business_event_log` (fachlich) —
diese Trennung bleibt, rückt aber vollständig aus dem Kernel heraus.

## Entscheidung

1. Der Kernel speichert ausschließlich **`DocumentChanged`** (Insert/Update/Delete mit
   Diff, ADR-002/004). Es gibt keine Event-Typen, keine Event-Registry, kein
   Event-Publishing im Storage Layer.
2. **Fachliche Events sind ein Processing-Konzern**: Ein gewöhnlicher Change Handler
   (ADR-009) übersetzt Zustandsübergänge in fachliche Events und publiziert sie wohin
   auch immer (Tabelle, Bus, Webhook):

   ```csharp
   public sealed class OrderEventTranslator : IChangeHandler
   {
       public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
       {
           if (change.IsFieldTransition("status", from: "Pending", to: "Paid"))
               await _publisher.PublishAsync(new OrderPaid(change.DocumentId), ct);
       }
   }
   ```

3. **Abgrenzung**: Dieses ADR betrifft Events, die Zustandsübergänge interpretieren.
   Fachliche **Fakten ohne Zustandswahrheit** (`UserLoggedIn`) speichert die Anwendung
   explizit über das append-only Event-Log — siehe ADR-013. Das ist keine Interpretation
   durch den Kernel und daher kein Widerspruch.

## Konsequenzen

- Fachliche Events können nachträglich eingeführt, geändert und (per Rebuild) aus der
  Change-Historie **rückwirkend erzeugt** werden — ein Vorteil, den Event-First-Systeme
  nicht haben.
- Die Schichtung ist sauber: Storage versteht Dokumente, Processing versteht Fachlichkeit.
- Konsumenten, die "echte" Domain-Events erwarten, bekommen sie — nur eben aus der
  Translator-Schicht, mit at-least-once-Semantik (Idempotenz beachten, ADR-009).
