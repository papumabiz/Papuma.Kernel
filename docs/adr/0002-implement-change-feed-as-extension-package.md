# ADR 0002: Implementierung des Change-Feed als Extension-Package

## Status
Vorgeschlagen

## Kontext
Der `Papuma.Kernel` ist als kleine, leichte .NET 10-Bibliothek konzipiert. Das vorgeschlagene Change-Feed- und Projection-System führt signifikante Infrastruktur-Komplexität ein (Background-Worker, Checkpointing, Idempotenz-Garantien, Partitionierung). Nicht jedes Projekt, das den Kernel verwendet, benötigt diese Funktionen.

## Entscheidung
Wir entwickeln den Change Kernel und die Projection Engine als separates Extension-Package (z. B. `Papuma.Kernel.ChangeFeed`). 
Der Hauptkernel bleibt frei von dieser Logik. Gemeinsame, minimale Basis-Typen (wie `ChangeEvent` oder `IOutboxStore`) können im Hauptkernel liegen oder vom Extension-Package eigenständig definiert werden, um Abhängigkeiten minimal zu halten.

## Konsequenzen
- **Positiv:** Der Core-Kernel bleibt leichtgewichtig, stabil und frei von unnötigen Abhängigkeiten. Projekte ohne Projektionsbedarf werden nicht belastet.
- **Negativ:** Erhöhter Wartungsaufwand für ein weiteres Projekt/Package in der Solution.
- **Neutral:** Erfordert klare Definition der öffentlichen Schnittstellen zwischen Core und Extension.
