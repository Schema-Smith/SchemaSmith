// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// Every SchemaSmith PostgreSQL procedure must kindle with <c>SET jit = 'off'</c>.
/// <para>WHY. The temp tables the quench procedures read are built with <c>CREATE TEMPORARY TABLE … AS</c>
/// and are therefore never ANALYZEd, so the planner costs every query over them from default row guesses.
/// Pushed through a correlated NOT EXISTS carrying JSON_ARRAY_ELEMENTS, STRING_AGG and a user function, the
/// exclude-constraint drop query in ModifiedTableQuench costed at 2,066,541 — past <c>jit_above_cost</c>
/// (100,000) and both <c>jit_inline_above_cost</c> and <c>jit_optimize_above_cost</c> (500,000). PostgreSQL
/// compiled, inlined and optimised 54 functions for ~797ms to execute a plan that returned zero rows in 30
/// microseconds, and every deploy paid it on every table.</para>
/// <para>WHY EVERY PROCEDURE AND NOT A CURATED LIST. The first attempt at this fix put the clause only on
/// procedures that no other <c>.sql</c> file CALLs, reasoning that a function-level SET covers nested calls.
/// That list was wrong: the product's own deploy path (<c>SchemaQuench/DatabaseQuench.cs</c>) CALLs
/// <c>ModifiedTableQuench</c>, <c>MissingTableAndColumnQuench</c>, <c>ForeignKeyQuench</c> and five more
/// DIRECTLY, so they are entry points too — and ModifiedTableQuench is exactly where the expensive query
/// lives. The curated list measured a 49% improvement while leaving a third of the cost in place. Asserting
/// over whatever procedures the catalog actually holds removes the judgement call, and a procedure added
/// later cannot quietly opt out.</para>
/// <para>This defect costs time and never correctness, so nothing else in the suite would notice the clause
/// being dropped by a later edit to a procedure header.</para>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class JitDisabledOnProceduresTests
{
    private IDbConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    private string ScalarStr(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    [Test]
    public void EverySchemaSmithProcedure_KindlesWithJitDisabled()
    {
        // prokind 'p' is a PROCEDURE. Scalar functions are deliberately out of scope: they are only ever
        // reached from inside a procedure that has already turned JIT off, and a per-row GUC save/restore
        // on a function called inside a subquery would be a cost of its own.
        var offenders = new List<string>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT p.proname, COALESCE(array_to_string(p.proconfig, ','), '')
                  FROM pg_proc p
                  JOIN pg_namespace n ON n.oid = p.pronamespace
                 WHERE n.nspname = 'SchemaSmith'
                   AND p.prokind = 'p'
                 ORDER BY p.proname";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var config = reader.GetString(1);
                if (!config.Contains("jit=off")) offenders.Add($"{name} [proconfig: {(config.Length == 0 ? "none" : config)}]");
            }
        }

        Assert.That(offenders, Is.Empty,
            "every SchemaSmith procedure must kindle with SET jit = 'off'. Without it the planner's cost "
            + "estimate over never-ANALYZEd temp tables sends PostgreSQL into LLVM for hundreds of milliseconds "
            + "per call to run plans that touch a handful of rows -- and the product CALLs these procedures "
            + "directly, so being callable from TableQuench is not cover. Missing: " + string.Join("; ", offenders));
    }

    [Test]
    public void TheProcedureSetIsNotEmpty()
    {
        // Guards the test above against passing vacuously if kindling changed shape and the catalog query
        // started returning nothing.
        var count = Convert.ToInt32(ScalarStr(
            "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace "
            + "WHERE n.nspname = 'SchemaSmith' AND p.prokind = 'p'"));

        Assert.That(count, Is.GreaterThan(15),
            "kindling should install roughly two dozen PostgreSQL procedures; a much smaller number means the "
            + "assertion above is checking almost nothing");
    }

    [Test]
    public void CallingAProcedure_LeavesTheCallersOwnJitSettingAlone()
    {
        // The claim the SET clause rests on: PostgreSQL saves and restores a function-level GUC around the
        // call. SchemaSmith turns JIT off for its own queries, not for the session it was invoked from --
        // a deploy that silently disabled JIT for whatever ran next would be a side effect nobody asked for.
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SET jit = on";
            cmd.ExecuteNonQuery();
        }

        Assert.That(ScalarStr("SHOW jit"), Is.EqualTo("on"), "setup: the session must start with JIT on");

        var table = $"JitProbe_{Guid.NewGuid():N}"[..20];
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandTimeout = 300;
            // WhatIf: every query runs, nothing is written. The point is the GUC, not the schema.
            cmd.CommandText = $@"
                CALL ""SchemaSmith"".""TableQuench""(
                    p_ProductName := 'JitProbe',
                    p_TableDefinitions := '{{ ""Schema"": ""public"", ""Name"": ""{table}"",
                        ""Columns"": [ {{ ""Name"": ""id"", ""DataType"": ""integer"", ""Nullable"": false }} ] }}',
                    p_WhatIf := true)";
            cmd.ExecuteNonQuery();
        }

        Assert.That(ScalarStr("SHOW jit"), Is.EqualTo("on"),
            "TableQuench must restore the caller's jit setting -- a function-level SET is scoped to the "
            + "call, and SchemaSmith has no business changing how the rest of the session plans its queries");
    }
}
