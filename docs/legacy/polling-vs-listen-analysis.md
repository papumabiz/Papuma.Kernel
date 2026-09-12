# Polling vs. LISTEN/NOTIFY — Analyse für Papuma.Kernel

> **⚠️ Historisches v1-Dokument.** Diese Analyse bezieht sich auf den v1-Code, der beim vNEXT-Reboot entfernt wurde — Code-Referenzen und relative Links lösen nicht mehr auf. Die Schlussfolgerungen bleiben gültig und werden von [ADR-010](../adr/adr-010-feed-consumption.md) und der [vNEXT-Architektur](../architecture.md) referenziert.

## Ist-Zustand

Der `ProjectionWorker` (v1: `src/Papuma.Kernel/Projections/ProjectionWorker.cs`) nutzt **reines Polling** mit folgender Logik:

```
loop:
    batch = SELECT ... FROM change_feed WHERE sequence_id > checkpoint LIMIT 100
    if batch.Count < BatchSize:
        await Task.Delay(100ms)    ← PollInterval
```

Das bedeutet:
- **Im Leerlauf:** 10 Queries/Sekunde pro Worker, auch wenn nichts passiert
- **Unter Last:** Kein Delay, sofortige Weiterverarbeitung (Batch voll → nächster Batch)
- **Pro Tenant-Worker:** Jeder Scope-spezifische Worker pollt unabhängig

---

## Wann wird Polling zum Problem?

### Szenario-Rechnung

| Situation | Worker-Anzahl | Queries/Sek im Leerlauf |
|---|---|---|
| 1 App, 1 Projection | 1 | 10 |
| 1 App, 5 Projections | 5 | 50 |
| 1 App, 5 Projections, 10 Tenants | 50 | 500 |
| 1 App, 5 Projections, 100 Tenants | 500 | 5.000 |
| 3 App-Instanzen, 5 Proj., 100 Tenants | 1.500 | 15.000 |

**Bis ~50 Worker** ist Polling kein Problem — PostgreSQL bewältigt tausende einfache Index-Lookups pro Sekunde problemlos.

**Ab ~500 Worker** wird es relevant: 5.000 leere SELECT-Queries pro Sekunde erzeugen unnötige CPU-Last auf der Datenbank, auch wenn der Index-Scan trivial ist.

**Ab ~1.500 Worker** ist es ein echtes Problem: 15.000 Queries/Sek im Leerlauf sind Verschwendung und können die Connection-Pool-Kapazität belasten.

### Unter Last ist Polling effizient

Wenn tatsächlich Events ankommen, ist Polling **optimal**: Der Worker liest Batches von 100 Events ohne Delay und verarbeitet sie sofort. Das Problem ist ausschließlich der **Leerlauf**.

---

## Die Alternative: PostgreSQL LISTEN/NOTIFY

PostgreSQL hat einen eingebauten Pub/Sub-Mechanismus:

```sql
-- Writer-Seite (nach INSERT in change_feed):
NOTIFY change_feed_updated;

-- Worker-Seite:
LISTEN change_feed_updated;
-- blockiert bis Notification kommt, dann:
SELECT ... FROM change_feed WHERE sequence_id > checkpoint
```

### Vorteile

| Aspekt | Polling | LISTEN/NOTIFY |
|---|---|---|
| Leerlauf-Last | 10 Queries/Sek pro Worker | 0 Queries bis Event kommt |
| Latenz | Bis zu PollInterval (100ms) | Nahezu sofort (~1-5ms) |
| Connection-Nutzung | Kurze Queries, häufig | Langlebige Connection, selten |

### Nachteile und Risiken

| Aspekt | Bewertung |
|---|---|
| **Langlebige Connections** | Jeder Worker hält eine dedizierte Connection offen, die auf NOTIFY wartet. Bei 500 Workern sind das 500 dauerhaft belegte Connections — das kann schlimmer sein als Polling. |
| **Keine Payload-Garantie** | NOTIFY liefert nur ein Signal, keine Daten. Der Worker muss trotzdem SELECT machen. |
| **Keine Persistenz** | Wenn der Worker offline ist, gehen Notifications verloren. Man braucht trotzdem Polling als Fallback. |
| **Connection-Verlust** | Wenn die LISTEN-Connection abbricht, verpasst der Worker Events bis zum Reconnect. |
| **Npgsql-Komplexität** | LISTEN/NOTIFY in Npgsql erfordert eine dedizierte Connection (nicht aus dem Pool) und asynchrones Warten via `NpgsqlConnection.WaitAsync()`. Das ist ein anderes Programmiermodell. |

---

## Empfohlener Ansatz: Hybrid — Polling + LISTEN/NOTIFY als Weckruf

Die beste Lösung kombiniert beide Ansätze:

```
┌─────────────────────────────────────────────────────┐
│                  Hybrid-Worker                      │
│                                                     │
│  1. LISTEN change_feed_updated                      │
│  2. Warte auf NOTIFY oder Timeout (z.B. 5 Sek)     │
│  3. SELECT ... FROM change_feed WHERE seq > chkpt   │
│  4. Verarbeite Batch                                │
│  5. Goto 2                                          │
└─────────────────────────────────────────────────────┘
```

### Wie es funktioniert

- **Im Leerlauf:** Worker wartet auf NOTIFY. Kein Polling, keine Queries.
- **Bei neuem Event:** NOTIFY weckt den Worker sofort → SELECT → Verarbeitung.
- **Fallback-Timeout:** Alle 5 Sekunden pollt der Worker trotzdem (Sicherheitsnetz für verpasste Notifications).
- **Unter Last:** Identisch zum aktuellen Verhalten — Batch voll → nächster Batch ohne Delay.

### Leerlauf-Vergleich

| Situation | Reines Polling | Hybrid |
|---|---|---|
| 50 Worker, Leerlauf | 500 Queries/Sek | 10 Queries/Sek (Fallback alle 5s) |
| 500 Worker, Leerlauf | 5.000 Queries/Sek | 100 Queries/Sek |
| 1.500 Worker, Leerlauf | 15.000 Queries/Sek | 300 Queries/Sek |

### Connection-Verbrauch

**Wichtig:** Man braucht nicht eine LISTEN-Connection pro Worker. Ein einzelner LISTEN-Kanal pro Prozess reicht:

```
1 LISTEN-Connection pro App-Instanz
    ↓ NOTIFY empfangen
    ↓ Signal an alle lokalen Worker (z.B. via Channel<T> oder SemaphoreSlim)
```

Bei 3 App-Instanzen mit je 500 Workern: **3 LISTEN-Connections** statt 1.500 Polling-Connections.

---

## Implementierungsskizze

### Writer-Seite: NOTIFY nach Commit

Der einfachste Weg ist ein Trigger auf `change_feed`:

```sql
CREATE OR REPLACE FUNCTION notify_change_feed()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('change_feed_updated', '');
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_change_feed_notify
    AFTER INSERT ON change_feed
    FOR EACH STATEMENT
    EXECUTE FUNCTION notify_change_feed();
```

`FOR EACH STATEMENT` statt `FOR EACH ROW` — bei Batch-Inserts wird nur eine Notification gesendet.

### Worker-Seite: Shared Listener

```csharp
// Konzeptskizze — kein produktionsreifer Code
public sealed class ChangeFeedNotificationListener : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Channel<bool> _wakeupChannel;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var listenCmd = conn.CreateCommand();
            listenCmd.CommandText = "LISTEN change_feed_updated";
            await listenCmd.ExecuteNonQueryAsync(ct);

            conn.Notification += (_, _) =>
                _wakeupChannel.Writer.TryWrite(true);

            while (!ct.IsCancellationRequested)
            {
                // Warte auf NOTIFY oder Timeout
                await conn.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
        }
    }
}
```

Der `ProjectionWorker` würde dann statt `Task.Delay(PollInterval)` auf den Wakeup-Channel warten:

```csharp
// Statt:
await Task.Delay(_options.PollInterval, stoppingToken);

// Hybrid:
using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
cts.CancelAfter(_options.FallbackPollInterval); // z.B. 5 Sekunden
try { await _wakeupChannel.Reader.ReadAsync(cts.Token); }
catch (OperationCanceledException) { /* Timeout → normaler Poll-Zyklus */ }
```

---

## Empfehlung für Papuma.Kernel

| Phase | Ansatz | Begründung |
|---|---|---|
| **Jetzt** | Polling beibehalten | Funktioniert, ist einfach, reicht für die meisten Deployments |
| **Wenn nötig** | `PollInterval` konfigurierbar erhöhen | Von 100ms auf 1-5 Sekunden reduziert Leerlauf-Last um 90-98% |
| **Bei Skalierung** | Hybrid mit Shared LISTEN-Connection | Eliminiert Leerlauf-Last fast vollständig, behält Polling als Fallback |

**Wichtig:** Der Wechsel von Polling zu Hybrid ist **kein Breaking Change**. Die `ProjectionWorkerOptions` können um ein `NotificationEnabled`-Flag erweitert werden. Bestehende Worker funktionieren weiter wie bisher.

### Was man NICHT tun sollte

- **Kein Kafka/RabbitMQ einführen** nur wegen Polling-Overhead. Das löst ein kleines Problem mit einer großen Infrastruktur-Abhängigkeit.
- **Kein pg_logical_replication** als Event-Quelle. Das ist ein Betriebsrisiko (Replication Slots, WAL-Retention) und löst ein anderes Problem.
- **Kein SKIP LOCKED-basiertes Queue-Pattern.** Das ändert die Semantik von Projections (jeder Event wird nur einmal verarbeitet statt von jedem Worker).

---

## Fazit

Polling ist für Papuma.Kernel **die richtige Wahl als Default**. Es ist einfach, robust und für die Zielgruppe (einzelne bis mittlere SaaS-Deployments) mehr als ausreichend.

Wenn das System auf viele Tenants mit vielen Projection-Workern skaliert, ist der Hybrid-Ansatz mit einer **shared LISTEN-Connection pro Prozess** die sauberste Erweiterung — er eliminiert das Leerlauf-Problem, ohne die Architektur zu ändern.

Die aktuelle Implementierung hat bereits die richtige Optimierung eingebaut: Wenn ein Batch voll ist (`processed == BatchSize`), wird **kein Delay** eingefügt. Das Polling-Problem betrifft also nur den Leerlauf, nicht die Durchsatzleistung unter Last.
