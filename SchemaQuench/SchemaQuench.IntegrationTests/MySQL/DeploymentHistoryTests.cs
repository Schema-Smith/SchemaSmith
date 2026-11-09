// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Text.RegularExpressions;
using System;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
[Category("Integration")]
[NonParallelizable]
public class DeploymentHistoryTests : DeploymentHistorySharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MySQL";
    protected override string ProductName => "HistoryProduct";
    protected override string ReferenceQueriesMarker => "-- MySQL and MariaDB";

    protected override string ViewScript(string view, int value) => $"CREATE OR REPLACE VIEW `{MainDb}`.{view} AS SELECT {value} AS x";
    protected override string DropView(string view) => $"DROP VIEW IF EXISTS `{MainDb}`.{view}";

    // An invisible index arrived in MySQL 8.0.
    protected override (string File, string Json, string Drop)? DowngradedTable(string table) =>
        Version.Parse(Regex.Match(Convert.ToString(Scalar("SELECT VERSION()")), @"^\d+\.\d+").Value) >= new Version(8, 0) ? null : InvisibleIndexTable(table);

    private (string File, string Json, string Drop) InvisibleIndexTable(string table) => ($"{table}.json", $$"""
        { "Name": "{{table}}",
          "Columns": [ { "Name": "a", "DataType": "INT", "Nullable": false } ],
          "Indexes": [ { "Name": "IX_{{table}}", "IndexColumns": "a", "Visible": false } ] }
        """, $"DROP TABLE IF EXISTS `{MainDb}`.{table}");
}
