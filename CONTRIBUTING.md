# Contributing to Papuma Kernel

Thanks for taking a look. This is a small, opinionated library — the fastest way
to get a change merged is to match what is already there.

## Before you write code

- **Bug fix?** Open an issue or go straight to a pull request with a failing test.
- **New behaviour or a changed public API?** Open an issue first. Papuma Kernel
  says no to a lot on purpose (see [what it deliberately is not](README.md#what-it-deliberately-is-not));
  a short discussion saves you from writing something that will be declined.
- **A design decision?** Design choices live in [ADRs](docs/adr/). If your change
  reverses or extends one, say which ADR in the issue — a merged change of that
  kind comes with a new ADR, not an edit to an old one.

## Building and testing

```bash
dotnet build Papuma.Kernel.slnx
dotnet test  Papuma.Kernel.slnx
```

Requirements:

- **.NET 10 SDK**
- **Docker or Podman** for the PostgreSQL suite — the integration tests start a
  real `postgres:18-alpine` container via Testcontainers. There is no mock and no
  in-memory provider; a change that only passes without a database is not tested.
- The `Papuma.Kernel.Local` (SQLite) suite needs nothing extra.

A green run is `dotnet test Papuma.Kernel.slnx` at the solution level, warning-free.

## Code conventions

The working rules are in [AGENTS.md](AGENTS.md) — they apply to humans too. The
short version:

- Small, local changes with a clear cause-and-effect chain.
- Prefer the BCL over another package; prefer no abstraction over a speculative one.
- Validate at the boundary, throw concrete parameterized exceptions.
- Public types get XML documentation.
- Every hand-written source file starts with the two SPDX header lines:

  ```csharp
  // SPDX-License-Identifier: MIT
  // SPDX-FileCopyrightText: 2026 Harald Lapp
  ```

- Production code in `src/`, tests mirroring it in `tests/`, samples in `samples/`.

## Tests

- No production change without test coverage or a written reason why none is possible.
- Validation changes cover the valid *and* the invalid input.
- Behaviour claimed about the database engine gets verified against the engine, not assumed.

## Pull requests

- One topic per pull request; keep unrelated reformatting out of it.
- Describe the *why*, not just the *what* — the diff already shows the what.
- Update `CHANGELOG.md` under an `Unreleased` heading when the change is visible
  to a consumer (API, behaviour, packaging, docs layout).
- CI must be green: build, tests and pack all run on every pull request.

## Releasing

Maintainers only. Packages go to nuget.org through
[`.github/workflows/release.yml`](.github/workflows/release.yml) with NuGet
Trusted Publishing (OIDC); no API key is stored anywhere.

One-time setup:

- **nuget.org:** a Trusted Publishing policy for this repository and the workflow
  file `release.yml`.
- **Secret `NUGET_USER`:** the nuget.org profile name (not the email address).
- **Environment `nuget-org`** (repository settings → Environments) with required
  reviewers. The publish job runs in it, so no push to nuget.org starts without
  an approval.

A release:

1. Set `<Version>` in `Directory.Build.props` and move the `Unreleased` entries in
   `CHANGELOG.md` under that version (`## X.Y.Z (date)`).
2. Optionally write curated release notes to `.github/release-notes/vX.Y.Z.md`;
   without them the release shows the version's CHANGELOG section.
3. Tag the commit `vX.Y.Z` (or `vX.Y.Z-suffix`) and push the tag. A tag that
   already exists can be published from the Actions tab or with
   `gh workflow run release.yml -f tag=vX.Y.Z`.
4. The build job tests and packs the tag; it fails if the tag is not exactly the
   packages' version or a package is missing. Approve the `nuget-org` deployment,
   and the publish job attests the packages and pushes them. The last job creates
   the GitHub release (a tag with a suffix as a pre-release); a release that
   already exists is left alone.

`publish-nuget.sh` publishes to GitHub Packages and is not part of a release.

## Security

Please do not open a public issue for a vulnerability — see [SECURITY.md](SECURITY.md).

## License

By contributing you agree that your contribution is licensed under the MIT
License, same as the rest of the project.
