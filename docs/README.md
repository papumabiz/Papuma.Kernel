# Papuma Kernel — Documentation

The same doc set ships inside every Papuma NuGet package under `docs/`, so a
coding agent can read the docs of exactly the version it builds against. Pages
describe the PostgreSQL kernel unless they say otherwise; `Papuma.Kernel.Local`
(SQLite) differences are collected in the playbook's
[Differences section](ai/papuma-kernel-playbook.md#differences-when-using-papumakernellocal-sqlite-embedded).

| Start here | |
|---|---|
| [tutorial.md](tutorial.md) | build one application end to end, guided |
| [getting-started.md](getting-started.md) | the terse API tour |
| [concepts.md](concepts.md) | the *why* behind every mechanism, narrative |

| Reference | |
|---|---|
| [architecture.md](architecture.md) | how the pieces fit together |
| [adr/](adr/) | 23 Architecture Decision Records — each a single, dated, reversible choice |
| [recipes/](recipes/) | pattern guides on kernel primitives (sagas, read models, bridges, …) |
| [gdpr.md](gdpr.md) | export, data inventory, history redaction |
| [observability.md](observability.md) | diagnostics, health checks, the dashboard |
| [feed-wire-format.md](feed-wire-format.md) | the cross-language contract feed consumers rely on |

| Other | |
|---|---|
| [ai/](ai/) | the coding-agent doc set, shipped inside the NuGet packages (see also [llms.txt](https://github.com/papumabiz/Papuma.Kernel/blob/master/llms.txt)) |
| [analyses/](analyses/) | explorations that are not decisions — ideas, trade-offs, open questions |
| [factsheets/](https://github.com/papumabiz/Papuma.Kernel/tree/master/docs/factsheets) | one-pagers for evaluation (general and technical, with PDFs) — repository only, not shipped |
| [legacy/](https://github.com/papumabiz/Papuma.Kernel/tree/master/docs/legacy) | frozen 0.x/v1 material, mostly German, unmaintained |
| [feedback/](https://github.com/papumabiz/Papuma.Kernel/tree/master/docs/feedback) | feedback from applications built on the kernel (friction, gaps, what works) — repository only, not shipped |
