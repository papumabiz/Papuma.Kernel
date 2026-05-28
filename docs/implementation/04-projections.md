# 04 – Projections: Workers, Checkpoints und Parallelität

## Das Interface: `IProjectionHandler`

Jede Projection implementiert dieses Interface:

```csharp
// src/Kernel/Projections/IProjectionHandler.cs
public interface IProjectionHandler
{
    string Name { get; }
    IReadOnlyCollection<string> EventTypes { get; }
    Task HandleAsync(ChangeRecord record, CancellationToken ct = default);
}
```

`Name` ist der eindeutige Name der Projection – derselbe String, der als Primary Key in `projection_checkpoint` verwendet wird.
`EventTypes` erlaubt SQL-seitiges Vorfiltern, damit nicht jede Projection den gesamten Feed laden muss.

---

## Der `ProjectionWorker`

Der Worker läuft als `BackgroundService` und verarbeitet Events mit **at-least-once** Semantik:

- Event wird verarbeitet
- danach wird Checkpoint gespeichert
- bei Crash dazwischen kann das Event erneut kommen

Darum müssen Handler idempotent sein (siehe unten).

```csharp
// src/Kernel/Projections/ProjectionWorker.cs
public class ProjectionWorker : BackgroundService
{
    private readonly IProjectionHandler _handler;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ProjectionWorker> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(100);
    private const int MaxAttemptsPerEvent = 10;

    public ProjectionWorker(
        IProjectionHandler handler,
        NpgsqlDataSource dataSource,
        ILogger<ProjectionWorker> logger)
    {
        _handler = handler;
        _dataSource = dataSource;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProjectionWorker [{Name}] started.", _handler.Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed < 100)
                    await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProjectionWorker [{Name}]", _handler.Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("ProjectionWorker [{Name}] stopped.", _handler.Name);
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var checkpoint = await LoadCheckpointAsync(conn, ct);
        var changes = await LoadChangesAsync(conn, checkpoint, ct);

        foreach (var change in changes)
        {
            try
            {
                await _handler.HandleAsync(change, ct);
                await SaveCheckpointAsync(conn, change.SequenceId, ct);
            }
            catch (Exception ex)
            {
                var movedToDeadLetter = await RegisterFailureAsync(conn, change, ex, ct);
                if (movedToDeadLetter)
                {
                    await SaveCheckpointAsync(conn, change.SequenceId, ct);
                    continue;
                }

                throw;
            }
        }

        return changes.Count;
    }

    private async Task<long> LoadCheckpointAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT last_sequence_id
            FROM projection_checkpoint
            WHERE projection_name = @name
            """;
        cmd.Parameters.AddWithValue("name", _handler.Name);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long id ? id : 0L;
    }

    private async Task<List<ChangeRecord>> LoadChangesAsync(
        NpgsqlConnection conn,
        long fromSequenceId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sequence_id, entity, entity_id, event_type, version,
                   payload::text, timestamp
            FROM change_feed
            WHERE sequence_id > @lastSeen
              AND redacted = FALSE
              AND event_type = ANY(@eventTypes)
                            AND NOT EXISTS (
                                    SELECT 1
                                    FROM projection_failures pf
                                    WHERE pf.projection_name = @name
                                        AND pf.sequence_id = change_feed.sequence_id
                                        AND pf.attempts < @maxAttempts
                                        AND pf.next_retry_at > NOW()
                            )
            ORDER BY sequence_id
            LIMIT 100
            """;

        cmd.Parameters.AddWithValue("lastSeen", fromSequenceId);
                cmd.Parameters.AddWithValue("name", _handler.Name);
                cmd.Parameters.AddWithValue("maxAttempts", MaxAttemptsPerEvent);
        cmd.Parameters.AddWithValue("eventTypes", _handler.EventTypes.ToArray());

        var records = new List<ChangeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new ChangeRecord(
                SequenceId: reader.GetInt64(0),
                Entity: reader.GetString(1),
                EntityId: reader.GetString(2),
                EventType: reader.GetString(3),
                Version: reader.GetInt32(4),
                PayloadJson: reader.GetString(5),
                Timestamp: reader.GetFieldValue<DateTimeOffset>(6)
            ));
        }

        return records;
    }

    private async Task SaveCheckpointAsync(
        NpgsqlConnection conn,
        long sequenceId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projection_checkpoint (projection_name, last_sequence_id, updated_at)
            VALUES (@name, @sequenceId, NOW())
            ON CONFLICT (projection_name)
            DO UPDATE SET last_sequence_id = @sequenceId, updated_at = NOW()
            """;
        cmd.Parameters.AddWithValue("name", _handler.Name);
        cmd.Parameters.AddWithValue("sequenceId", sequenceId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> RegisterFailureAsync(
        NpgsqlConnection conn,
        ChangeRecord change,
        Exception ex,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projection_failures
                (projection_name, sequence_id, event_type, attempts, last_error, next_retry_at)
            VALUES
                (@name, @sequenceId, @eventType, 1, @error, NOW() + INTERVAL '5 seconds')
            ON CONFLICT (projection_name, sequence_id)
            DO UPDATE SET
                attempts = projection_failures.attempts + 1,
                last_error = EXCLUDED.last_error,
                next_retry_at = NOW() + INTERVAL '5 seconds',
                updated_at = NOW()
            RETURNING attempts
            """;

        cmd.Parameters.AddWithValue("name", _handler.Name);
        cmd.Parameters.AddWithValue("sequenceId", change.SequenceId);
        cmd.Parameters.AddWithValue("eventType", change.EventType);
        cmd.Parameters.AddWithValue("error", ex.ToString());

        var attempts = (int)(await cmd.ExecuteScalarAsync(ct) ?? 1);
        return attempts >= MaxAttemptsPerEvent;
    }
}
```

---

## Beispiel: Eine konkrete Projection

```csharp
// src/App/Features/Users/Projections/UserReadModelProjection.cs
public class UserReadModelProjection : IProjectionHandler
{
    public string Name => "user_read_model";
    public IReadOnlyCollection<string> EventTypes => new[] { "UserEmailUpdated" };

    private readonly NpgsqlDataSource _dataSource;

    public UserReadModelProjection(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task HandleAsync(ChangeRecord record, CancellationToken ct = default)
    {
        switch (record.EventType)
        {
            case "UserEmailUpdated":
                await HandleEmailUpdated(record, ct);
                break;
        }
    }

    private async Task HandleEmailUpdated(ChangeRecord record, CancellationToken ct)
    {
        var email = record.Version switch
        {
            1 => JsonSerializer.Deserialize<UserEmailUpdatedV1>(record.PayloadJson)!.Email,
            2 => JsonSerializer.Deserialize<UserEmailUpdatedV2>(record.PayloadJson)!.Value,
            _ => throw new InvalidOperationException(
                $"Unknown version {record.Version} for UserEmailUpdated")
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET email = @email, updated_at = NOW()
            WHERE id = @id::uuid
            """;
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("id", record.EntityId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

---

## Registrierung in ASP.NET Core

```csharp
// Program.cs
builder.Services.AddSingleton<UserReadModelProjection>();
builder.Services.AddSingleton<SearchIndexProjection>();
builder.Services.AddSingleton<AnalyticsProjection>();

builder.Services.AddSingleton<IHostedService>(sp => new ProjectionWorker(
    sp.GetRequiredService<UserReadModelProjection>(),
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<ILogger<ProjectionWorker>>()
));

builder.Services.AddSingleton<IHostedService>(sp => new ProjectionWorker(
    sp.GetRequiredService<SearchIndexProjection>(),
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<ILogger<ProjectionWorker>>()
));
```

Oder als Extension:

```csharp
// src/Kernel/Projections/ProjectionExtensions.cs
public static class ProjectionExtensions
{
    public static IServiceCollection AddProjection<THandler>(
        this IServiceCollection services)
        where THandler : class, IProjectionHandler
    {
        services.AddSingleton<THandler>();
        services.AddSingleton<IHostedService>(sp => new ProjectionWorker(
            sp.GetRequiredService<THandler>(),
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ILogger<ProjectionWorker>>()
        ));

        return services;
    }
}
```

---

## Parallelität und Reihenfolge

**Zwischen Projections: Parallelität erlaubt.**  
Jede Projection läuft in einem eigenen Worker.

**Innerhalb einer Projection: Reihenfolge erzwungen.**  
Events werden in `sequence_id` Reihenfolge verarbeitet.

```
change_feed
  ├── UserReadModelProjection   (seq 1 → 2 → 3 → 4 ...)
  ├── SearchIndexProjection     (seq 1 → 2 → 3 ...)     ← kann hinterher sein
  └── AnalyticsProjection       (seq 1 → ...)            ← kann weit hinterher sein
```

---

## Idempotenz-Regeln (Pflicht)

At-least-once bedeutet: Wiederholte Verarbeitung ist normal.

- Read Models mit `INSERT ... ON CONFLICT DO UPDATE`
- Side Effects mit Dedup-Key (`projection_name + sequence_id`)
- Externe APIs nur mit idempotency token aufrufen

Ohne diese Regeln sind Replay und Recovery nicht sicher.

## Checkpoint-Strategien

Es gibt zwei valide Strategien:

1. Pro Event speichern: präzise Recovery, mehr Schreiblast
2. Pro Batch speichern: weniger Schreiblast, mehr Reprocessing bei Crash

Foundation startet sinnvoll mit Pro Event. Bei höherer Last kann auf Batch umgestellt werden.

## Polling-Intervall

100 bis 500 ms Polling ist in vielen Systemen bereits quasi-echtzeitfähig.
`LISTEN/NOTIFY` kann später ergänzt werden, ist aber kein Muss.

---

## Replay: Eine Projection von vorne starten

Replay bedeutet: Checkpoint auf 0 zurücksetzen, Worker verarbeitet den Feed neu.

```csharp
// src/Kernel/Projections/ReplayService.cs
public class ReplayService
{
    private readonly NpgsqlDataSource _dataSource;

    public ReplayService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task ResetCheckpointAsync(string projectionName, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE projection_checkpoint
            SET last_sequence_id = 0, updated_at = NOW()
            WHERE projection_name = @name
            """;
        cmd.Parameters.AddWithValue("name", projectionName);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

Replay ist regulärer Betrieb, kein Sonderfall.

## Zusatztabelle für Fehlerverfolgung

```sql
CREATE TABLE projection_failures (
    projection_name TEXT        NOT NULL,
    sequence_id     BIGINT      NOT NULL,
    event_type      TEXT        NOT NULL,
    attempts        INT         NOT NULL,
    last_error      TEXT        NOT NULL,
    next_retry_at   TIMESTAMPTZ NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (projection_name, sequence_id)
);
```
