# ADR-006: Keys und Constraints über Metamodell und Expression-Indizes

## Status

Accepted (2026-06-11)

## Kontext

Der klassische Dokument-Store-Zyklus: "JSON ist flexibel → wo sind meine Constraints? →
wir bauen relationale Strukturen nach." Ohne Plan passiert das ad-hoc und inkonsistent.
Reale Anforderungen, die nicht wegdiskutierbar sind:

- **Uniqueness** (z. B. E-Mail pro Tenant eindeutig),
- **Lookup-Queries** auf einzelne Felder (Login per E-Mail, Suche per Kundennummer),
- **Referenzen zwischen Aggregaten** (Order → Customer).

## Entscheidung

1. Keys werden **im Metamodell deklariert** (Attribut oder Fluent, vgl. ADR-007-Muster):

   ```csharp
   builder.For<User>()
       .UniqueKey(x => x.Email)
       .LookupKey(x => x.CustomerNumber);
   ```

2. Der Kernel materialisiert daraus **partielle Expression-Indizes** auf der
   Dokumenttabelle:

   ```sql
   CREATE UNIQUE INDEX ux_user_email
       ON papuma.document (tenant_id, (data ->> 'email'))
       WHERE document_type = 'User';
   ```

   Unique-Verletzungen werden als typisierte `UniqueKeyViolationException` (mit Key-Name)
   an den Aufrufer gegeben.
3. **Lookup-API** entlang der deklarierten Keys — bewusst kein LINQ-Provider:

   ```csharp
   User? user = await session.LoadByKeyAsync<User>(x => x.Email, "harry@example.com");
   ```

   Alles darüber hinaus (Ad-hoc-Queries, Reporting) gehört in Projektionen (ADR-009).
4. **Referenzen zwischen Aggregaten** sind fachliche IDs ohne Foreign-Key-Erzwingung.
   Referentielle Konsistenz ist Sache der Anwendung bzw. von Change Handlern
   (z. B. ein Handler, der auf `Customer`-Delete reagiert). Der Kernel bietet dafür
   keine Kaskaden — bewusst.
5. Index-DDL ist Teil des Schema-Managements des Kernels (idempotentes
   `EnsureSchemaAsync` beim Start bzw. explizites CLI/Setup), nicht manueller Wildwuchs.

## Konsequenzen

- Constraints existieren genau dort, wo sie deklariert sind — auffindbar, versioniert,
  konsistent benannt.
- Kein schleichender Rückbau zur relationalen Welt: Was kein Key ist, bekommt keinen
  Index auf dem Write Store; Reporting-Bedürfnisse wandern in Projektionen.
- Partielle Indizes pro Dokumenttyp halten die Indexmenge klein und treffsicher.
- Cross-Aggregate-Integrität ist schwächer als bei Foreign Keys — akzeptierter Preis;
  wo harte Konsistenz nötig ist, ist es ein Hinweis, dass die Aggregatgrenze falsch liegt.
