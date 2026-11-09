// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[NonParallelizable]
public class DeploymentHistoryTests : DeploymentHistorySharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "HistoryProduct";
    protected override string ReferenceQueriesMarker => "-- PostgreSQL";

    protected override string ViewScript(string view, int value) => $"CREATE OR REPLACE VIEW public.{view} AS SELECT {value} AS x";
    protected override string DropView(string view) => $"DROP VIEW IF EXISTS public.{view}";

    // A VIRTUAL generated column arrived in PostgreSQL 18.
    protected override (string File, string Json, string Drop)? DowngradedTable(string table) =>
        Convert.ToInt32(Scalar("SELECT current_setting('server_version_num')::int")) >= 180000 ? null : ($"public.{table}.json", $$"""
            { "Schema": "public", "Name": "{{table}}",
              "Columns": [ { "Name": "a", "DataType": "integer", "Nullable": false },
                           { "Name": "b", "DataType": "integer", "Generated": "ALWAYS", "GenerationExpression": "a * 2", "Virtual": true } ] }
            """, $"DROP TABLE IF EXISTS public.{table}");
}
