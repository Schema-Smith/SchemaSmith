# AdventureWorks (SQL Server)

## Source

| | |
|---|---|
| **Repository** | [microsoft/sql-server-samples](https://github.com/microsoft/sql-server-samples) |
| **File** | `AdventureWorks2022.bak` |
| **License** | MIT |
| **Self-port** | No — Microsoft official |

## Extraction Notes

- Restored fresh `.bak` to SQL Server 2022 Docker
- Extracted with SchemaSmith Community toolset (SchemaTongs + DataTongs)
- 71 tables, 20 views, 10 procedures, 11 functions, 10 triggers, 5 schemas, 6 UDTs, 6 XML schema collections, 1 full-text catalog, 8 XML indexes, 3 full-text indexes
- All 68 data tables configured with `MergeType: Insert/Update`
- `dbo.ufnLeadingZeros` re-extracted with `ScriptDynamicDependencyRemoval=true` — this function has `WITH SCHEMABINDING` and is referenced by a computed column on `Sales.Customer.AccountNumber`. **This is a deliberate trade-off, and it makes a re-deploy of this package report changes forever.** The generated preamble drops the dependents on *every* deploy — not only when the function body changed — so `Sales.Customer.AccountNumber` and its unique index `AK_Customer_AccountNumber` are genuinely dropped and recreated each run, and every deployment summary reports one column and one index created. Nothing is wrong: the objects end up exactly as declared. The alternative is a create-only-when-missing guard, which would leave a *changed* function body undeployed and need a hand-written migration script, so this demo chose the idempotent-script side. Two consequences worth knowing: `--WhatIf` does not show the rebuild (it never runs the preamble, so nothing reads as missing), and SQL Server never reuses a dropped `column_id`, so `sys.tables.max_column_id_used` on `Sales.Customer` climbs by one per deploy — 7 columns, `max_column_id_used` 13 after a handful of runs. See [ScriptDynamicDependencyRemovalForFunctions](../../../docs/end-user/reference/schematongs.md#scriptdynamicdependencyremovalforfunctions).
- Data delivery excludes `dbo.DatabaseLog` (populated by DDL trigger) and `Production.TransactionHistory` (populated by DML triggers)
- `HumanResources.Employee` and `Purchasing.Vendor` use `MergeType=Insert/Update` (no delete) because they have INSTEAD OF DELETE triggers
- Full round-trip validated: quench to clean database, structural comparison, idempotency verified — **with the one documented exception above**: the `ufnLeadingZeros` dependency preamble means a second deploy reports one column and one index created rather than zero changes. Every other object converges to no change.
