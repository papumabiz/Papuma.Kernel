# Papuma Kernel

<p align="center"><img src="assets/logo.png" width="300" /></p>

Papuma Kernel is a small .NET 10 library for a PostgreSQL-based application kernel:
**documents as the source of truth, an automatically derived change feed, and
privacy policies applied before anything reaches the feed** (document-sourced CQRS).

> **vNEXT (1.0):** rebuilt from scratch on PostgreSQL ≥ 18 (`RETURNING OLD/NEW`) —
> no migration path from 0.x (see [CHANGELOG](CHANGELOG.md)).
>
> - Start here: [docs/vNEXT/getting-started.md](docs/vNEXT/getting-started.md)
> - Architecture: [docs/vNEXT/architecture.md](docs/vNEXT/architecture.md) ·
>   Decisions: [docs/vNEXT/adr/](docs/vNEXT/adr) ·
>   Background: [docs/vNEXT/concepts.md](docs/vNEXT/concepts.md)

## Contents

- `src/Papuma.Kernel`: core library (store, diff engine, policies, feeds, hosting)
- `src/Papuma.Kernel.AspNetCore`: optional ASP.NET Core integration (tenant middleware, feed lag health check)
- `tests/Papuma.Kernel.Tests`: integration tests (PostgreSQL 18 via Testcontainers)
- `docs/vNEXT`: architecture, ADRs, concepts, recipes, plan · `docs/v1`: archived v1 docs

## Getting Started

```bash
dotnet build Papuma.Kernel.slnx
dotnet test Papuma.Kernel.slnx   # integration tests need Docker/Podman (PostgreSQL 18)
```

For the API walkthrough see [docs/vNEXT/getting-started.md](docs/vNEXT/getting-started.md).

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
