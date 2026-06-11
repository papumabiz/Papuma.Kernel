# Papuma Kernel

<p align="center"><img src="assets/logo.png" width="300" /></p>

Papuma Kernel is a small .NET 10 library for a PostgreSQL-based application kernel:
**documents as the source of truth, an automatically derived change feed, and
privacy policies applied before anything reaches the feed** (document-sourced CQRS).

> **vNEXT reboot in progress (branch `vnext`).** The kernel is being rebuilt from
> scratch on PostgreSQL ≥ 18 (`RETURNING OLD/NEW`) — no migration path from v1.
> The v1 sources remain in the repository until the final teardown (phase 10).
>
> - Start here: [docs/vNEXT/getting-started.md](docs/vNEXT/getting-started.md)
> - Architecture: [docs/vNEXT/architecture.md](docs/vNEXT/architecture.md) ·
>   Decisions: [docs/vNEXT/adr/](docs/vNEXT/adr) ·
>   Background: [docs/vNEXT/concepts.md](docs/vNEXT/concepts.md)
> - Progress: [docs/vNEXT/implementation-plan.md](docs/vNEXT/implementation-plan.md)

## Contents

- `src/Papuma.Kernel.Next`: **vNEXT** core library (ships as `Papuma.Kernel` after the teardown)
- `src/Papuma.Kernel.Next.AspNetCore`: vNEXT ASP.NET Core integration (tenant middleware, feed lag health check)
- `tests/Papuma.Kernel.Next.Tests`: vNEXT integration tests (PostgreSQL 18 via Testcontainers)
- `src/Papuma.Kernel`, `src/Papuma.Kernel.AspNetCore`: v1 (legacy, scheduled for removal)
- `docs/vNEXT`: architecture, ADRs, concepts, recipes, plan

## Getting Started

```bash
dotnet build Papuma.Kernel.slnx
dotnet test Papuma.Kernel.slnx   # integration tests need Docker/Podman (PostgreSQL 18)
```

For the API walkthrough see [docs/vNEXT/getting-started.md](docs/vNEXT/getting-started.md).

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
