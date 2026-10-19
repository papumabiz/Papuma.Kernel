// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Core.Tests.Robustness;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Robustness;

/// <summary>The SQLite kernel as an engine under test for <see cref="SessionModelRunner"/>.</summary>
internal sealed class SqliteStoreAdapter(SqliteDocumentStore store) : IStoreAdapter
{
    public ISessionAdapter Open(ScopeContext scope) => new SqliteSessionAdapter(store.OpenSession(scope));
}

internal sealed class SqliteSessionAdapter(SqliteDocumentSession session) : ISessionAdapter
{
    public Task<SaveResult> SaveAsync(FreeDoc document, long expectedVersion) => session.SaveAsync(document, expectedVersion);

    public Task<DocumentResult<FreeDoc>?> LoadAsync(string id) => session.LoadAsync<FreeDoc>(id);

    public Task<SaveResult> DeleteAsync(string id, long expectedVersion) => session.DeleteAsync<FreeDoc>(id, expectedVersion);

    public Task<SaveResult> PatchAsync(string id, PatchSpec patch, long? expectedVersion) =>
        session.PatchAsync<FreeDoc>(
            id,
            p =>
            {
                switch (patch.Kind)
                {
                    case PatchKind.SetTitle:
                        p.Set(x => x.Title, patch.Text);
                        break;
                    case PatchKind.RemoveTitle:
                        p.Remove(x => x.Title);
                        break;
                    default:
                        p.Increment(x => x.Counter, patch.By);
                        break;
                }
            },
            expectedVersion);

    public Task<SaveResult> RollbackAsync(string id, long toVersion, long expectedVersion) =>
        session.RollbackAsync<FreeDoc>(id, toVersion, expectedVersion);

    public Task<IReadOnlyList<ChangeRecord>> HistoryAsync(string id) => session.GetHistoryAsync<FreeDoc>(id);

    public Task CommitAsync() => session.CommitAsync();

    public ValueTask DisposeAsync() => session.DisposeAsync();
}
