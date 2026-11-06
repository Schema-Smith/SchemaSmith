# Course 12, Module 10 — Indexes beyond the B-tree (lab)

Goal: declare the indexes that aren't a list of columns in key order, and see a deploy keep them converged like any
other index. On SQL Server: a **spatial** index on a `geometry` column, a **selective XML** index with a secondary on
one of its paths, **XML compression**, and an **ordered clustered columnstore** index. On PostgreSQL, the same idea
through `AccessMethod`: a **GiST** index on a range and **GIN** indexes on `jsonb` and on a `tsvector`.

SQL Server and PostgreSQL: `sqlserver/` and `postgres/`, everything against `declared_m10`. SQL Server 2025's JSON
indexes are in [Course 10, Module 1](../course10-module-01), which deploys one to the 2025 tier.

## Before you start

- The [sandbox](../docker) is up and the Course 12 databases exist (run [`../course12-setup`](../course12-setup)
  once — it creates `declared_m10`).
- The CLI is on your PATH: `schemaquench --version` answers **2.8.0 or later**.
- SQL Server **2022 or later** for the ordered columnstore index and XML compression (the sandbox is). Below 2022
  each is left off and the run records a downgrade.

## Step 1: Read the declarations

```bash
# from course12-module-10/sqlserver
cat Package/Templates/Main/Tables/*.json
```

Each kind of index has its own array on the table, because each has its own shape:

| Table | Declares | Where |
| --- | --- | --- |
| `dbo.Place` | A spatial index on `Shape`, with a `GEOMETRY_GRID` and its bounding box | `SpatialIndexes` |
| `dbo.OrderDoc` | A selective XML index promoting two paths, a secondary on the `Total` path, and `"XmlCompression": true` on the table | `XmlIndexes`, table property |
| `dbo.SalesFact` | A clustered columnstore index ordered by `SaleDate` | `Indexes`, with `"ColumnStore": true` and `"Order": "[SaleDate]"` |

The selective XML index lists the paths your queries use instead of shredding every node of every document:

```text
"Selective": { "Paths": [
  { "Name": "Customer", "Path": "/order/customer", "Type": "xs:string", "MaxLength": 40, "SingletonCardinality": true },
  { "Name": "Total", "Path": "/order/total", "Type": "DECIMAL(10,2)", "SingletonCardinality": true } ] }
```

## Step 2: Deploy

```bash
# from course12-module-10/sqlserver
schemaquench --ConfigFile:quench.settings.json
```

```
Creating index [dbo].[SalesFact].[CCI_SalesFact]
Add Missing Xml Indexes
Creating index [dbo].[OrderDoc].[SXI_OrderDoc_Doc]
Creating index [dbo].[OrderDoc].[SXI_OrderDoc_Doc_Total]
Add Missing Spatial Indexes
Creating spatial index [dbo].[Place].[SX_Place_Shape]
```

Ask the catalog what it built:

```bash
# from course12-module-10
../lab-sql.sh sqlserver declared_m10 "SELECT name, type_desc FROM sys.indexes WHERE name IN ('SX_Place_Shape', 'SXI_OrderDoc_Doc', 'SXI_OrderDoc_Doc_Total', 'CCI_SalesFact') ORDER BY name"
# → CCI_SalesFact CLUSTERED COLUMNSTORE
../lab-sql.sh sqlserver declared_m10 "SELECT c.name, ic.column_store_order_ordinal FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = OBJECT_ID('dbo.SalesFact') AND ic.column_store_order_ordinal > 0"
# → SaleDate 1
../lab-sql.sh sqlserver declared_m10 "SELECT xml_compression_desc FROM sys.partitions WHERE object_id = OBJECT_ID('dbo.OrderDoc') AND index_id = 1"
# → ON
../lab-sql.sh sqlserver declared_m10 "SELECT name, path FROM sys.selective_xml_index_paths ORDER BY name"
# → Total /order/total
```

The columnstore is ordered on `SaleDate`, the XML is compressed, and the selective index holds exactly the two paths
you declared.

## Step 3: Deploy again

```bash
# from course12-module-10/sqlserver
schemaquench --ConfigFile:quench.settings.json
```

No `Creating` line. Each index is compared on what you declared and left alone. A spatial index compares only the
attributes you wrote down, so the grid density and cells per object the engine chose for you don't read as drift.

## Step 4: Change a selective path list

Your queries start filtering on order status. Add a third path to the selective index:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.OrderDoc.json" append "XmlIndexes[0].Selective.Paths" '{"Name": "Status", "Path": "/order/status", "Type": "xs:string", "MaxLength": 12, "SingletonCardinality": true}' -->
```json
{ "Name": "Status", "Path": "/order/status", "Type": "xs:string", "MaxLength": 12, "SingletonCardinality": true }
```

```bash
# from course12-module-10/sqlserver
schemaquench --ConfigFile:quench.settings.json
```

```
Dropping index [dbo].[OrderDoc].[SXI_OrderDoc_Doc]
Add Missing Xml Indexes
Creating index [dbo].[OrderDoc].[SXI_OrderDoc_Doc]
Creating index [dbo].[OrderDoc].[SXI_OrderDoc_Doc_Total]
```

A selective index can't be altered in place to a new path list, so it's dropped and created again, and SQL Server
drops its secondaries with it. The deploy puts `SXI_OrderDoc_Doc_Total` back in the same run.

```bash
# from course12-module-10
../lab-sql.sh sqlserver declared_m10 "SELECT name FROM sys.selective_xml_index_paths ORDER BY name"
# → Status
```

## Step 5: The PostgreSQL parallel

PostgreSQL has no separate arrays for these. Every index is in `Indexes`, and `AccessMethod` picks the structure:

```bash
# from course12-module-10/postgres
cat Package/Templates/Main/Tables/public.booking.json
```

| Index | `AccessMethod` | Key | For |
| --- | --- | --- | --- |
| `ix_booking_during` | `gist` | `during` (a `tsrange`) | "which bookings overlap this window" (`&&`) |
| `ix_booking_details` | `gin` | `details jsonb_path_ops` | "which bookings contain this JSON" (`@>`) |
| `ix_booking_notes` | `gin` | `notes_tsv`, a stored generated `tsvector` column | full-text search on `notes` (`@@`) |

An operator class goes in the key after the column, the way PostgreSQL writes it: `details jsonb_path_ops` builds a
smaller, faster GIN index that answers only containment.

```bash
# from course12-module-10/postgres
schemaquench --ConfigFile:quench.settings.json
schemaquench --ConfigFile:quench.settings.json
```

The first run creates the table and its three indexes; the second changes nothing.

```bash
# from course12-module-10
../lab-sql.sh postgres declared_m10 "SELECT indexname, indexdef FROM pg_indexes WHERE tablename = 'booking' AND indexname <> 'pk_booking' ORDER BY indexname"
# → ix_booking_details|CREATE INDEX ix_booking_details ON public.booking USING gin (details jsonb_path_ops)
```

<!-- TRAINING-RELEASE-PIN #609 #610: the two notes below describe a --Validate false error and an expression-key churn. When a release includes the fixes: re-run --Validate on postgres/Package and drop the first note; deploy an expression key twice and drop the second, or move the tsvector index onto the expression. -->
Two things to know today. `--Validate` reports `SS-IDX-001` for the `jsonb_path_ops` key, as if `details jsonb_path_ops`
were a column name ([#609](https://github.com/Schema-Smith/SchemaSmith/issues/609)); the deploy reads it correctly.
And a key that is a function call with more than one argument, such as `to_tsvector('english', notes)`, is dropped
and re-created on every deploy ([#610](https://github.com/Schema-Smith/SchemaSmith/issues/610)), which is one more
reason to index the stored `tsvector` column instead.

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL / MariaDB |
| --- | --- | --- | --- |
| Spatial | `SpatialIndexes` | `Indexes`, `AccessMethod: gist` (PostGIS) | `Indexes`, `IndexType: SPATIAL` |
| JSON | `JsonIndexes` (2025, [Course 10 Module 1](../course10-module-01)) | `Indexes`, `AccessMethod: gin` over `jsonb` | a functional key part (MySQL 8.0.13+), an index on a generated column (MariaDB) |
| XML | `XmlIndexes`: primary, secondary, selective | — | — |
| Columnstore | `Indexes`, `ColumnStore`, with `Order` from 2022 | — | — |
| Full-text | `FullTextIndex` ([Module 7](../course12-module-07)) | `Indexes`, `AccessMethod: gin` over a `tsvector` | `FullTextIndexes` ([Module 7](../course12-module-07)) |

## The principle

An index that isn't a B-tree is still an index you declare. Give it the shape its engine wants, and the deploy
compares what you wrote, rebuilds only what changed, and puts back what a rebuild took with it.
