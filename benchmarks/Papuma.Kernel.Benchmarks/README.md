# Papuma.Kernel Benchmarks

```bash
dotnet run --project benchmarks/Papuma.Kernel.Benchmarks -c Release
```

## Diff engine (risk #4 of the implementation plan)

`JsonDiffEngine.Diff` runs once per write inside the session — its cost adds
directly to every save/patch latency. Measured 2026-06-12 (ShortRun, .NET 10,
i7-1260P; document shape: 60% flat scalars, 20% nested objects to depth 3,
20% atomic arrays of 10 elements; `FieldCount` = leaf fields):

| Leaves | No change | 1 leaf changed | 10% changed | Insert | Allocated (typical) |
|-------:|----------:|---------------:|------------:|-------:|--------------------:|
| 10     | 1.8 µs    | 2.0 µs         | 1.9 µs      | 1.6 µs | ~2 KB               |
| 100    | 16 µs     | 16 µs          | 19 µs       | 16 µs  | ~8–20 KB            |
| 1,000  | 275 µs    | 386 µs         | 263 µs      | 329 µs | ~75–194 KB          |
| 10,000 | 4.0 ms    | 3.7 ms         | 3.7 ms      | 5.0 ms | ~0.8–1.9 MB         |

Observations:

- **Scaling is linear in the leaf count** — no pathological cases; the cost is
  dominated by walking both objects and `DeepEquals` on the leaves, not by the
  number of *changes* (a no-change save costs the same as a 10% change).
- **Inserts allocate roughly double** (every leaf becomes a diff entry with a
  cloned value).

## The resulting limit recommendation (documented in ADR-004)

- **≤ ~1,000 leaf fields: a non-topic.** The diff costs well under 0.5 ms —
  less than the Postgres roundtrip it accompanies. This covers the intended
  aggregate sizes by a wide margin.
- **~1,000–10,000 leaves: works, but pay attention.** The diff adds up to a few
  hundred µs and ~100–200 KB of allocations per write. Acceptable for rarely
  written documents; for hot writers, check whether the aggregate is cut too
  large (a frequently changing sub-collection usually wants to be its own
  document type, cf. concepts §17's inventory-vs-product split).
- **> ~10,000 leaves: remodel.** Milliseconds and MB-scale allocations per
  write are a symptom, not the problem: an aggregate this large almost always
  bundles independent lifecycles. *Embed by default* (architecture §5) has its
  boundary exactly here — what changes together stays together, what changes
  independently moves out.

The numbers are per-write CPU costs and scale with write throughput, not with
readers or handlers (the diff is computed once and stored).

## Feed throughput baseline (post-1.0 prep item)

```bash
dotnet run --project benchmarks/Papuma.Kernel.Benchmarks -c Release -- feed
```

End-to-end against PostgreSQL 18 in a local container (Testcontainers/Podman,
i7-1260P). 10,000 documents seeded, then the same feed drained by fresh handler
sets. Measured 2026-06-12:

| Scenario | Rate |
|---|---:|
| Write path: 4 parallel sessions, 50 saves/commit | **898 writes/s** (~1.1 ms per save incl. diff + change insert) |
| 1 no-op handler — the engine ceiling | **26,904 deliveries/s** (~37 µs engine overhead per delivery) |
| 4 no-op handlers — read amplification | 31,144 total/s (≈ 7,800/s per handler — linear, cheap) |
| 1 projection handler (1 SQL upsert per change) | **1,416 deliveries/s** |
| 4 projection handlers — latency coupling | 1,248 total/s (≈ 312/s *per handler* — wall time is additive) |

What the numbers mean for the three scaling triggers (concepts §14):

- **The engine is not the limit.** ~27k deliveries/s for a no-op handler means
  checkpoint + gapless read overhead is ~37 µs — limits 1–3 are entirely about
  what *handlers do*, never about the engine loop.
- **The §14 estimate is confirmed and refined:** a realistic projection handler
  (one idempotent upsert per change) sustains **~1,400 changes/s** on local
  hardware — the per-change SQL roundtrip (~0.7 ms) dominates everything.
- **Limit 1 (latency coupling) is real and additive:** four projection handlers
  drain at ~312/s *each*, because cycles run handlers sequentially — exactly
  the case `Task.WhenAll` parallelization would solve. Watch
  `cycle.duration` vs `handler.duration`.
- **Limit 3 (read amplification) is cheap:** four no-op handlers still clear
  ~7,800/s each; the SQL-side type filter only pays off at high volume × many
  handlers that discard most deliveries.
- **Headroom math for applications:** sustained write rate ÷ slowest handler's
  drain rate must stay well below 1. Example: 900 writes/s against a 1,400/s
  projection = 0.64 — keeps up, but a flash-sale burst builds visible (and
  observable) lag that drains afterwards.

Local-container numbers are optimistic on network latency (no real RTT to the
database); on managed Postgres, expect the projection-handler rate to drop with
the roundtrip time — measure in your environment, the probe is reusable.
