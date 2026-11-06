// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
[Category("Integration")]
[NonParallelizable]
public class DriftTests : DriftSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MySQL";
    protected override string ProductName => "DriftProduct";
    protected override bool KindlesInATransaction => false;

    protected override string TableJson(string table, params string[] columns) => SchemaQuench.IntegrationTests.MySQL.DestructiveChangeMySqlFamily.TableJson(table, columns);
    protected override string TableFileName(string table) => $"{table}.json";
    // The change audit names a MySQL-family column table.column; the database is the target's.
    protected override string QualifiedColumn(string table, string column) => $"{table}.{column}";
    protected override string DropTable(string table) => $"DROP TABLE IF EXISTS `{MainDb}`.{table}";
    protected override string DropColumn(string table, string column) => $"ALTER TABLE `{MainDb}`.{table} DROP COLUMN {column}";
    protected override string ViewScript(string view, int value) => $"CREATE OR REPLACE VIEW `{MainDb}`.{view} AS SELECT {value} AS x";
    protected override string DropView(string view) => $"DROP VIEW IF EXISTS `{MainDb}`.{view}";
    protected override string StampTable => $"`{MainDb}`.SchemaSmith_KindleStamp";
}
