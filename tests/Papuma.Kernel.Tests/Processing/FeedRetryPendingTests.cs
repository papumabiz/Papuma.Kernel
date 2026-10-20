// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Testing;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Processing;

/// <summary>
/// A handler waiting out a retry backoff does no work each poll cycle (review P-15): the
/// batch is cut at the record whose retry is pending, and nothing behind it is mapped.
/// Delivery semantics stay: poison records before the blocked one are skipped, nothing
/// behind it is delivered, and the retry runs when it is due.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FeedRetryPendingTests
{
    private readonly PostgresFixture _fixture;

    public FeedRetryPendingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record PendingDoc(string Id, string Name);

    private static readonly KernelModel Model = new KernelModelBuilder().Document<PendingDoc>().Build();

    private async Task<(ScratchPostgresDatabase Db, List<string> Ids, List<long> Seqs)> NewDatabaseWithDocsAsync(int count)
    {
        var db = await ScratchPostgresDatabase.CreateAsync(_fixture.Database);
        await SchemaManager.EnsureSchemaAsync(db.DataSource, Model);
        var store = new DocumentStore(db.DataSource, Model);

        var ids = new List<string>();
        await using (var session = store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}")))
        {
            for (var i = 0; i < count; i++)
            {
                var id = Guid.NewGuid().ToString("N");
                await session.SaveAsync(new PendingDoc(id, "doc"), 0);
                ids.Add(id);
            }

            await session.CommitAsync();
        }

        var seqs = new List<long>();
        await using var cmd = db.DataSource.CreateCommand("SELECT seq FROM papuma.change ORDER BY seq");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            seqs.Add(reader.GetInt64(0));
        }

        return (db, ids, seqs);
    }

    private sealed class RecordingHandler(HashSet<string>? failing = null) : IChangeHandler
    {
        public string Name { get; } = $"pending-{Guid.NewGuid():N}";

        public List<string> Delivered { get; } = [];

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            Delivered.Add(change.DocumentId);
            if (failing?.Contains(change.DocumentId) == true)
            {
                throw new InvalidOperationException("simulated failure");
            }

            return Task.CompletedTask;
        }
    }

    private static Task ExecuteAsync(ScratchPostgresDatabase db, string sql, params (string Name, object Value)[] parameters) =>
        db.DataSource.CreateCommand(sql).WithParameters(parameters).ExecuteNonQueryAsync();

    [Fact]
    public async Task RowsBehindAPendingRetry_AreNotMapped()
    {
        var (db, ids, seqs) = await NewDatabaseWithDocsAsync(3);
        await using var _ = db;
        var handler = new RecordingHandler(failing: [ids[0]]);
        using var processor = new ChangeFeedProcessor(db.DataSource, [handler], new ChangeFeedProcessorOptions
        {
            BaseRetryDelay = TimeSpan.FromHours(1),
            MaxRetryDelay = TimeSpan.FromHours(1),
        });

        await processor.ProcessOnceAsync(); // the first record fails: its retry is an hour away
        Assert.Equal([ids[0]], handler.Delivered);

        // A row that cannot be mapped (a diff must be an object), behind the waiting record.
        await ExecuteAsync(db, "UPDATE papuma.change SET diff = '[]'::jsonb WHERE seq = @seq", ("seq", seqs[2]));

        // Pending retry: the cycle neither maps the broken row nor delivers anything.
        Assert.Equal(0, await processor.ProcessOnceAsync());
        Assert.Equal(0, await processor.ProcessOnceAsync());
        Assert.Equal([ids[0]], handler.Delivered);
    }

    [Fact]
    public async Task AnUnmappableRow_StillFailsTheCycle_WhenItIsNotBehindAPendingRetry()
    {
        var (db, _, seqs) = await NewDatabaseWithDocsAsync(3);
        await using var _ = db;
        await ExecuteAsync(db, "UPDATE papuma.change SET diff = '[]'::jsonb WHERE seq = @seq", ("seq", seqs[1]));
        var handler = new RecordingHandler();
        using var processor = new ChangeFeedProcessor(db.DataSource, [handler]);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessOnceAsync());
        Assert.Contains($"seq {seqs[1]}", ex.Message);
    }

    [Fact]
    public async Task PoisonBeforeAPendingRetry_IsSkipped_AndNothingBehindThePendingRetryIsDelivered()
    {
        var (db, ids, seqs) = await NewDatabaseWithDocsAsync(3);
        await using var _ = db;
        var handler = new RecordingHandler();
        using var processor = new ChangeFeedProcessor(db.DataSource, [handler], new ChangeFeedProcessorOptions { MaxAttempts = 3 });
        await processor.ProcessOnceAsync(); // registers the checkpoint ...
        await processor.ResetCheckpointAsync(handler.Name); // ... and starts over, so nothing was delivered
        handler.Delivered.Clear();

        // Record 1 is poison (attempts >= MaxAttempts); record 2 waits out a backoff.
        await ExecuteAsync(db,
            """
            INSERT INTO papuma.failure (handler_name, seq, attempts, last_error, next_retry_at)
            VALUES (@name, @poison, 5, 'x', now()), (@name, @pending, 1, 'x', now() + interval '1 hour')
            """,
            ("name", handler.Name), ("poison", seqs[0]), ("pending", seqs[1]));

        Assert.Equal(0, await processor.ProcessOnceAsync());
        Assert.Empty(handler.Delivered); // 1 skipped, 2 waiting, 3 held back

        // Once the retry is released, the feed continues behind the skipped poison record.
        Assert.True(await processor.RetryFailureAsync(handler.Name, seqs[1]));
        Assert.Equal(2, await processor.ProcessOnceAsync());
        Assert.Equal(ids.Skip(1), handler.Delivered);
    }
}

file static class CommandExtensions
{
    public static NpgsqlCommand WithParameters(this NpgsqlCommand command, (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
