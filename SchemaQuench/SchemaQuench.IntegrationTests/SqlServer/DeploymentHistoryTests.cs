// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[NonParallelizable]
public class DeploymentHistoryTests : DeploymentHistorySharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "HistoryProduct";
    protected override string ReferenceQueriesMarker => "-- SQL Server";

    protected override string ViewScript(string view, int value) => $"CREATE OR ALTER VIEW dbo.{view} AS SELECT {value} AS x";
    protected override string DropView(string view) => $"DROP VIEW IF EXISTS dbo.{view}";

    // A JSON index arrived in SQL Server 2025.
    protected override (string File, string Json, string Drop)? DowngradedTable(string table) =>
        Convert.ToInt32(Scalar("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT)")) >= 17 ? null : ($"dbo.{table}.json", $$"""
            { "Schema": "[dbo]", "Name": "[{{table}}]",
              "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Doc]", "DataType": "NVARCHAR(MAX)", "Nullable": true } ],
              "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ],
              "JsonIndexes": [ { "Name": "[JX_{{table}}]", "Column": "[Doc]", "Paths": [ "$.a" ] } ] }
            """, $"DROP TABLE IF EXISTS dbo.{table}");
}
