# ADR-011: No domain events in the storage layer

## Status

Accepted (2026-06-11)

## Context

Is `Status: Pending → Paid` an `Update` or an `OrderPaid`? Both — but on different
levels. If the storage layer produced domain events, the data model would have to
anticipate every domain interpretation, and the kernel would be back at the
event-sourcing mandate vNEXT is trying to avoid.

v1 already separated `change_feed` (technical) from `business_event_log` (domain)
— this separation remains, but moves entirely out of the kernel.

## Decision

1. The kernel stores exclusively **`DocumentChanged`** (insert/update/delete with
   diff, ADR-002/004). There are no event types, no event registry, no event
   publishing in the storage layer.
2. **Domain events are a processing concern**: an ordinary change handler
   (ADR-009) translates state transitions into domain events and publishes them
   wherever (table, bus, webhook):

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

3. **Scope boundary**: this ADR concerns events that interpret state transitions.
   Domain **facts without state truth** (`UserLoggedIn`) are stored explicitly by
   the application via the append-only event log — see ADR-013. That is no
   interpretation by the kernel and therefore no contradiction.

## Consequences

- Domain events can be introduced later, changed, and (via rebuild)
  **retroactively generated** from the change history — an advantage event-first
  systems do not have.
- The layering is clean: storage understands documents, processing understands
  the domain.
- Consumers expecting "real" domain events get them — just from the translator
  layer, with at-least-once semantics (mind idempotency, ADR-009).
