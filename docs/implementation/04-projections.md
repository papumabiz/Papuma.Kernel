# 04 – Projections: Workers, Checkpoints und Parallelität

## Das Interface: `IProjectionHandler`

Jede Projection implementiert dieses Interface:

```csharp
// src/Kernel/Projections/IProjectionHandler.cs
public interface IProjectionHandler
{
    string Name { get; }
    Task HandleAsync(ChangeRecord record, CancellationToken ct = default);
}
```

`Name` ist der eindeutige Name der Projection – derselbe String, der als Primary Key in `projection_checkpoint` verwendet wird.

---

## Der `ProjectionWorker`

Der `ProjectionWorker` ist ein `BackgroundService` (ASP.NET Core), der in einer Endlosschleife den Change Feed pollt und Events an einen Handler weitergibt.

```csharp
// src/Kernel/Projections/ProjectionWorker.cs
public class ProjectionWorker : BackgroundService
{
    private readonly IProjectionHandler _handler;
    private readonly NpgsqlDataSource   _dataSource;
    private readonly ILogger<ProjectionWorker> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(100);

    public ProjectionWorker(
        IProjectionHandler handler,
        NpgsqlDataSource dataSource,
        ILogger<ProjectionWorker> logger)
    {
        _handler    = handler;
        _dataSource = dataSource;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProjectionWorker [{Name}] started.", _handler.Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);

                // Wenn weniger als ein volles Batch ankam: kurz warten
                if (processed < 100)
                    await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProjectionWorker [{Name}].", _handler.Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("ProjectionWorker [{Name}] stopped.", _handler.Name);
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var checkpoint = await LoadCheckpointAsync(conn, ct);
        var changes    = await LoadChangesAsync(conn, checkpoint, ct);

        foreach (var change in changes)
        {
            await _handler.HandleAsync(change, ct);
            await SaveCheckpointAsync(conn, change.SequenceId, ct);
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
            ORDER BY sequence_id
            LIMIT 100
            """;
        cmd.Parameters.AddWithValue("lastSeen", fromSequenceId);

        var records = new List<ChangeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new ChangeRecord(
                SequenceId:  reader.GetInt64(0),
                Entity:      reader.GetString(1),
                EntityId:    reader.GetString(2),
                EventType:   reader.GetString(3),
                Version:     reader.GetInt32(4),
                PayloadJson: reader.GetString(5),
                Timestamp:   reader.GetDateTime(6)
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
        cmd.Parameters.AddWithValue("name",       _handler.Name);
        cmd.Parameters.AddWithValue("sequenceId", sequenceId);

        await cmd.ExecuteNonQueryAsync(ct);
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

    private readonly NpgsqlDataSource _dataSource;

    public UserReadModelProjection(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task HandleAsync(ChangeRecord record, CancellationToken ct = default)
    {
        // Nur Events für User verarbeiten
        if (record.Entity != "User")
            return;

        switch (record.EventType)
        {
            case "UserEmailUpdated":
                await HandleEmailUpdated(record, ct);
                break;

            // weitere Event-Typen hier...
        }
    }

    private async Task HandleEmailUpdated(ChangeRecord record, CancellationToken ct)
    {
        // Versioniertes Deserialisieren – siehe 05-versionierung.md
        var email = record.Version switch
        {
            1 => JsonSerializer.Deserialize<UserEmailUpdatedV1>(record.PayloadJson)!.Email,
            2 => JsonSerializer.Deserialize<UserEmailUpdatedV2>(record.PayloadJson)!.Value,
            _ => throw new InvalidOperationException(
                    $"Unknown version {record.Version} for UserEmailUpdated")
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET email = @email, updated_at = NOW()
            WHERE id = @id::uuid
            """;
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("id",    record.EntityId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

---

## Registrierung in ASP.NET Core

Jede Projection wird als eigener `BackgroundService` registriert:

```csharp
// Program.cs
builder.Services.AddSingleton<IProjectionHandler, UserReadModelProjection>();
builder.Services.AddSingleton<IProjectionHandler, SearchIndexProjection>();
builder.Services.AddSingleton<IProjectionHandler, AnalyticsProjection>();

// Für jede Projection einen Worker registrieren
builder.Services.AddHostedService<ProjectionWorker>(sp =>
    new ProjectionWorker(
        sp.GetRequiredService<IEnumerable<IProjectionHandler>>()
          .First(h => h.Name == "user_read_model"),
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<ILogger<ProjectionWorker>>()
    ));
```

Oder eleganter mit einer Erweiterungsmethode:

```csharp
// src/Kernel/Projections/ProjectionExtensions.cs
public static class ProjectionExtensions
{
    public static IServiceCollection AddProjection<THandler>(
        this IServiceCollection services)
        where THandler : class, IProjectionHandler
    {
        services.AddSingleton<THandler>();
        services.AddHostedService(sp => new ProjectionWorker(
            sp.GetRequiredService<THandler>(),
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ILogger<ProjectionWorker>>()
        ));
        return services;
    }
}

// Nutzung in Program.cs:
builder.Services
    .AddProjection<UserReadModelProjection>()
    .AddProjection<SearchIndexProjection>()
    .AddProjection<AnalyticsProjection>();
```

---

## Parallelität und Reihenfolge

**Zwischen Projections: Parallelität erlaubt.**  
Jede Projection läuft in ihrem eigenen Worker-Thread, komplett unabhängig. `UserReadModelProjection` und `SearchIndexProjection` laufen parallel.

**Innerhalb einer Projection: Reihenfolge erzwungen.**  
Events für denselben Entity werden sequenziell verarbeitet – garantiert durch die `ORDER BY sequence_id` im Query.

```
change_feed
  ├── UserReadModelProjection   (seq 1 → 2 → 3 → 4 ...)
  ├── SearchIndexProjection     (seq 1 → 2 → 3 ...)     ← kann hinterher sein
  └── AnalyticsProjection       (seq 1 → ...)            ← kann weit hinterher sein
```

---

## Polling-Intervall: Warum 100ms reicht

Menschen nehmen Latenzen unter 100–500ms als "Echtzeit" wahr. Für die meisten Anwendungsfälle ist ein Polling-Intervall von 100ms vollkommen ausreichend.

Wenn echter Push gewünscht ist, kann später `LISTEN/NOTIFY` von PostgreSQL ergänzt werden – das ist ein optionales Upgrade, kein Pflichtbestandteil.

---

## Replay: Eine Projection von vorne starten

Replay bedeutet: Checkpoint auf 0 zurücksetzen, Worker läuft dann alle Events durch.

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
        await using var cmd  = conn.CreateCommand();
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

Replay ist kein Sonderfall. Es ist normaler Betrieb – wann immer Projection-Logik sich ändert.
