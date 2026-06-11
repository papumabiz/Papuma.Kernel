# ADR-010: Feed-Konsum — snapshot-basiertes Polling mit LISTEN/NOTIFY-Wakeup

## Status

Accepted (2026-06-11)

## Kontext

Der Change Feed wird über eine `seq`-Spalte (Identity) geordnet. Naives Polling mit
`WHERE seq > @lastSeq` verliert Änderungen: Transaktion A zieht `seq = 100`, committet
aber **nach** Transaktion B mit `seq = 101`. Ein Poller, der 101 bereits gesehen und
seinen Checkpoint gesetzt hat, sieht 100 nie.

Die v1-Analyse [polling-vs-listen-analysis.md](../../analyses/polling-vs-listen-analysis.md)
hat bereits ergeben: LISTEN/NOTIFY allein ist als Wahrheitsquelle ungeeignet
(Verbindungsabrisse, keine Persistenz), Polling allein ist träge oder teuer.

## Entscheidung

1. **Polling ist die Wahrheit, NOTIFY ist nur der Wecker.** Die Engine pollt den Feed;
   ein `NOTIFY papuma_changes` am Ende jeder Save-Transaktion weckt wartende Poller
   sofort auf. Verpasste Notifications kosten nur Latenz (max. Poll-Intervall), nie Daten.
2. **Lückenloses Lesen über Transaktions-Snapshots.** Jeder ChangeRecord speichert
   `txid = pg_current_xact_id()` (xid8). Der Poller liest nur Changes, deren Transaktion
   sicher abgeschlossen und für alle sichtbar ist:

   ```sql
   SELECT ...
   FROM papuma.change
   WHERE seq > @checkpoint
     AND txid < pg_snapshot_xmin(pg_current_snapshot())
   ORDER BY seq
   LIMIT @batchSize;
   ```

   Damit kann keine noch offene Transaktion mit kleinerer `seq` mehr "hinter" dem
   Checkpoint einschlagen — der Checkpoint darf gefahrlos auf die höchste gelesene
   `seq` gesetzt werden.
3. **Ein Poll-Zyklus pro Prozess**, Verteilung der Changes an Handler in-process.
   Mehrere konkurrierende Konsumenten-Prozesse koordinieren sich über
   `FOR UPDATE SKIP LOCKED` auf der Checkpoint-Tabelle (ein Leader pro Handler-Gruppe) —
   kein verteilter Konsens im Kernel.

## Konsequenzen

- Keine verlorenen Changes bei normaler MVCC-Parallelität; das Verfahren braucht keine
  künstlichen Lag-Fenster oder Heuristiken.
- Sehr lange laufende Schreib-Transaktionen halten `pg_snapshot_xmin` und damit den
  Feed-Fortschritt auf — akzeptiert und beobachtbar (Metrik "feed lag"); lange
  Transaktionen sind ohnehin ein Anti-Pattern des Write-Pfads (ADR-003: ein Save, eine
  kurze Transaktion).
- Latenz im Normalfall ≈ NOTIFY-Roundtrip (Millisekunden), im Störungsfall ≤ Poll-Intervall.
- `xid8` ist wraparound-sicher; keine Sonderbehandlung nötig.
