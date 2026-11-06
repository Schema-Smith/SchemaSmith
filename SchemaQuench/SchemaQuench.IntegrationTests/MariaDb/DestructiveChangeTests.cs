// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[NonParallelizable]
public class DestructiveChangeTests : DestructiveChangeSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MariaDB";
    protected override string ProductName => "DestructiveChangeProduct";

    protected override string TableJson(string table, params string[] columns) => SchemaQuench.IntegrationTests.MySQL.DestructiveChangeMySqlFamily.TableJson(table, columns);
    protected override string TableFileName(string table) => $"{table}.json";
    protected override string Qualified(string table) => $"{MainDb}.{table}";
    // ANALYZE so the catalog's row estimate is current.
    protected override string Insert(string table, string column, int value) => $"INSERT INTO `{MainDb}`.{table} ({column}) VALUES ({value}); ANALYZE TABLE `{MainDb}`.{table}";
    protected override string Drop(string table) => $"DROP TABLE IF EXISTS `{MainDb}`.{table}";
    protected override bool ColumnExists(string table, string column) =>
        Count($"SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{MainDb}' AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'") == 1;
    protected override long SumOf(string table, string column) => Convert.ToInt64(Scalar($"SELECT SUM({column}) FROM `{MainDb}`.{table}"));
    protected override long RowCount(string table) => Count($"SELECT COUNT(*) FROM `{MainDb}`.{table}");
}
