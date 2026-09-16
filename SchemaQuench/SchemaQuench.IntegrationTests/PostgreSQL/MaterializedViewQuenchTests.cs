// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Threading;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[TestFixture]
[NonParallelizable]
public class MaterializedViewQuenchTests
{
    private readonly string _productName = "MvQuenchTests";
    private readonly string _adminConnectionString;

    public MaterializedViewQuenchTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _adminConnectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres", config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
    }

    private string _mvTestDb = "";

    [OneTimeSetUp]
    public void Setup()
    {
        _mvTestDb = $"MvTest_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"CREATE DATABASE ""{_mvTestDb}"";";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_mvTestDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

        // Create the source table for our materialized view tests
        cmd.CommandText = @"
CREATE TABLE public.test_source (id INT, name VARCHAR(100), amount DECIMAL(10,2));
INSERT INTO public.test_source VALUES (1, 'Alice', 100.00), (2, 'Bob', 200.00);
";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        if (string.IsNullOrEmpty(_mvTestDb)) return;
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_mvTestDb}' AND pid <> pg_backend_pid();";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $@"DROP DATABASE IF EXISTS ""{_mvTestDb}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void InitialQuench_CreatesViewWithDataAndIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);
        RunMaterializedViewQuench(cmd, ViewDefinitionWithIndex());

        // Verify materialized view exists
        cmd.CommandText = "SELECT COUNT(*) FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Materialized view should exist");

        // Verify index exists
        cmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'mv_test' AND indexname = 'ix_mv_test_id'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Index should exist on materialized view");

        // Verify data is populated
        cmd.CommandText = "SELECT COUNT(*) FROM public.mv_test";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(2), "Materialized view should have 2 rows");

        // Verify index is unique
        cmd.CommandText = @"
SELECT idx.indisunique
  FROM pg_class c
  JOIN pg_index idx ON idx.indrelid = c.oid
  JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = 'ix_mv_test_id'
  WHERE c.relname = 'mv_test'
    AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = 'public')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "Index should be unique");

        conn.Close();
    }

    [Test]
    public void ReQuench_WithNoChanges_ViewStillExistsWithCorrectData()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Re-quench with the same definition — use the PostgreSQL-stored definition
        // to ensure the diff comparison matches and no rebuild occurs
        var storedDef = GetStoredDefinition(cmd, "mv_test");
        RunMaterializedViewQuench(cmd, BuildViewJson("mv_test", "public", storedDef, true, ViewIndexJson_IdOnly()));

        // Verify the view still exists
        cmd.CommandText = "SELECT COUNT(*) FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Materialized view should still exist");

        // Verify data still present
        cmd.CommandText = "SELECT COUNT(*) FROM public.mv_test";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(2), "Data should still be present");

        // Verify index still exists
        cmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'mv_test' AND indexname = 'ix_mv_test_id'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Index should still exist");

        conn.Close();
    }

    [Test]
    public void ReQuench_WithNoChanges_DoesNotRebuildView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Use stored definition so the procedure's diff comparison matches
        var storedDef = GetStoredDefinition(cmd, "mv_test");

        // Record OID before re-quench
        cmd.CommandText = @"
SELECT c.oid FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  WHERE c.relname = 'mv_test' AND c.relkind = 'm'";
        var oidBefore = cmd.ExecuteScalar();

        RunMaterializedViewQuench(cmd, BuildViewJson("mv_test", "public", storedDef, true, ViewIndexJson_IdOnly()));

        // Verify OID is unchanged (view was NOT dropped and recreated)
        cmd.CommandText = @"
SELECT c.oid FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  WHERE c.relname = 'mv_test' AND c.relkind = 'm'";
        var oidAfter = cmd.ExecuteScalar();
        Assert.That(oidAfter, Is.EqualTo(oidBefore), "View OID should be unchanged (no-op re-quench)");

        conn.Close();
    }

    // #242. The test above deliberately feeds the STORED definition back in so the diff matches -- which means
    // it cannot see the thing a user actually hits: PostgreSQL rewrites a matview body when it stores it
    // (schema-qualifying, re-aliasing, re-casing), so a body authored by hand never matches the catalog and the
    // view is dropped and rebuilt on every deploy. Rebuilding a materialized view re-runs its query, so this is
    // not a cosmetic churn. This authors the body the way a person writes it and asserts the view is left alone.
    [Test]
    public void ReQuench_WithANaturallyAuthoredDefinition_DoesNotRebuildView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);
        var authored = "SELECT id, name, amount FROM public.test_source";
        RunMaterializedViewQuench(cmd, BuildViewJson("mv_test", "public", authored, true, ViewIndexJson_IdOnly()));

        cmd.CommandText = @"
SELECT c.oid FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  WHERE c.relname = 'mv_test' AND c.relkind = 'm'";
        var oidBefore = cmd.ExecuteScalar();
        Assert.That(oidBefore, Is.Not.Null, "setup: the view must exist after the first deploy");

        for (var pass = 2; pass <= 3; pass++)
        {
            RunMaterializedViewQuench(cmd, BuildViewJson("mv_test", "public", authored, true, ViewIndexJson_IdOnly()));
            cmd.CommandText = @"
SELECT c.oid FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  WHERE c.relname = 'mv_test' AND c.relkind = 'm'";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(oidBefore),
                $"pass {pass}: the materialized view was rebuilt for an unchanged declaration, which re-runs its query");
        }

        conn.Close();
    }

    [Test]
    public void DefinitionChange_TriggersRebuild()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Record OID before
        cmd.CommandText = @"
SELECT c.oid FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  WHERE c.relname = 'mv_test' AND c.relkind = 'm'";
        var oidBefore = cmd.ExecuteScalar();

        // Call with modified definition (added WHERE clause)
        RunMaterializedViewQuench(cmd, ViewDefinitionWithWhereClause());

        // Verify view still exists
        cmd.CommandText = "SELECT COUNT(*) FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Materialized view should exist after rebuild");

        // Verify OID changed (view was dropped and recreated)
        cmd.CommandText = @"
SELECT c.oid FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  WHERE c.relname = 'mv_test' AND c.relkind = 'm'";
        var oidAfter = cmd.ExecuteScalar();
        Assert.That(oidAfter, Is.Not.EqualTo(oidBefore), "View OID should change (definition changed triggers rebuild)");

        // Verify data reflects new definition (both rows have amount > 50)
        cmd.CommandText = "SELECT COUNT(*) FROM public.mv_test";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(2), "Filtered view should have 2 rows (both > 50)");

        // Verify the definition in pg_matviews contains the WHERE clause
        cmd.CommandText = "SELECT definition FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        var definition = cmd.ExecuteScalar()?.ToString() ?? "";
        Assert.That(definition.ToUpperInvariant(), Does.Contain("WHERE"), "Definition should contain WHERE clause");

        conn.Close();
    }

    [Test]
    public void IndexOnlyChange_DoesNotRebuildView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Use stored definition to ensure no definition diff
        var storedDef = GetStoredDefinition(cmd, "mv_test");

        // Record OID before
        cmd.CommandText = @"
SELECT c.oid FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  WHERE c.relname = 'mv_test' AND c.relkind = 'm'";
        var oidBefore = cmd.ExecuteScalar();

        // Call with same definition but an additional index
        RunMaterializedViewQuench(cmd, BuildViewJson("mv_test", "public", storedDef, true, ViewIndexJson_IdAndName()));

        // Verify view OID is unchanged (view was NOT rebuilt)
        cmd.CommandText = @"
SELECT c.oid FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  WHERE c.relname = 'mv_test' AND c.relkind = 'm'";
        var oidAfter = cmd.ExecuteScalar();
        Assert.That(oidAfter, Is.EqualTo(oidBefore), "View OID should be unchanged (index-only change)");

        // Verify the new index exists
        cmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'mv_test' AND indexname = 'ix_mv_test_name'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "New index should exist");

        // Verify the original index still exists
        cmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'mv_test' AND indexname = 'ix_mv_test_id'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Original index should still exist");

        conn.Close();
    }

    [Test]
    public void EmptyArray_DropsView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Verify the view exists before
        cmd.CommandText = "SELECT COUNT(*) FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Materialized view should exist before drop test");

        // Call with empty array
        RunMaterializedViewQuench(cmd, "[]");

        // Verify the view was dropped
        cmd.CommandText = "SELECT COUNT(*) FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(0), "Materialized view should be dropped");

        // Verify ownership entry was cleaned up
        cmd.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""TableName"" = 'mv_test' AND ""ProductName"" = 'MvQuenchTests'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(0), "Ownership entry should be removed");

        conn.Close();
    }

    [Test]
    public void InitialQuench_SetsOwnership()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);
        RunMaterializedViewQuench(cmd, ViewDefinitionWithIndex());

        // Verify ownership entry exists with correct product name
        cmd.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""TableName"" = 'mv_test' AND ""ProductName"" = 'MvQuenchTests'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Ownership entry should exist with correct ProductName");

        conn.Close();
    }

    [Test]
    public void IndexRemoval_RemovesIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // First, add a second index by quenching with two indexes (using stored definition for no-rebuild)
        var storedDef = GetStoredDefinition(cmd, "mv_test");
        RunMaterializedViewQuench(cmd, BuildViewJson("mv_test", "public", storedDef, true, ViewIndexJson_IdAndName()));

        // Verify both indexes exist
        cmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'mv_test' AND indexname = 'ix_mv_test_name'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Second index should exist before removal");

        // Record OID before
        cmd.CommandText = @"
SELECT c.oid FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  WHERE c.relname = 'mv_test' AND c.relkind = 'm'";
        var oidBefore = cmd.ExecuteScalar();

        // Re-quench with only the first index (removing the second)
        storedDef = GetStoredDefinition(cmd, "mv_test");
        RunMaterializedViewQuench(cmd, BuildViewJson("mv_test", "public", storedDef, true, ViewIndexJson_IdOnly()));

        // Verify the removed index no longer exists
        cmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'mv_test' AND indexname = 'ix_mv_test_name'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(0), "Removed index should no longer exist");

        // Verify the remaining index still exists
        cmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'mv_test' AND indexname = 'ix_mv_test_id'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Remaining index should still exist");

        // Verify view OID is unchanged (index-only change should not rebuild view)
        cmd.CommandText = @"
SELECT c.oid FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  WHERE c.relname = 'mv_test' AND c.relkind = 'm'";
        var oidAfter = cmd.ExecuteScalar();
        Assert.That(oidAfter, Is.EqualTo(oidBefore), "View OID should be unchanged (index-only change)");

        conn.Close();
    }

    [Test]
    public void OwnershipFixup_ReassignsProduct()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Manually update ownership to a different product name
        cmd.CommandText = @"UPDATE ""SchemaSmith"".""ProductOwnership"" SET ""ProductName"" = 'WrongProduct' WHERE ""TableName"" = 'mv_test'";
        cmd.ExecuteNonQuery();

        // Verify it was changed
        cmd.CommandText = @"SELECT ""ProductName"" FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""TableName"" = 'mv_test'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("WrongProduct"), "Ownership should be set to wrong product");

        // Attempting to quench under the original product should fail — validation guards against stealing views
        var storedDef = GetStoredDefinition(cmd, "mv_test");
        var ex = Assert.Throws<Npgsql.PostgresException>(() =>
            RunMaterializedViewQuench(cmd, BuildViewJson("mv_test", "public", storedDef, true, ViewIndexJson_IdOnly())));
        Assert.That(ex!.MessageText, Does.Contain("already owned by another product"), "Should reject quench for view owned by different product");

        // Fix ownership back and verify quench succeeds
        cmd.CommandText = @"UPDATE ""SchemaSmith"".""ProductOwnership"" SET ""ProductName"" = 'MvQuenchTests' WHERE ""TableName"" = 'mv_test'";
        cmd.ExecuteNonQuery();

        storedDef = GetStoredDefinition(cmd, "mv_test");
        Assert.DoesNotThrow(() =>
            RunMaterializedViewQuench(cmd, BuildViewJson("mv_test", "public", storedDef, true, ViewIndexJson_IdOnly())));

        // Verify ownership remains correct
        cmd.CommandText = @"SELECT ""ProductName"" FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""TableName"" = 'mv_test'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(_productName), "Ownership should remain with original product");

        conn.Close();
    }

    [Test]
    public void TableQuench_DropRemovedTables_DoesNotDropMaterializedViews()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        // Ensure a materialized view exists with ownership registered
        EnsureViewExists(cmd);

        // Verify materialized view exists before table quench
        cmd.CommandText = "SELECT COUNT(*) FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Materialized view should exist before table quench");

        // Verify ownership is registered
        cmd.CommandText = $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ProductOwnership"" WHERE ""TableName"" = 'mv_test' AND ""IndexName"" IS NULL AND ""ProductName"" = '{_productName}'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.GreaterThan(0), "Materialized view ownership should be registered");

        // Run TableQuench with DropTablesRemovedFromProduct=true, passing only a regular table definition.
        // The materialized view is in ProductOwnership but NOT in the table JSON — this is the scenario
        // where the bug would cause DROP TABLE on the materialized view (error 42809).
        var tableJson = @"[{""Name"":""test_source"",""Schema"":""public"",""Columns"":[{""Name"":""id"",""DataType"":""INT4""},{""Name"":""name"",""DataType"":""VARCHAR(100)"",""Nullable"":true},{""Name"":""amount"",""DataType"":""NUMERIC(10, 2)"",""Nullable"":true}],""Indexes"":[],""ForeignKeys"":[],""CheckConstraints"":[],""ExcludeConstraints"":[],""Statistics"":[]}]";
        cmd.CommandText = $@"CALL ""SchemaSmith"".""TableQuench""(
            p_ProductName := '{_productName}',
            p_TableDefinitions := '{tableJson.Replace("'", "''")}',
            p_DropTablesRemovedFromProduct := true,
            p_DropUnknownIndexes := false
        )";
        Assert.DoesNotThrow(() => cmd.ExecuteNonQuery(), "TableQuench should not fail when materialized view exists in ProductOwnership");

        // Verify materialized view was NOT dropped
        cmd.CommandText = "SELECT COUNT(*) FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Materialized view should survive table quench with DropTablesRemovedFromProduct=true");

        conn.Close();
    }

    [Test]
    public void KindleTheForge_UpdatesProcedure_WhenBodyChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        // Deploy initial version via KindleTheForge
        // force: the version-gated kindle would otherwise skip after the fixture's initial kindle
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL, forceReKindle: true);

        // Verify procedure exists
        cmd.CommandText = @"SELECT prosrc FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE p.proname = 'ExecuteOrDebug' AND n.nspname = 'SchemaSmith'";
        var body1 = (string)cmd.ExecuteScalar()!;
        Assert.That(body1, Is.Not.Null.And.Not.Empty);

        // Deploy a modified version of the procedure using the same split path
        var modifiedScript = @"
CREATE OR REPLACE PROCEDURE ""SchemaSmith"".""ExecuteOrDebug""(p_Script TEXT, p_WhatIf BOOLEAN)
    LANGUAGE plpgsql
AS $$
BEGIN
    -- MODIFIED BODY FOR TEST
    IF p_Script IS NOT NULL AND LENGTH(TRIM(p_Script)) > 0 THEN
        IF p_WhatIf THEN
            RAISE NOTICE '%', p_Script;
        ELSE
            EXECUTE p_Script;
        END IF;
    END IF;
END $$;";
        foreach (var statement in PostgreSqlStatementSplitter.Split(modifiedScript))
        {
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }

        // Verify body actually changed
        cmd.CommandText = @"SELECT prosrc FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE p.proname = 'ExecuteOrDebug' AND n.nspname = 'SchemaSmith'";
        var body2 = (string)cmd.ExecuteScalar()!;
        Assert.That(body2, Does.Contain("MODIFIED BODY FOR TEST"), "Procedure body should reflect the updated code");
        Assert.That(body2, Is.Not.EqualTo(body1), "Body should differ from original deployment");

        // Re-deploy original via KindleTheForge to restore correct state
        // force: the version-gated kindle would otherwise skip, leaving the modified proc body in place
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL, forceReKindle: true);

        // Verify it went back to the original
        cmd.CommandText = @"SELECT prosrc FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE p.proname = 'ExecuteOrDebug' AND n.nspname = 'SchemaSmith'";
        var body3 = (string)cmd.ExecuteScalar()!;
        Assert.That(body3, Does.Not.Contain("MODIFIED BODY FOR TEST"), "KindleTheForge should restore the original body");

        conn.Close();
    }

    private void RunMaterializedViewQuench(IDbCommand cmd, string viewDefinitionsJson)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"CALL ""SchemaSmith"".""MaterializedViewQuench""(
  '{_productName}',
  '{viewDefinitionsJson.Replace("'", "''")}',
  false,
  true
);";
        var retry = true;
        var tries = 0;
        while (retry && tries++ < 10)
        {
            try
            {
                cmd.ExecuteNonQuery();
                retry = false;
            }
            catch (Exception e)
            {
                if (!e.Message.Contains("deadlock detected", StringComparison.OrdinalIgnoreCase)) throw;
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>
    /// Reads the definition back from pg_matviews as PostgreSQL stores it.
    /// The procedure compares the input definition against this stored text,
    /// so using the stored form ensures the diff check works correctly.
    /// </summary>
    private static string GetStoredDefinition(IDbCommand cmd, string viewName)
    {
        cmd.CommandText = $"SELECT TRIM(definition) FROM pg_matviews WHERE schemaname = 'public' AND matviewname = '{viewName}'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string BuildViewJson(string name, string schema, string definition, bool withData, string indexesJson)
    {
        // Escape the definition for JSON embedding
        var escapedDef = definition.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
        return $@"[{{""Name"":""{name}"",""Schema"":""{schema}"",""Definition"":""{escapedDef}"",""WithData"":{(withData ? "true" : "false")},""Indexes"":{indexesJson}}}]";
    }

    private static string ViewIndexJson_IdOnly()
    {
        return @"[{""Name"":""ix_mv_test_id"",""Unique"":true,""IndexColumns"":""id"",""AccessMethod"":""btree"",""FillFactor"":90}]";
    }

    private static string ViewIndexJson_IdAndName()
    {
        return @"[{""Name"":""ix_mv_test_id"",""Unique"":true,""IndexColumns"":""id"",""AccessMethod"":""btree"",""FillFactor"":90},{""Name"":""ix_mv_test_name"",""Unique"":false,""IndexColumns"":""name"",""AccessMethod"":""btree"",""FillFactor"":90}]";
    }

    private static string ViewDefinitionWithIndex()
    {
        return @"[{""Name"":""mv_test"",""Schema"":""public"",""Definition"":""SELECT id, name, amount FROM public.test_source"",""WithData"":true,""Indexes"":[{""Name"":""ix_mv_test_id"",""Unique"":true,""IndexColumns"":""id"",""AccessMethod"":""btree"",""FillFactor"":90}]}]";
    }

    private static string ViewDefinitionWithWhereClause()
    {
        return @"[{""Name"":""mv_test"",""Schema"":""public"",""Definition"":""SELECT id, name, amount FROM public.test_source WHERE amount > 50"",""WithData"":true,""Indexes"":[{""Name"":""ix_mv_test_id"",""Unique"":true,""IndexColumns"":""id"",""AccessMethod"":""btree"",""FillFactor"":90}]}]";
    }

    /// <summary>
    /// Ensures mv_test exists with the standard definition and single index.
    /// Idempotent — safe to call whether the view exists or not.
    /// </summary>
    private void EnsureViewExists(IDbCommand cmd)
    {
        RunMaterializedViewQuench(cmd, ViewDefinitionWithIndex());
    }

    /// <summary>
    /// Drops mv_test if it exists by quenching with an empty array.
    /// </summary>
    private void EnsureViewDropped(IDbCommand cmd)
    {
        RunMaterializedViewQuench(cmd, "[]");
    }

    [Test]
    public void WithNoData_CreatesEmptyMaterializedView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);

        var viewJson = BuildViewJson("mv_test", "public",
            "SELECT id, name, amount FROM public.test_source",
            withData: false, ViewIndexJson_IdOnly());
        RunMaterializedViewQuench(cmd, viewJson);

        // Verify materialized view exists
        cmd.CommandText = "SELECT COUNT(*) FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        Assert.That((long)cmd.ExecuteScalar()!, Is.EqualTo(1), "Materialized view should exist");

        // Verify no data was populated — querying a WITH NO DATA view requires ispopulated check
        cmd.CommandText = "SELECT ispopulated FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(false), "Materialized view should not be populated (WITH NO DATA)");

        conn.Close();
    }

    [Test]
    public void WithData_PopulatesMaterializedView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_adminConnectionString);
        conn.Open();
        conn.ChangeDatabase(_mvTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);

        var viewJson = BuildViewJson("mv_test", "public",
            "SELECT id, name, amount FROM public.test_source",
            withData: true, ViewIndexJson_IdOnly());
        RunMaterializedViewQuench(cmd, viewJson);

        // Verify view is populated
        cmd.CommandText = "SELECT ispopulated FROM pg_matviews WHERE schemaname = 'public' AND matviewname = 'mv_test'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true), "Materialized view should be populated (WITH DATA)");

        // Verify actual data exists
        cmd.CommandText = "SELECT COUNT(*) FROM public.mv_test";
        Assert.That((long)cmd.ExecuteScalar()!, Is.GreaterThan(0), "Materialized view should contain data");

        conn.Close();
    }
}
