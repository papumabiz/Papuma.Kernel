# 04 – Projections: Workers, Checkpoints und Parallelität

## Das Interface: `IProjectionHandler`

Jede Projection implementiert dieses Interface:

```csharp
// src/Kernel/Projections/IProjectionHandler.cs
public interface IProjectionHandler
{
    string Name { get; }
    IReadOnlyCollection<string> EventTypes { get; }
    Task HandleAsync(
        ChangeRecord record,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct = default);
}
```

`Name` ist der eindeutige Name der Projection – derselbe String, der als Primary Key in `projection_checkpoint` verwendet wird.
`EventTypes` erlaubt SQL-seitiges Vorfiltern, damit nicht jede Projection den gesamten Feed laden muss.

> **Warum bekommt der Handler `NpgsqlConnection` + `NpgsqlTransaction`?**
>
> Damit Handler-Write und Checkpoint-Update in **einer** Transaktion passieren können. Ohne diese Kopplung gibt es ein Konsistenzproblem: Wenn die Applikation zwischen Handler-Write und Checkpoint-Update crasht, wurde der Handler-Write committed, aber der Checkpoint nicht aktualisiert. Beim Neustart wird das Event erneut verarbeitet – bei nicht-idempotenten Operationen (Zähler, Benachrichtigungen, externe API-Calls) führt das zu Duplikaten.
>
> Mit der transaktionalen Kopplung wird bei einem Crash die gesamte Transaktion zurückgerollt. Es gibt keinen inkonsistenten Zustand.

Für Projections, die in **externe Systeme** schreiben (Elasticsearch, Redis, E-Mail), kann die Transaktion nicht helfen. Hier muss Idempotenz im Handler sichergestellt werden:

```csharp
// src/Kernel/Projections/IExternalProjectionHandler.cs
public interface IExternalProjectionHandler
{
    string Name { get; }
    IReadOnlyCollection<string> EventTypes { get; }

    // Kein Transaction-Parameter → Idempotenz ist Pflicht
    Task HandleAsync(ChangeRecord record, CancellationToken ct = default);
}
```

---

## Konfiguration: `ProjectionWorkerOptions`

```csharp
// src/Kernel/Projections/ProjectionWorkerOptions.cs
public class ProjectionWorkerOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);
    public int BatchSize { get; set; } = 100;
    public int MaxAttemptsPerEvent { get; set; } = 10;

    /// <summary>
    /// Basis-Delay für exponentielles Backoff bei Fehlern.
    /// Formel: MIN(BaseRetryDelay * 2^(attempts-1), MaxRetryDelay)
    /// </summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximaler Delay bei Retries (Cap für exponentielles Backoff).
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
}
```

---

## Der `ProjectionWorker`

Der Worker läuft als `BackgroundService` und verarbeitet Events mit **at-least-once** Semantik:

- Event wird verarbeitet
- danach wird Checkpoint gespeichert
- beides in **einer Transaktion** (für DB-basierte Projections)
- bei Crash dazwischen wird die gesamte Transaktion zurückgerollt

Darum müssen Handler idempotent sein (siehe unten).

Aktueller Stand:

- Der Worker kann optional mit einem `ScopeContext` betrieben werden.
- Der Projection-Name wird scope-spezifisch gebildet (z.B. `user_read_model@Tenant:acme` oder `user_read_model@Platform`).

```csharp
// src/Kernel/Projections/ProjectionWorker.cs
public class ProjectionWorker : BackgroundService
{
    private readonly IProjectionHandler _handler;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ProjectionWorker> _logger;
    private readonly ProjectionWorkerOptions _options;
    private readonly Channel<ReplayRequest> _replayChannel =
        Channel.CreateBounded<ReplayRequest>(1);

    public ProjectionWorker(
        IProjectionHandler handler,
        NpgsqlDataSource dataSource,
        ILogger<ProjectionWorker> logger,
        ProjectionWorkerOptions? options = null)
    {
        _handler = handler;
        _dataSource = dataSource;
        _logger = logger;
        _options = options ?? new ProjectionWorkerOptions();
    }

    /// <summary>
    /// Fordert einen Replay an. Der Worker prüft das Signal am Anfang jedes Loops.
    /// Da der Worker single-threaded ist, gibt es keine Race Condition.
    /// </summary>
    public async Task RequestReplayAsync(CancellationToken ct = default)
    {
        await _replayChannel.Writer.WriteAsync(new ReplayRequest(), ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProjectionWorker [{Name}] started.", _handler.Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Prüfe ob ein Replay angefordert wurde
            if (_replayChannel.Reader.TryRead(out _))
            {
                _logger.LogInformation(
                    "ProjectionWorker [{Name}] replay requested, resetting checkpoint.",
                    _handler.Name);
                await ResetCheckpointAsync(stoppingToken);

                if (_handler is IReplayableProjection replayable)
                    await replayable.PrepareReplayAsync(stoppingToken);
            }

            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed < _options.BatchSize)
                    await Task.Delay(_options.PollInterval, stoppingToken);
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
            // EINE Transaktion für Handler-Write + Checkpoint-Update
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await _handler.HandleAsync(change, conn, tx, ct);
                await SaveCheckpointAsync(conn, tx, change.SequenceId, ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                var movedToDeadLetter = await RegisterFailureAsync(conn, change, ex, ct);
                if (movedToDeadLetter)
                {
                    // Dead-Letter: Checkpoint trotzdem weitersetzen (eigene Transaktion)
                    await using var skipTx = await conn.BeginTransactionAsync(ct);
                    await SaveCheckpointAsync(conn, skipTx, change.SequenceId, ct);
                    await skipTx.CommitAsync(ct);
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
            SELECT sequence_id, scope, tenant_id, entity, entity_id, event_type, version,
                   correlation_id, causation_id, actor_id, payload::text, timestamp
            FROM change_feed
            WHERE sequence_id > @lastSeen
              AND (@scope IS NULL OR scope = @scope)
              AND (
                  @scope IS NULL
                  OR
                  @scope <> 'Tenant'
                  OR
                  tenant_id = @tenantId
              )
              AND redacted = FALSE
              AND event_type = ANY(@eventTypes)
              AND xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
              AND NOT EXISTS (
                  SELECT 1
                  FROM projection_failures pf
                  WHERE pf.projection_name = @name
                    AND pf.sequence_id = change_feed.sequence_id
                    AND pf.attempts >= @maxAttempts
              )
            ORDER BY sequence_id
            LIMIT @batchSize
            """;

        cmd.Parameters.AddWithValue("lastSeen", fromSequenceId);
    cmd.Parameters.AddWithValue("scope", (object?)_scope?.Scope.ToString() ?? DBNull.Value);
    cmd.Parameters.AddWithValue("tenantId", (object?)_scope?.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", _handler.Name);
        cmd.Parameters.AddWithValue("maxAttempts", _options.MaxAttemptsPerEvent);
        cmd.Parameters.AddWithValue("eventTypes", _handler.EventTypes.ToArray());
        cmd.Parameters.AddWithValue("batchSize", _options.BatchSize);

        var records = new List<ChangeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new ChangeRecord(
                SequenceId:    reader.GetInt64(0),
                Entity:        reader.GetString(3),
                EntityId:      reader.GetString(4),
                EventType:     reader.GetString(5),
                Version:       reader.GetInt32(6),
                CorrelationId: reader.IsDBNull(7) ? null : reader.GetString(7),
                CausationId:   reader.IsDBNull(8) ? null : reader.GetString(8),
                ActorId:       reader.GetString(9),
                PayloadJson:   reader.GetString(10),
                Timestamp:     reader.GetFieldValue<DateTimeOffset>(11),
                Scope:         Enum.Parse<ScopeType>(reader.GetString(1), ignoreCase: false),
                TenantId:      reader.IsDBNull(2) ? null : reader.GetString(2)
            ));
        }

        return records;
    }

    private async Task SaveCheckpointAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long sequenceId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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
        // Exponentielles Backoff: MIN(base * 2^(attempts-1), max)
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projection_failures
                (projection_name, sequence_id, event_type, attempts, last_error, next_retry_at)
            VALUES
                (@name, @sequenceId, @eventType, 1, @error,
                 NOW() + LEAST(
                     @baseDelay * POWER(2, 0),
                     @maxDelay
                 ) * INTERVAL '1 second')
            ON CONFLICT (projection_name, sequence_id)
            DO UPDATE SET
                attempts = projection_failures.attempts + 1,
                last_error = EXCLUDED.last_error,
                next_retry_at = NOW() + LEAST(
                    @baseDelay * POWER(2, projection_failures.attempts),
                    @maxDelay
                ) * INTERVAL '1 second',
                updated_at = NOW()
            RETURNING attempts
            """;

        cmd.Parameters.AddWithValue("name", _handler.Name);
        cmd.Parameters.AddWithValue("sequenceId", change.SequenceId);
        cmd.Parameters.AddWithValue("eventType", change.EventType);
        cmd.Parameters.AddWithValue("error", ex.ToString());
        cmd.Parameters.AddWithValue("baseDelay", _options.BaseRetryDelay.TotalSeconds);
        cmd.Parameters.AddWithValue("maxDelay", _options.MaxRetryDelay.TotalSeconds);

        var attempts = (int)(await cmd.ExecuteScalarAsync(ct) ?? 1);
        return attempts >= _options.MaxAttemptsPerEvent;
    }

    private async Task ResetCheckpointAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE projection_checkpoint
            SET last_sequence_id = 0, updated_at = NOW()
            WHERE projection_name = @name
            """;
        cmd.Parameters.AddWithValue("name", _handler.Name);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

// Marker-Record für den Replay-Channel (intern, nicht Teil der öffentlichen API)
private sealed record ReplayRequest;
```

---

## Schutz vor Sequence-Gaps: `xmin`-basierter Sichtbarkeitsfilter

> ⚠️ **Dieses Kapitel beschreibt ein kritisches Problem und seine Lösung.**

### Das Problem

PostgreSQL-Sequenzen (`BIGSERIAL`) sind **nicht transaktional**. Wenn zwei Transaktionen gleichzeitig in den Change Feed schreiben, kann folgendes passieren:

```
T1: Transaktion A holt sequence_id = 42, beginnt INSERT
T2: Transaktion B holt sequence_id = 43, INSERT + COMMIT → sichtbar
T3: Worker pollt: WHERE sequence_id > 41 → findet 43, NICHT 42
T4: Worker setzt Checkpoint auf 43
T5: Transaktion A committed → sequence_id 42 ist jetzt sichtbar
T6: Worker pollt: WHERE sequence_id > 43 → 42 wird NIE gefunden
```

**Event 42 ist für diese Projection für immer verloren.**

### Die Lösung: `xmin`-Filter

PostgreSQL speichert intern für jede Row die Transaktions-ID (`xmin`), die sie erstellt hat. Mit `pg_current_snapshot()` kann geprüft werden, welche Transaktionen für alle Sessions sichtbar sind:

```sql
AND xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
```

**Wie es funktioniert:** `pg_snapshot_xmin(pg_current_snapshot())` gibt die niedrigste noch aktive Transaktions-ID zurück. Nur Rows, deren `xmin` kleiner ist (also deren Transaktion **abgeschlossen** ist), werden geladen. Uncommitted Transaktionen werden automatisch ausgeschlossen.

**Trade-off:** Minimale Latenz (nur die Dauer der längsten offenen Transaktion). PostgreSQL-spezifisch, aber da das System ohnehin PostgreSQL-only ist, kein Problem.

---

## Optionales Interface: `IReplayableProjection`

Projections, die Replay unterstützen, können dieses Interface implementieren, um vor dem Replay ihr Read Model zu leeren:

```csharp
// src/Kernel/Projections/IReplayableProjection.cs
public interface IReplayableProjection
{
    /// <summary>
    /// Wird VOR dem Replay aufgerufen. Hier kann das Read Model geleert werden.
    /// </summary>
    Task PrepareReplayAsync(CancellationToken ct = default);
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

    public async Task HandleAsync(
        ChangeRecord record,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct = default)
    {
        switch (record.EventType)
        {
            case "UserEmailUpdated":
                await HandleEmailUpdated(record, connection, transaction, ct);
                break;
        }
    }

    private async Task HandleEmailUpdated(
        ChangeRecord record,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        var email = record.Version switch
        {
            1 => JsonSerializer.Deserialize<UserEmailUpdatedV1>(record.PayloadJson)!.Email,
            2 => JsonSerializer.Deserialize<UserEmailUpdatedV2>(record.PayloadJson)!.Value,
            _ => throw new InvalidOperationException(
                $"Unknown version {record.Version} for UserEmailUpdated")
        };

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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

> **Beachte:** Der Handler verwendet die vom Worker bereitgestellte `connection` und `transaction`. Damit sind Handler-Write und Checkpoint-Update atomar.

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
        this IServiceCollection services,
        Action<ProjectionWorkerOptions>? configure = null,
        ScopeContext? scope = null)
        where THandler : class, IProjectionHandler
    {
        services.AddSingleton<THandler>();
        services.AddSingleton<IHostedService>(sp =>
        {
            var options = new ProjectionWorkerOptions();
            configure?.Invoke(options);

            return new ProjectionWorker(
                sp.GetRequiredService<THandler>(),
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<ILogger<ProjectionWorker>>(),
                options,
                scope
            );
        });

        return services;
    }
}
```

> ⚠️ **Connection-Pool-Dimensionierung:** Jeder `ProjectionWorker` hält während der Batch-Verarbeitung eine Connection offen. Bei N Projections sollte der Connection-Pool auf mindestens `N + Headroom für HTTP-Requests` dimensioniert werden. Standardmäßig erlaubt Npgsql 100 Connections pro Pool – bei vielen Projections und hoher Last muss das angepasst werden.

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

> ⚠️ **Wichtig: Race Condition bei externem Checkpoint-Reset**
>
> Ein direkter `UPDATE projection_checkpoint SET last_sequence_id = 0` während der Worker läuft, führt zu einer Race Condition: Der Worker könnte gerade Events verarbeiten und seinen Checkpoint zurückschreiben, wodurch der Reset überschrieben wird.
>
> **Lösung:** Replay wird über den Worker selbst angefordert. Der Worker prüft am Anfang jedes Loops, ob ein Replay-Signal vorliegt. Da der Worker single-threaded ist, gibt es keine Race Condition.

```csharp
// src/Kernel/Projections/ReplayService.cs
public class ReplayService
{
    private readonly IReadOnlyDictionary<string, ProjectionWorker> _workers;

    public ReplayService(IReadOnlyDictionary<string, ProjectionWorker> workers)
    {
        _workers = workers;
    }

    public async Task RequestReplayAsync(string projectionName, CancellationToken ct = default)
    {
        if (!_workers.TryGetValue(projectionName, out var worker))
            throw new InvalidOperationException($"Unknown projection: {projectionName}");

        await worker.RequestReplayAsync(ct);
    }
}
```

Replay ist regulärer Betrieb, kein Sonderfall.

## Snapshot-Strategie (optional)

In diesem Architekturmodell sind Snapshots **kein Pflichtbestandteil**. Sie werden erst relevant, wenn Rebuild oder Recovery zu lange dauern.

Warum anfangs meist nicht noetig:

- CRUD Truth ist vorhanden
- Change Feed dient primär Synchronisation und Replay
- Aggregate muessen nicht bei jedem Request aus kompletter Event-History hydriert werden

Wann Snapshots sinnvoll werden:

- Rebuild-Zeit fuer wichtige Projections ueberschreitet SLOs
- Incident-Recovery dauert zu lange
- Sehr grosse Historie (z.B. Jahre, hunderte Millionen Events)

Wichtig: Hier sind **Projection Snapshots** gemeint, nicht klassische Aggregate-Snapshots aus purem Event Sourcing.

### Minimales Snapshot-Schema

```sql
CREATE TABLE projection_snapshot (
    projection_name    TEXT        NOT NULL,
    snapshot_id        BIGSERIAL   PRIMARY KEY,
    snapshot_sequence  BIGINT      NOT NULL,
    snapshot_payload   JSONB       NOT NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_projection_snapshot_latest
    ON projection_snapshot (projection_name, snapshot_sequence DESC);
```

### Replay mit Snapshot-Startpunkt

1. Letzten Snapshot fuer Projection laden
2. Read Model aus `snapshot_payload` wiederherstellen
3. Feed nur ab `snapshot_sequence + 1` weiter verarbeiten

```csharp
// Pseudocode
var snapshot = await LoadLatestSnapshot("user_read_model");
if (snapshot is not null)
{
    await RestoreReadModelFromSnapshot(snapshot.PayloadJson);
    await SetCheckpoint("user_read_model", snapshot.SnapshotSequence);
}

await worker.Run(); // verarbeitet nur neuere Events
```

### Snapshot-Erzeugung

- entweder zeitbasiert (z.B. alle 6h)
- oder eventbasiert (z.B. alle 100k verarbeiteten Events)

Snapshotting ist rein eine Performance-Optimierung. Die fachliche Wahrheit bleibt unveraendert im Modell aus CRUD Truth + Change Feed.

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

### Exponentielles Backoff bei Retries

Statt eines fixen Retry-Intervalls verwendet der Worker exponentielles Backoff:

```
Attempt 1: 5 Sekunden
Attempt 2: 10 Sekunden
Attempt 3: 20 Sekunden
Attempt 4: 40 Sekunden
Attempt 5: 80 Sekunden
...
Attempt N: MIN(5 * 2^(N-1), 300) Sekunden (Cap bei 5 Minuten)
```

Das verhindert, dass transiente Fehler (z.B. kurzzeitig nicht erreichbare Datenbank) den Worker in eine Endlosschleife mit hoher Last treiben.

### Failure-Query-Semantik

Die `NOT EXISTS`-Subquery in `LoadChangesAsync` filtert Events, die bereits die maximale Anzahl an Versuchen erreicht haben (`attempts >= @maxAttempts`). Diese Events werden übersprungen und der Checkpoint wird weitergesetzt (Dead Letter). Events mit weniger Versuchen werden erneut geladen, sobald `next_retry_at` erreicht ist.

> **Hinweis zur Performance:** Bei sehr großem Change Feed und vielen Failures kann die korrelierte Subquery langsam werden. In diesem Fall kann die Failure-Prüfung in den Application-Code verlagert werden (nach dem Laden der Events), oder die Failures können in einem In-Memory-Set gecacht werden.
