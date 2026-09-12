# AGENTS.md

## Purpose

This repository contains `Papuma.Kernel`, a small .NET 10 library for a PostgreSQL-based, document-sourced application kernel (documents as the source of truth, derived change feed, privacy policies — see `docs/architecture.md`). Requires PostgreSQL ≥ 18; integration tests run against a real PostgreSQL 18 container (Testcontainers).

This document defines the working rules for GitHub Copilot and other agents in this workspace.

## Core Rules

- Prefer small, local changes with a clear cause-and-effect chain.
- Keep public APIs stable unless a change is explicitly requested.
- Use modern C# and .NET 10 patterns, but avoid unnecessary abstraction.
- Prefer BCL solutions over additional packages.
- Pay attention to nullability, guard clauses, and meaningful exceptions.
- Avoid changes in `bin/`, `obj/`, and generated files.

## Code Quality

- Every new or modified public type should get XML documentation when the API becomes easier to consume or document.
- Non-obvious logic should receive short, targeted comments. Avoid verbose comments and explanations of the obvious.
- Names should be precise and domain-oriented. Prefer descriptive type, method, and parameter names.
- Validation belongs at the boundary, not deep inside the implementation.
- Errors should use concrete, parameterized exceptions with valid parameter names.

## License Headers

- Every hand-written source file starts with the two SPDX lines:
  - `// SPDX-License-Identifier: MIT`
  - `// SPDX-FileCopyrightText: 2026 Harald Lapp`
- Generated files, build artifacts, and external third-party files should not be rewritten.

## .NET 10 Guidelines

- Target framework is `net10.0`.
- Use nullable-safe code.
- Use `async`/`await` only when real I/O is involved.
- Prefer `ArgumentNullException.ThrowIfNull(...)` and clear guard checks.
- When using regex, keep it compiled, time-bounded, and narrowly defined.

## Tests

- Never change production code without suitable test coverage or a documented reason.
- Prefer small, focused tests for the affected unit.
- If a change affects validation logic, cover valid and invalid inputs.

## Build and Verification

- Standard build: `dotnet build Papuma.Kernel.slnx`
- Standard tests: `dotnet test Papuma.Kernel.slnx`
- If only a narrow area is affected, verify it first with the smallest sensible test or build scope.

## Repository Conventions

- Production code lives under `src/` (`Papuma.Kernel`, `.Local`, `.Core`, `.AspNetCore`, `.Mcp`, `.FSharp`).
- Tests live under `tests/`, mirroring the project they cover.
- Documentation lives under `docs/` — [`docs/README.md`](docs/README.md) is the map.
  `docs/legacy/` is frozen pre-1.0 material; never cite it as current.
- Changes should remain compatible with the repository's existing style.
