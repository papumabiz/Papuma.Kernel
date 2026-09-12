## What and why

<!-- The diff shows the what. Explain the why, and what you considered instead. -->

## Checklist

- [ ] `dotnet build Papuma.Kernel.slnx` is warning-free
- [ ] `dotnet test Papuma.Kernel.slnx` is green (Postgres suite included — needs Docker/Podman)
- [ ] Tests cover the change, or the PR says why none are possible
- [ ] Public types touched carry XML documentation
- [ ] New source files carry the SPDX header
- [ ] `CHANGELOG.md` updated under `Unreleased` if a consumer can notice this
- [ ] A design decision here is recorded as a new ADR in `docs/adr/`, not an edit to an existing one
