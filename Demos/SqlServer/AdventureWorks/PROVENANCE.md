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
- `dbo.ufnLeadingZeros` is `WITH SCHEMABINDING` and is referenced by the computed column `Sales.Customer.AccountNumber` and, through it, by the unique index `AK_Customer_AccountNumber`. Its script is a plain `CREATE OR ALTER`. When the function body changes, SchemaQuench drops the declared dependents, applies the change and puts them back in the same run (`dropped` and `created` in the deployment summary); when the script is unchanged, nothing is touched. See [Schema-Bound Dependents](../../../docs/end-user/reference/schemaquench.md#schema-bound-dependents).
- The full-text columns `HumanResources.JobCandidate.Resume` (xml) and `Production.Document.Document` (varbinary) declare `LANGUAGE 1033` explicitly, as SchemaTongs writes them: a non-character column has no collation to imply a language.
- Data delivery excludes `dbo.DatabaseLog` (populated by DDL trigger) and `Production.TransactionHistory` (populated by DML triggers)
- `HumanResources.Employee` and `Purchasing.Vendor` use `MergeType=Insert/Update` (no delete) because they have INSTEAD OF DELETE triggers
- Full round-trip validated on 2.8.0: quench to a clean database, then a second deploy of the unchanged package reports zero objects created, modified or dropped. Module scripts are skipped as unchanged; data is redelivered as a no-op merge.
