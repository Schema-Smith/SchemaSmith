// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[NonParallelizable]
public class DestructiveChangeTests : DestructiveChangeSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "DestructiveChangeProduct";

    protected override string TableJson(string table, params string[] columns) => $$"""
        { "Schema": "[dbo]", "Name": "[{{table}}]",
          "Columns": [ { "Name": "[id]", "DataType": "INT IDENTITY(1, 1)", "Nullable": false }{{string.Concat(columns.Select(c => $", {{ \"Name\": \"[{c}]\", \"DataType\": \"INT\", \"Nullable\": true }}"))}} ],
          "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" } ] }
        """;
    protected override string TableFileName(string table) => $"dbo.{table}.json";
    protected override string Qualified(string table) => $"dbo.{table}";
    protected override string Insert(string table, string column, int value) => $"INSERT dbo.{table} ({column}) VALUES ({value})";
    protected override string Drop(string table) => $"DROP TABLE IF EXISTS dbo.{table}";
    protected override bool ColumnExists(string table, string column) => Count($"SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = '{column}'") == 1;
    protected override long SumOf(string table, string column) => Convert.ToInt64(Scalar($"SELECT SUM({column}) FROM dbo.{table}"));
    protected override long RowCount(string table) => Count($"SELECT COUNT(*) FROM dbo.{table}");
}
