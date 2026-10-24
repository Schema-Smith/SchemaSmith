// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using System;
using System.Data;
using System.Threading;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Microsoft.Extensions.Configuration;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
public class BaseTableQuenchTests
{
    protected readonly string _connectionString;
    protected readonly string _mainDb;
    protected readonly string _productName;

    public BaseTableQuenchTests()
    {
        _productName = FixtureProduct.NameFor(GetType());
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres", config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
    }

    protected void RunTableQuenchProc(IDbCommand cmd, string json, bool indexOnly = false, bool dropTablesRemovedFromProduct = false, bool whatIf = false, string productName = "")
    {
        var prod = string.IsNullOrEmpty(productName) ? _productName : productName;
        cmd.CommandTimeout = 300;
        cmd.CommandText = indexOnly
            ? @$"
CALL ""SchemaSmith"".""IndexOnlyQuench""(p_ProductName := '{prod}', p_TableDefinitions := '{json.Replace("'", "''")}', p_DropUnknownIndexes := true);
CALL ""SchemaSmith"".""FixupIndexOwnership""(p_ProductName := '{prod}');
"
            : $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{prod}', p_TableDefinitions := '{json.Replace("'", "''")}', p_WhatIf := {(whatIf ? "true" : "false")}, p_DropTablesRemovedFromProduct := {(dropTablesRemovedFromProduct ? "true" : "false")}, p_DropUnknownIndexes := false)";
        var retry = true;
        var tries = 0;
        while (retry && tries++ < 10)
        {
            try
            {
                TransientRetry.Run(() => cmd.ExecuteNonQuery());
                retry = false;
            }
            catch (Exception e)
            {
                if (!e.Message.ContainsIgnoringCase("deadlock detected")) throw;
                Thread.Sleep(1000);
            }
        }
    }

    protected static string GetColumnDataType(IDbCommand cmd, string tableSchema, string tableName, string columnName)
    {
        // Calls the same "SchemaSmith"."ColumnTypeArguments" the product extraction/drift-comparison
        // procs use, rather than re-implementing the type-argument CASE a third time here — a fourth
        // hand-maintained copy is exactly the pattern that let timestamptz(3)/time(3) go unnoticed.
        cmd.CommandText = @$"
SELECT CASE WHEN c.domain_name IS NOT NULL
                  THEN CASE WHEN c.domain_schema != 'pg_catalog' THEN '""' || c.domain_schema || '"".' ELSE '' END || '""' || c.domain_name || '""'
                  ELSE CASE WHEN c.udt_schema != 'pg_catalog' THEN c.udt_schema || '.' ELSE '' END || c.udt_name
                  END ||
       ""SchemaSmith"".""ColumnTypeArguments""(c.domain_name, c.udt_name, c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.datetime_precision)
  FROM information_schema.columns c
  WHERE table_schema = '{tableSchema}'
    AND table_name = '{tableName}'
    AND column_name = '{columnName}'";
        return cmd.ExecuteScalar()?.ToString()?.ToUpper() ?? "UNKNOWN";
    }

    /// <summary>Detected PostgreSQL major (e.g. 12, 16) of the configured target — for gating tests of
    /// version-specific features (a test that exercises a PG14 feature Assert.Ignore's below 14).</summary>
    protected int PgServerMajor()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
