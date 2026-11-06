# Course 4, Recipe 11 — Staged rollouts: an expensive change outside the deployment (lab)

**Goal:** roll an expensive index change across a fleet of tenant databases **one maintenance window at a time**,
while every deploy in the meantime leaves each database in the shape it already has. You'll declare the old and new
shapes of one index as two conditional variants, gated on what is deployed. One tenant gets converted by hand and a
new tenant arrives already converted. `--WhatIf` then finds the straggler, and you finish the rollout with a single
definition.

Each engine rolls out a change that costs real time on a large table:

| Engine | Table | The expensive change |
| --- | --- | --- |
| SQL Server | `dbo.Sale` | rebuild `IX_Sale_Report` with `PAGE` compression |
| PostgreSQL | `public.document` | move the `jsonb` GIN index `ix_document_attrs` to the `jsonb_path_ops` operator class |
| MySQL, MariaDB | `Document` | widen the `FULLTEXT` index `ft_document` from `Title` to `Title, Body` |

One deploy reaches every tenant: the template's `DatabaseIdentificationScript` returns every database whose name
starts with `cookbook_r11_t`.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (all four engines `PASS`).
- The CLI is on your PATH (`schemaquench --version` reports `SchemaQuench - Version: 2.8.0.0` or later).
- Running the lab a second time? Restore the starting package first (`git checkout -- .` in this folder): Steps 3
  and 7 edit it.

## Step 1: Create two tenants

Start from two empty tenant databases. These lines drop any left over from an earlier run, including the third
tenant you'll add in Step 5:

```bash
# from course4-recipe-11
../lab-sql.sh sqlserver master "IF DB_ID('cookbook_r11_t1') IS NOT NULL DROP DATABASE cookbook_r11_t1; IF DB_ID('cookbook_r11_t2') IS NOT NULL DROP DATABASE cookbook_r11_t2; IF DB_ID('cookbook_r11_t3') IS NOT NULL DROP DATABASE cookbook_r11_t3"
../lab-sql.sh sqlserver master "CREATE DATABASE cookbook_r11_t1; CREATE DATABASE cookbook_r11_t2"
../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS cookbook_r11_t1"
../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS cookbook_r11_t2"
../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS cookbook_r11_t3"
../lab-sql.sh postgres postgres "CREATE DATABASE cookbook_r11_t1"
../lab-sql.sh postgres postgres "CREATE DATABASE cookbook_r11_t2"
../lab-sql.sh mysql information_schema "DROP DATABASE IF EXISTS cookbook_r11_t1; DROP DATABASE IF EXISTS cookbook_r11_t2; DROP DATABASE IF EXISTS cookbook_r11_t3"
../lab-sql.sh mysql information_schema "CREATE DATABASE cookbook_r11_t1; CREATE DATABASE cookbook_r11_t2"
../lab-sql.sh mariadb information_schema "DROP DATABASE IF EXISTS cookbook_r11_t1; DROP DATABASE IF EXISTS cookbook_r11_t2; DROP DATABASE IF EXISTS cookbook_r11_t3"
../lab-sql.sh mariadb information_schema "CREATE DATABASE cookbook_r11_t1; CREATE DATABASE cookbook_r11_t2"
```

## Step 2: Deploy today's shape

The package ships the index as it is in production today: no compression, the default operator class, `Title` only.

```bash
# from course4-recipe-11/<engine>
schemaquench --ConfigFile:rollout.settings.json
```

Both tenants get the table and the index. On SQL Server:

```
[localhost,11433].[cookbook_r11_t1]         Adding new table [dbo].[Sale]
[localhost,11433].[cookbook_r11_t1]         Creating constraint [dbo].[Sale].[PK_Sale]
[localhost,11433].[cookbook_r11_t1]         Creating index [dbo].[Sale].[IX_Sale_Report]
[localhost,11433].[cookbook_r11_t1] Successfully Quenched
```

Check the shape on each tenant. You'll run these same lines after every step:

```bash
# from course4-recipe-11
../lab-sql.sh sqlserver cookbook_r11_t1 "SELECT p.data_compression_desc FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.object_id = OBJECT_ID('dbo.Sale') AND i.name = 'IX_Sale_Report'"
# → NONE
../lab-sql.sh sqlserver cookbook_r11_t2 "SELECT p.data_compression_desc FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.object_id = OBJECT_ID('dbo.Sale') AND i.name = 'IX_Sale_Report'"
# → NONE
../lab-sql.sh postgres cookbook_r11_t1 "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_document_attrs'"
# → CREATE INDEX ix_document_attrs ON public.document USING gin (attrs)
../lab-sql.sh postgres cookbook_r11_t2 "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_document_attrs'"
# → CREATE INDEX ix_document_attrs ON public.document USING gin (attrs)
../lab-sql.sh mysql cookbook_r11_t1 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title
../lab-sql.sh mysql cookbook_r11_t2 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title
../lab-sql.sh mariadb cookbook_r11_t1 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title
../lab-sql.sh mariadb cookbook_r11_t2 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title
```

## Step 3: Declare both shapes

The new shape is decided, but converting every tenant inside one deployment window is not an option. So declare
**both** shapes under the same index name. Each gets a `VariantName` and a `ShouldApplyExpression`:

- **Before conversion** applies only where the old shape is **already deployed**.
- **Converted** applies everywhere else, including a database with no index yet.

Write the gates that way round. The old shape's gate asks a question only an unconverted database answers yes to,
so a brand-new database falls through to the new shape and never needs converting. The two gates are each other's
negation, so exactly one variant applies to any database, whatever state it is in.

Replace the index on your engine's table file with the two variants:

#### SQL Server

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" sqlserver/Package/Templates/Main/Tables/dbo.Sale.json set Indexes '[{"Name": "PK_Sale", "PrimaryKey": true, "Clustered": true, "IndexColumns": "SaleId"}, {"Name": "IX_Sale_Report", "VariantName": "Before conversion", "IndexColumns": "SoldAt", "IncludeColumns": "Amount", "ShouldApplyExpression": "EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.object_id = OBJECT_ID('"'"'dbo.Sale'"'"') AND i.name = '"'"'IX_Sale_Report'"'"' AND p.data_compression_desc <> '"'"'PAGE'"'"')"}, {"Name": "IX_Sale_Report", "VariantName": "Converted", "IndexColumns": "SoldAt", "IncludeColumns": "Amount", "CompressionType": "PAGE", "ShouldApplyExpression": "NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.object_id = OBJECT_ID('"'"'dbo.Sale'"'"') AND i.name = '"'"'IX_Sale_Report'"'"' AND p.data_compression_desc <> '"'"'PAGE'"'"')"}]' -->
```json
"Indexes": [
  {
    "Name": "PK_Sale",
    "PrimaryKey": true,
    "Clustered": true,
    "IndexColumns": "SaleId"
  },
  {
    "Name": "IX_Sale_Report",
    "VariantName": "Before conversion",
    "IndexColumns": "SoldAt",
    "IncludeColumns": "Amount",
    "ShouldApplyExpression": "EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.object_id = OBJECT_ID('dbo.Sale') AND i.name = 'IX_Sale_Report' AND p.data_compression_desc <> 'PAGE')"
  },
  {
    "Name": "IX_Sale_Report",
    "VariantName": "Converted",
    "IndexColumns": "SoldAt",
    "IncludeColumns": "Amount",
    "CompressionType": "PAGE",
    "ShouldApplyExpression": "NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.object_id = OBJECT_ID('dbo.Sale') AND i.name = 'IX_Sale_Report' AND p.data_compression_desc <> 'PAGE')"
  }
]
```

#### PostgreSQL

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" postgres/Package/Templates/Main/Tables/public.document.json set Indexes '[{"Name": "pk_document", "PrimaryKey": true, "IndexColumns": "documentid"}, {"Name": "ix_document_attrs", "VariantName": "Before conversion", "AccessMethod": "gin", "IndexColumns": "attrs", "ShouldApplyExpression": "EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = '"'"'public'"'"' AND indexname = '"'"'ix_document_attrs'"'"' AND indexdef NOT LIKE '"'"'%jsonb_path_ops%'"'"')"}, {"Name": "ix_document_attrs", "VariantName": "Converted", "AccessMethod": "gin", "IndexColumns": "attrs jsonb_path_ops", "ShouldApplyExpression": "NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = '"'"'public'"'"' AND indexname = '"'"'ix_document_attrs'"'"' AND indexdef NOT LIKE '"'"'%jsonb_path_ops%'"'"')"}]' -->
```json
"Indexes": [
  {
    "Name": "pk_document",
    "PrimaryKey": true,
    "IndexColumns": "documentid"
  },
  {
    "Name": "ix_document_attrs",
    "VariantName": "Before conversion",
    "AccessMethod": "gin",
    "IndexColumns": "attrs",
    "ShouldApplyExpression": "EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ix_document_attrs' AND indexdef NOT LIKE '%jsonb_path_ops%')"
  },
  {
    "Name": "ix_document_attrs",
    "VariantName": "Converted",
    "AccessMethod": "gin",
    "IndexColumns": "attrs jsonb_path_ops",
    "ShouldApplyExpression": "NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ix_document_attrs' AND indexdef NOT LIKE '%jsonb_path_ops%')"
  }
]
```

#### MySQL

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" mysql/Package/Templates/Main/Tables/Document.json set FullTextIndexes '[{"Name": "ft_document", "VariantName": "Before conversion", "Columns": "`Title`", "ShouldApplyExpression": "EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '"'"'Document'"'"' AND index_name = '"'"'ft_document'"'"') AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '"'"'Document'"'"' AND index_name = '"'"'ft_document'"'"' AND column_name = '"'"'Body'"'"')"}, {"Name": "ft_document", "VariantName": "Converted", "Columns": "`Title`,`Body`", "ShouldApplyExpression": "NOT (EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '"'"'Document'"'"' AND index_name = '"'"'ft_document'"'"') AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '"'"'Document'"'"' AND index_name = '"'"'ft_document'"'"' AND column_name = '"'"'Body'"'"'))"}]' -->
```json
"FullTextIndexes": [
  {
    "Name": "ft_document",
    "VariantName": "Before conversion",
    "Columns": "`Title`",
    "ShouldApplyExpression": "EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document') AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document' AND column_name = 'Body')"
  },
  {
    "Name": "ft_document",
    "VariantName": "Converted",
    "Columns": "`Title`,`Body`",
    "ShouldApplyExpression": "NOT (EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document') AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document' AND column_name = 'Body'))"
  }
]
```

#### MariaDB

<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" mariadb/Package/Templates/Main/Tables/Document.json set FullTextIndexes '[{"Name": "ft_document", "VariantName": "Before conversion", "Columns": "`Title`", "ShouldApplyExpression": "EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '"'"'Document'"'"' AND index_name = '"'"'ft_document'"'"') AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '"'"'Document'"'"' AND index_name = '"'"'ft_document'"'"' AND column_name = '"'"'Body'"'"')"}, {"Name": "ft_document", "VariantName": "Converted", "Columns": "`Title`,`Body`", "ShouldApplyExpression": "NOT (EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '"'"'Document'"'"' AND index_name = '"'"'ft_document'"'"') AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '"'"'Document'"'"' AND index_name = '"'"'ft_document'"'"' AND column_name = '"'"'Body'"'"'))"}]' -->
```json
"FullTextIndexes": [
  {
    "Name": "ft_document",
    "VariantName": "Before conversion",
    "Columns": "`Title`",
    "ShouldApplyExpression": "EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document') AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document' AND column_name = 'Body')"
  },
  {
    "Name": "ft_document",
    "VariantName": "Converted",
    "Columns": "`Title`,`Body`",
    "ShouldApplyExpression": "NOT (EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document') AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document' AND column_name = 'Body'))"
  }
]
```

<!-- TRAINING-RELEASE-PIN #609: the note below describes a --Validate false error on the PostgreSQL two-variant package. When a release includes the fix: run --Validate on the Step 3 package and drop the note. -->
> **Note:** On PostgreSQL, `--Validate` reports `SS-IDX-001` for `attrs jsonb_path_ops`, reading the operator class as
> part of a column name ([#609](https://github.com/Schema-Smith/SchemaSmith/issues/609)); the deploy reads it correctly.

## Step 4: Redeploy — nothing changes

```bash
# from course4-recipe-11/<engine>
schemaquench --ConfigFile:rollout.settings.json
```

Each tenant still has the old shape, so each one matches **Before conversion**, the declaration that describes
exactly what is deployed. The log has no index work at all, only:

```
[localhost,11433].[cookbook_r11_t1] Successfully Quenched
[localhost,11433].[cookbook_r11_t2] Successfully Quenched
```

This is the package you ship for the whole rollout. Every release in the meantime deploys the rest of its changes
and leaves this index alone.

## Step 5: A DBA converts one tenant, and a new tenant arrives

In a maintenance window of their own, a DBA converts `cookbook_r11_t1` by hand. No deploy is involved:

```bash
# from course4-recipe-11
../lab-sql.sh sqlserver cookbook_r11_t1 "ALTER INDEX IX_Sale_Report ON dbo.Sale REBUILD WITH (DATA_COMPRESSION = PAGE)"
../lab-sql.sh postgres cookbook_r11_t1 "DROP INDEX ix_document_attrs; CREATE INDEX ix_document_attrs ON public.document USING gin (attrs jsonb_path_ops)"
../lab-sql.sh mysql cookbook_r11_t1 "ALTER TABLE Document DROP INDEX ft_document, ADD FULLTEXT INDEX ft_document (Title, Body)"
../lab-sql.sh mariadb cookbook_r11_t1 "ALTER TABLE Document DROP INDEX ft_document, ADD FULLTEXT INDEX ft_document (Title, Body)"
```

Meanwhile a new customer signs up, and `cookbook_r11_t3` is created:

```bash
# from course4-recipe-11
../lab-sql.sh sqlserver master "CREATE DATABASE cookbook_r11_t3"
../lab-sql.sh postgres postgres "CREATE DATABASE cookbook_r11_t3"
../lab-sql.sh mysql information_schema "CREATE DATABASE cookbook_r11_t3"
../lab-sql.sh mariadb information_schema "CREATE DATABASE cookbook_r11_t3"
```

## Step 6: Redeploy a mixed fleet

```bash
# from course4-recipe-11/<engine>
schemaquench --ConfigFile:rollout.settings.json
```

The fleet now holds both shapes, and the deploy respects each one. `t1` matches **Converted**, so its conversion
stands. `t2` still matches **Before conversion**, so it is not converted behind anyone's back. The only index work in
the log is the new tenant's, and it starts on the new shape:

```
[localhost,11433].[cookbook_r11_t3]         Adding new table [dbo].[Sale]
[localhost,11433].[cookbook_r11_t3]         Creating constraint [dbo].[Sale].[PK_Sale]
[localhost,11433].[cookbook_r11_t3]         Creating index [dbo].[Sale].[IX_Sale_Report] (variant: Converted)
```

PostgreSQL logs `Add missing Index public.document.ix_document_attrs (variant: Converted)`, and MySQL and MariaDB
log ``Create fulltext index: `Document`.`ft_document` (variant: Converted)``. Check all three tenants:

```bash
# from course4-recipe-11
../lab-sql.sh sqlserver cookbook_r11_t1 "SELECT p.data_compression_desc FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.object_id = OBJECT_ID('dbo.Sale') AND i.name = 'IX_Sale_Report'"
# → PAGE
../lab-sql.sh sqlserver cookbook_r11_t2 "SELECT p.data_compression_desc FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.object_id = OBJECT_ID('dbo.Sale') AND i.name = 'IX_Sale_Report'"
# → NONE
../lab-sql.sh sqlserver cookbook_r11_t3 "SELECT p.data_compression_desc FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.object_id = OBJECT_ID('dbo.Sale') AND i.name = 'IX_Sale_Report'"
# → PAGE
../lab-sql.sh postgres cookbook_r11_t1 "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_document_attrs'"
# → CREATE INDEX ix_document_attrs ON public.document USING gin (attrs jsonb_path_ops)
../lab-sql.sh postgres cookbook_r11_t2 "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_document_attrs'"
# → CREATE INDEX ix_document_attrs ON public.document USING gin (attrs)
../lab-sql.sh postgres cookbook_r11_t3 "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_document_attrs'"
# → CREATE INDEX ix_document_attrs ON public.document USING gin (attrs jsonb_path_ops)
../lab-sql.sh mysql cookbook_r11_t1 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title,Body
../lab-sql.sh mysql cookbook_r11_t2 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title
../lab-sql.sh mysql cookbook_r11_t3 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title,Body
../lab-sql.sh mariadb cookbook_r11_t1 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title,Body
../lab-sql.sh mariadb cookbook_r11_t2 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title
../lab-sql.sh mariadb cookbook_r11_t3 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title,Body
```

Deploy once more if you like: all three tenants report `Successfully Quenched` with no index work.

## Step 7: Write the finished definition

When the rollout is over, the package should hold one definition again. Replace the two variants with the single
new shape, **with no gate**. A gate on the last definition would be wrong now: on any database still on the old
shape, it would evaluate false, the index would be declared nowhere, and the deploy would drop it.

#### SQL Server

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" sqlserver/Package/Templates/Main/Tables/dbo.Sale.json remove-where Indexes 'VariantName=Before conversion' && python -I "$WALK_TOOLS/jedit.py" sqlserver/Package/Templates/Main/Tables/dbo.Sale.json del 'Indexes[Name=IX_Sale_Report].ShouldApplyExpression' && python -I "$WALK_TOOLS/jedit.py" sqlserver/Package/Templates/Main/Tables/dbo.Sale.json del 'Indexes[Name=IX_Sale_Report].VariantName' -->
```json
{
  "Name": "IX_Sale_Report",
  "IndexColumns": "SoldAt",
  "IncludeColumns": "Amount",
  "CompressionType": "PAGE"
}
```

#### PostgreSQL

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" postgres/Package/Templates/Main/Tables/public.document.json remove-where Indexes 'VariantName=Before conversion' && python -I "$WALK_TOOLS/jedit.py" postgres/Package/Templates/Main/Tables/public.document.json del 'Indexes[Name=ix_document_attrs].ShouldApplyExpression' && python -I "$WALK_TOOLS/jedit.py" postgres/Package/Templates/Main/Tables/public.document.json del 'Indexes[Name=ix_document_attrs].VariantName' -->
```json
{
  "Name": "ix_document_attrs",
  "AccessMethod": "gin",
  "IndexColumns": "attrs jsonb_path_ops"
}
```

#### MySQL

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" mysql/Package/Templates/Main/Tables/Document.json remove-where FullTextIndexes 'VariantName=Before conversion' && python -I "$WALK_TOOLS/jedit.py" mysql/Package/Templates/Main/Tables/Document.json del 'FullTextIndexes[Name=ft_document].ShouldApplyExpression' && python -I "$WALK_TOOLS/jedit.py" mysql/Package/Templates/Main/Tables/Document.json del 'FullTextIndexes[Name=ft_document].VariantName' -->
```json
{
  "Name": "ft_document",
  "Columns": "`Title`,`Body`"
}
```

#### MariaDB

<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" mariadb/Package/Templates/Main/Tables/Document.json remove-where FullTextIndexes 'VariantName=Before conversion' && python -I "$WALK_TOOLS/jedit.py" mariadb/Package/Templates/Main/Tables/Document.json del 'FullTextIndexes[Name=ft_document].ShouldApplyExpression' && python -I "$WALK_TOOLS/jedit.py" mariadb/Package/Templates/Main/Tables/Document.json del 'FullTextIndexes[Name=ft_document].VariantName' -->
```json
{
  "Name": "ft_document",
  "Columns": "`Title`,`Body`"
}
```

## Step 8: Preview — who still has the old shape?

Before you deploy the finished package, ask which databases it would convert. A straggler would be converted *inside* the
deployment window, the very thing the rollout exists to avoid:

```bash
# from course4-recipe-11/<engine>
schemaquench --ConfigFile:rollout.settings.json --WhatIf
```

Only `t2` has work to show. On SQL Server, the preview lists the rebuild it would run:

```
[localhost,11433].[cookbook_r11_t2]       RAISERROR('  Altering index compression for [dbo].[Sale].[IX_Sale_Report] TO PAGE', 10, 100) WITH NOWAIT;
[localhost,11433].[cookbook_r11_t2]       ALTER INDEX [IX_Sale_Report] ON [dbo].[Sale] REBUILD PARTITION=ALL WITH (DATA_COMPRESSION=PAGE);
```

PostgreSQL shows `Dropping Index public.document.ix_document_attrs` followed by `Add missing Index`. MySQL and MariaDB
show ``Drop modified fulltext index: DROP INDEX `ft_document` ON `cookbook_r11_t2`.`Document` ``. In a real fleet,
this list is your remaining work. Hand it to the DBAs and keep shipping the two-variant package until the list comes
back empty.

> **Note:** On SQL Server, the 2.8.0 preview also lists a `Dropping index [dbo].[Sale].[IX_Sale_Report]` line after the
> rebuild. The real run does not drop the index; it rebuilds it in place, as Step 9 shows.

## Step 9: Deploy the finished package

This lab's fleet is small, so let the deploy convert `t2` itself:

```bash
# from course4-recipe-11/<engine>
schemaquench --ConfigFile:rollout.settings.json
```

```
[localhost,11433].[cookbook_r11_t2]         Altering index compression for [dbo].[Sale].[IX_Sale_Report] TO PAGE
[localhost,11433].[cookbook_r11_t2] Successfully Quenched
```

`t1` and `t3` already match the single definition, so they log nothing. Check that every tenant has the new shape:

```bash
# from course4-recipe-11
../lab-sql.sh sqlserver cookbook_r11_t2 "SELECT p.data_compression_desc FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.object_id = OBJECT_ID('dbo.Sale') AND i.name = 'IX_Sale_Report'"
# → PAGE
../lab-sql.sh postgres cookbook_r11_t2 "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_document_attrs'"
# → CREATE INDEX ix_document_attrs ON public.document USING gin (attrs jsonb_path_ops)
../lab-sql.sh mysql cookbook_r11_t2 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title,Body
../lab-sql.sh mariadb cookbook_r11_t2 "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'Document' AND index_name = 'ft_document'"
# → Title,Body
```

A last deploy is clean on every tenant:

```bash
# from course4-recipe-11/<engine>
schemaquench --ConfigFile:rollout.settings.json
```

## When the deploy can do the conversion

Gating on the live shape keeps scheduling out of the package entirely: DBAs convert when they choose, and the deploy
follows. If a deployment window *is* a safe slot for some databases, gate on a control table instead. A DBA sets a row
to schedule a database, and the deploy performs the conversion. Keep the new shape's gate true once a database has
converted, for example "the row is set **or** the new shape is already deployed", or clearing the row later would
convert it back.

## Clean up

```bash
# from course4-recipe-11
../lab-sql.sh sqlserver master "DROP DATABASE cookbook_r11_t1; DROP DATABASE cookbook_r11_t2; DROP DATABASE cookbook_r11_t3"
../lab-sql.sh postgres postgres "DROP DATABASE cookbook_r11_t1"
../lab-sql.sh postgres postgres "DROP DATABASE cookbook_r11_t2"
../lab-sql.sh postgres postgres "DROP DATABASE cookbook_r11_t3"
../lab-sql.sh mysql information_schema "DROP DATABASE cookbook_r11_t1; DROP DATABASE cookbook_r11_t2; DROP DATABASE cookbook_r11_t3"
../lab-sql.sh mariadb information_schema "DROP DATABASE cookbook_r11_t1; DROP DATABASE cookbook_r11_t2; DROP DATABASE cookbook_r11_t3"
```

## The principle

A deploy declares the shape a database should have. An expensive change makes "should have" a matter of timing:
the new shape is right, but not yet, and not everywhere at once. Conditional variants let the package say exactly
that. The old shape stays declared wherever it is still deployed, the new shape is declared everywhere else, and the
database's own state chooses between them. The conversion runs on the DBAs' schedule, every deploy in the meantime
does no work on that index, new databases start converted, and `--WhatIf` names whatever is left before you close the
rollout out.
