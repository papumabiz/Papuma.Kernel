# ADR 0001: Verwendung eines expliziten Outbox Change-Feed Musters

## Status
Vorgeschlagen

## Kontext
Das System benötigt die Fähigkeit, Änderungen an Domänenobjekten als Events für Read-Model-Projektionen bereitzustellen. Herkömmliche Ansätze wie Datenbank-Trigger, ORM-Change-Tracking-Magie oder SQL-Parsing führen zu versteckter Komplexität, schlechter Testbarkeit und Bindung an spezifische Technologien. Der Kernel soll SQL-first (Dapper/EF-agnostisch) bleiben.

## Entscheidung
Wir implementieren ein explizites, regelbasiertes Change-Feed-DSL-Muster (`ChangeKernel`, `ChangeRule<T>`). 
- Änderungen werden nicht automatisch erraten, sondern durch deklarative Contracts definiert.
- Ein `ChangeEmitter` wird nach CRUD-Operationen explizit aufgerufen (oder über einen Repository-Hook gekapselt).
- Die resultierenden `ChangeEvent`-Records werden in einer neutralen `Outbox`-Tabelle in der Datenbank gespeichert.

## Konsequenzen
- **Positiv:** Volle Kontrolle über Event-Payloads und Granularität. Keine versteckte Magie. Change Contracts sind isoliert unit-testbar.
- **Negativ:** Erfordert Disziplin der Entwickler, den Emitter nach Mutationen aufzurufen (oder die Infrastruktur sauber zu kapseln).
- **Neutral:** Führt eine neue `Outbox`-Tabelle und entsprechende DTOs (`ChangeEvent`) ein.
