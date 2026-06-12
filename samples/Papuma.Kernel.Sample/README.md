# Papuma.Kernel Sample — a mini shop with every kernel concept as running code

One small ASP.NET Core app, one domain (products, stock, orders, approvals) —
and on the way through it you meet every concept the kernel ships:

| Concept | Where in this sample | Background |
|---|---|---|
| Session = unit of work, atomic multi-document commit | `POST /products` (product + inventory + fact, one commit) | architecture §5 |
| Bounded counter (never oversell) | `Inventory` validator + `Increment(-qty)` in `POST /orders` | concepts §17 |
| Privacy policies | `[SensitiveData] CustomerEmail` — check `/orders/{id}/history`: only `{"changed": true}` | ADR-007 |
| Event log (facts without state truth) | `StockReplenished` on create/replenish | ADR-013 |
| Workflow / saga | [`OrderWorkflowHandler`](Handlers/OrderWorkflowHandler.cs) | concepts §18, [recipe](../../docs/vNEXT/recipes/workflow-saga.md) |
| Human-in-the-loop | `ApprovalTask` document + `POST /approvals/{id}/decide` | concepts §18 |
| Saga compensation | rejection gives the stock back (`Increment(+qty)`) | concepts §18 |
| The timer primitive | [`ApprovalEscalationService`](Services/ApprovalEscalationService.cs) (`dueAt` poller) | concepts §18 |
| Optimistic concurrency as UX | second approver gets HTTP 409 | ADR-003 |
| Audit for free | `GET /orders/{id}/history` — every transition with actor and diff | ADR-003/004 |
| Realtime UI push | [`OrderUiNotifier`](Handlers/OrderUiNotifier.cs) → SignalR `/hub/shop` | [recipe](../../docs/vNEXT/recipes/realtime-ui-notifications.md) |
| Health check (feed lag) | `GET /health` | observability.md |
| MCP server for AI agents | `/mcp` (read-only default) | observability.md |

## Run it

Requires PostgreSQL ≥ 18 (ADR-001). Quickest way:

```bash
docker run -d --name papuma-sample-pg \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=papuma_sample \
  -p 5432:5432 postgres:18-alpine

dotnet run --project samples/Papuma.Kernel.Sample
```

(Podman works identically. A different port/credentials: override
`ConnectionStrings__papuma`.) The schema (tables, RLS, key indexes) is created
idempotently at startup — there is no migration step.

## Walk the workflow

```bash
BASE=http://localhost:5099   # check the launch output for the actual port

# 1. A product with 5 in stock — three writes, ONE atomic commit.
PRODUCT=$(curl -s -X POST $BASE/products -H 'content-type: application/json' \
  -d '{"name":"Espresso Machine","price":100,"initialStock":5}' | jq -r .id)

# 2. A small order auto-approves; the stock decrement is the atomic
#    conditional decrement — try quantity 99 and you get a typed 409.
curl -s -X POST $BASE/orders -H 'content-type: application/json' \
  -d "{\"productId\":\"$PRODUCT\",\"quantity\":1,\"customerEmail\":\"alice@example.com\"}"

# 3. An expensive order (> 500) enters the human-in-the-loop path …
ORDER=$(curl -s -X POST $BASE/orders -H 'content-type: application/json' \
  -d "{\"productId\":\"$PRODUCT\",\"quantity\":6,\"customerEmail\":\"bob@example.com\"}" | jq -r .id)
# … status: "PendingApproval", and the workflow handler has materialized
# an ApprovalTask document with the deterministic id "approval-<orderId>".

# 4. The human decision is a normal write. Decide within 2 minutes —
#    otherwise the escalation poller flips the task to "Escalated" (the dueAt timer).
curl -s -X POST $BASE/approvals/approval-$ORDER/decide \
  -H 'content-type: application/json' \
  -d '{"decision":"Approved","decidedBy":"manager@example.com"}'
# Decide twice and the second caller gets 409 — expectedVersion at work.

# 5. The order is now Approved, and its history is the workflow execution log:
curl -s $BASE/orders/$ORDER/history | jq
# v1 Insert  actor=bob@example.com      (customerEmail: only {"changed": true}!)
# v2 Update  actor=manager@example.com  (status: PendingApproval → Approved)

# 6. Reject an order instead, and the saga compensates: the stock comes back.
```

## Where the recipes became real

- **workflow-saga**: the whole loop *order → task document → human write →
  handler → order transition* lives in
  [`OrderWorkflowHandler`](Handlers/OrderWorkflowHandler.cs); the timer in
  [`ApprovalEscalationService`](Services/ApprovalEscalationService.cs).
- **realtime-ui-notifications**: [`OrderUiNotifier`](Handlers/OrderUiNotifier.cs)
  pushes ids + changed paths (never values) to `/hub/shop`.
- **MCP**: point an MCP-capable agent at `$BASE/mcp` and ask for
  `get_feed_lag`, `get_document_history` or `get_model_inventory`.

## Things worth breaking

- Order more than the stock → typed 409, stock untouched, no retry loop.
- Kill the app between order placement and approval → restart, nothing is lost:
  checkpoints resume the feed, the task document *is* the waiting state.
- Let an approval sit past its `dueAt` → the poller escalates it; deciding and
  escalating race safely (`expectedVersion` — one of them wins, typed).
- Watch `/health` while a handler throws: stop-the-line keeps order, the lag
  metric makes it visible.
