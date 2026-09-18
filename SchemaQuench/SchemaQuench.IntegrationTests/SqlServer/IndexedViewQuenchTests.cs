// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[TestFixture]
public class IndexedViewQuenchTests
{
    private readonly string _productName = "IvQuenchTests";
    private string _connectionString;
    private string _ivTestDb = "";

    [OneTimeSetUp]
    public void Setup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _ivTestDb = $"IvTest_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_ivTestDb}];";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_ivTestDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        // Create Test schema and source table
        cmd.CommandText = "EXEC('CREATE SCHEMA [Test]')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE TABLE Test.SourceTable (Id INT NOT NULL, Name VARCHAR(100) NOT NULL, Amount DECIMAL(10,2) NOT NULL);
CREATE UNIQUE CLUSTERED INDEX UDX_SourceTable ON Test.SourceTable (Id);
";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        if (string.IsNullOrEmpty(_ivTestDb)) return;
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
IF DB_ID('{_ivTestDb}') IS NOT NULL
  ALTER DATABASE [{_ivTestDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE IF EXISTS [{_ivTestDb}];
";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void InitialQuench_CreatesViewWithClusteredAndNonclusteredIndexes()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);

        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());

        // Verify indexed view exists
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'
AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Indexed view should exist");

        // Verify clustered index exists
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Id'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Clustered index should exist");

        // Verify nonclustered index exists
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Name'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Nonclustered index should exist");

        // Verify ownership extended property
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.extended_properties ep
INNER JOIN sys.views v ON ep.major_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'
AND ep.name = 'SchemaSmith_Product' AND CAST(ep.value AS NVARCHAR(200)) = 'IvQuenchTests'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Ownership extended property should be set");

        conn.Close();
    }

    [Test]
    public void ReQuench_WithNoChanges_ViewStillExists()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());

        // Verify the view still exists
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'
AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Indexed view should still exist");

        // Verify both indexes still exist
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Id'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Clustered index should still exist");

        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Name'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Nonclustered index should still exist");

        conn.Close();
    }

    [Test]
    public void ReQuench_WithNoChanges_DoesNotRebuildView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Record object_id before re-quench
        cmd.CommandText = @"
SELECT v.object_id FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var objectIdBefore = cmd.ExecuteScalar();

        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());

        // Verify object_id is unchanged (view was NOT dropped and recreated)
        cmd.CommandText = @"
SELECT v.object_id FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var objectIdAfter = cmd.ExecuteScalar();
        Assert.That(objectIdAfter, Is.EqualTo(objectIdBefore), "View object_id should be unchanged (no-op re-quench)");

        conn.Close();
    }

    [Test]
    public void DefinitionChange_TriggersRebuild()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Record object_id before
        cmd.CommandText = @"
SELECT v.object_id FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var objectIdBefore = cmd.ExecuteScalar();

        // Quench with a modified definition (added WHERE clause)
        RunIndexedViewQuench(cmd, ViewDefinitionWithWhereClause());

        // Verify view still exists
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'
AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Indexed view should exist after rebuild");

        // Verify object_id changed (view was dropped and recreated)
        cmd.CommandText = @"
SELECT v.object_id FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var objectIdAfter = cmd.ExecuteScalar();
        Assert.That(objectIdAfter, Is.Not.EqualTo(objectIdBefore), "View object_id should change (definition changed triggers rebuild)");

        // Verify the definition contains the WHERE clause
        cmd.CommandText = @"
SELECT m.definition FROM sys.sql_modules m
INNER JOIN sys.views v ON m.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var definition = cmd.ExecuteScalar()?.ToString() ?? "";
        Assert.That(definition.ToUpperInvariant(), Does.Contain("WHERE"), "Definition should contain WHERE clause");

        conn.Close();
    }

    [Test]
    public void IndexOnlyChange_DoesNotRebuildView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        // Establish the WHERE clause definition first so adding an index is an index-only change
        RunIndexedViewQuench(cmd, ViewDefinitionWithWhereClause());

        // Record object_id before
        cmd.CommandText = @"
SELECT v.object_id FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var objectIdBefore = cmd.ExecuteScalar();

        // Quench with same definition but an additional nonclustered index
        RunIndexedViewQuench(cmd, ViewDefinitionWithThreeIndexes());

        // Verify view object_id is unchanged (view was NOT rebuilt)
        cmd.CommandText = @"
SELECT v.object_id FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var objectIdAfter = cmd.ExecuteScalar();
        Assert.That(objectIdAfter, Is.EqualTo(objectIdBefore), "View object_id should be unchanged (index-only change)");

        // Verify the new index exists
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Amount'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "New index should exist");

        conn.Close();
    }

    [Test]
    public void EmptyArray_DropsView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Verify the view exists before
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'
AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Indexed view should exist before drop test");

        // Call with empty array
        RunIndexedViewQuench(cmd, "[]");

        // Verify the view was dropped
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(0), "Indexed view should be dropped");

        // Verify ownership extended property was removed (no view = no EP)
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.extended_properties ep
WHERE ep.name = 'SchemaSmith_Product' AND CAST(ep.value AS NVARCHAR(200)) = 'IvQuenchTests'
AND ep.major_id NOT IN (SELECT object_id FROM sys.tables)";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(0), "Ownership extended property should be removed");

        conn.Close();
    }

    [Test]
    public void IndexRemoval_RemovesIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Record object_id before
        cmd.CommandText = @"
SELECT v.object_id FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var objectIdBefore = cmd.ExecuteScalar();

        // Re-quench with only the clustered index (removing the nonclustered)
        RunIndexedViewQuench(cmd, ViewDefinitionWithClusteredOnly());

        // Verify nonclustered index no longer exists
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Name'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(0), "Nonclustered index should be removed");

        // Verify clustered index still exists
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Id'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Clustered index should still exist");

        // Verify view object_id is unchanged (index-only change)
        cmd.CommandText = @"
SELECT v.object_id FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var objectIdAfter = cmd.ExecuteScalar();
        Assert.That(objectIdAfter, Is.EqualTo(objectIdBefore), "View object_id should be unchanged (index-only change)");

        conn.Close();
    }

    [Test]
    public void OwnershipFixup_ReassignsProduct()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewExists(cmd);

        // Manually update the extended property to a different value
        cmd.CommandText = @"
EXEC sp_updateextendedproperty
    @name = N'SchemaSmith_Product',
    @value = N'WrongProduct',
    @level0type = N'SCHEMA', @level0name = N'Test',
    @level1type = N'VIEW', @level1name = N'vTestSummary'";
        cmd.ExecuteNonQuery();

        // Verify it was changed
        cmd.CommandText = @"
SELECT CAST(ep.value AS NVARCHAR(200)) FROM sys.extended_properties ep
INNER JOIN sys.views v ON ep.major_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'
AND ep.name = 'SchemaSmith_Product'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("WrongProduct"), "Extended property should be set to wrong product");

        // Attempting to quench under the original product should fail — the procedure doesn't see
        // the existing view as its own and tries to CREATE it, causing a name conflict
        var ex = Assert.Throws<Microsoft.Data.SqlClient.SqlException>(() =>
            RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes()));
        Assert.That(ex!.Message, Does.Contain("already"), "Should fail when view is owned by different product");

        // Fix ownership back and verify quench succeeds
        cmd.CommandText = @"
EXEC sp_updateextendedproperty
    @name = N'SchemaSmith_Product',
    @value = N'IvQuenchTests',
    @level0type = N'SCHEMA', @level0name = N'Test',
    @level1type = N'VIEW', @level1name = N'vTestSummary'";
        cmd.ExecuteNonQuery();

        Assert.DoesNotThrow(() => RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes()));

        // Verify ownership remains correct
        cmd.CommandText = @"
SELECT CAST(ep.value AS NVARCHAR(200)) FROM sys.extended_properties ep
INNER JOIN sys.views v ON ep.major_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'
AND ep.name = 'SchemaSmith_Product'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(_productName), "Extended property should remain with original product");

        conn.Close();
    }

    [Test]
    public void MultipleViews_QuenchedIndependently()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        // Create a second source table
        cmd.CommandText = @"
IF OBJECT_ID('Test.SourceTable2', 'U') IS NULL
BEGIN
    CREATE TABLE Test.SourceTable2 (Code INT NOT NULL, Label VARCHAR(100) NOT NULL, Value DECIMAL(10,2) NOT NULL);
    CREATE UNIQUE CLUSTERED INDEX UDX_SourceTable2 ON Test.SourceTable2 (Code);
END";
        cmd.ExecuteNonQuery();

        // Quench with two indexed views
        RunIndexedViewQuench(cmd, TwoViewDefinitions());

        // Verify both views exist
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'
AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "First indexed view should exist");

        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary2'
AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Second indexed view should exist");

        // Verify indexes on second view
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary2' AND i.name = 'IX_vTestSummary2_Code'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Second view clustered index should exist");

        // Drop the second view by quenching with only the first
        RunIndexedViewQuench(cmd, ViewDefinitionWithClusteredOnly());

        // Verify first view still exists
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "First view should still exist");

        // Verify second view was dropped
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary2'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(0), "Second view should be dropped");

        conn.Close();
    }

    [Test]
    public void CreatingView_EchoesVariantNameInOperationMessage()
    {
        var messages = new System.Collections.Generic.List<string>();
        using var conn = (Microsoft.Data.SqlClient.SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.InfoMessage += (_, e) => { foreach (Microsoft.Data.SqlClient.SqlError err in e.Errors) messages.Add(err.Message); };
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);

        var json = @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""VariantName"":""Modern engines"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""}]}]";
        RunIndexedViewQuench(cmd, json);

        Assert.That(messages, Has.Some.Contains("(variant: Modern engines)"));

        EnsureViewDropped(cmd);
        conn.Close();
    }

    /// <summary>
    /// Ensures vTestSummary exists with the standard two-index definition.
    /// Idempotent — safe to call whether the view exists or not.
    /// </summary>
    private void EnsureViewExists(IDbCommand cmd)
    {
        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());
    }
    // PAD_INDEX on an indexed view index. SQL Server accepts it here (verified on 2022 with the correct
    // SET options -- an earlier probe reported a false rejection that was actually a QUOTED_IDENTIFIER
    // complaint). IGNORE_DUP_KEY is deliberately absent: the engine rejects it on a view index outright,
    // so there is nothing to manage.
    //
    // Without this, PadIndex would appear in the indexed-view JSON schema -- it rides the shared
    // SqlServerIndex type -- and silently do nothing, which is the same "editor offers a setting that
    // does nothing" defect the platform-scoping work exists to prevent.

    [Test]
    public void PadIndex_OnAnIndexedViewIndex_IsDeployed()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);
        RunIndexedViewQuench(cmd, ViewDefinitionWithPadIndex(true));

        cmd.CommandText = @"
SELECT CONVERT(INT, i.is_padded) FROM sys.indexes i
INNER JOIN sys.views v ON v.object_id = i.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Id'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1),
            "declaring PadIndex on an indexed view index has to actually reach the engine -- otherwise the "
            + "schema offers a setting that does nothing");
    }

    [Test]
    public void PadIndex_OnAnIndexedViewIndex_IsIdempotent()
    {
        // The churn guard, same as for table indexes: if the existing-vs-desired comparison does not
        // carry PadIndex, the second deploy either drops and recreates the index or misses a change.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);
        RunIndexedViewQuench(cmd, ViewDefinitionWithPadIndex(true));
        RunIndexedViewQuench(cmd, ViewDefinitionWithPadIndex(true));

        cmd.CommandText = @"
SELECT CONVERT(INT, i.is_padded) FROM sys.indexes i
INNER JOIN sys.views v ON v.object_id = i.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Id'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "still set after a re-deploy");
    }

    [Test]
    public void NotDeclaringPadIndex_OnAnIndexedViewIndex_LeavesItOff()
    {
        // The negative half: an emit that added PAD_INDEX unconditionally would pass the assertions above
        // while changing every indexed view index in every package.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);
        RunIndexedViewQuench(cmd, ViewDefinitionWithPadIndex(false));

        cmd.CommandText = @"
SELECT CONVERT(INT, i.is_padded) FROM sys.indexes i
INNER JOIN sys.views v ON v.object_id = i.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Id'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.Zero);
    }

    [Test]
    public void ChangingPadIndex_OnAnExistingIndexedViewIndex_IsApplied()
    {
        // Idempotence and create-from-scratch both pass happily while the existing-vs-desired comparison
        // ignores PadIndex entirely -- proven by mutation: breaking that comparison left all three of the
        // other tests green. This is the one that exercises it.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);
        RunIndexedViewQuench(cmd, ViewDefinitionWithPadIndex(false));

        RunIndexedViewQuench(cmd, ViewDefinitionWithPadIndex(true));

        cmd.CommandText = @"
SELECT CONVERT(INT, i.is_padded) FROM sys.indexes i
INNER JOIN sys.views v ON v.object_id = i.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Id'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1),
            "turning PadIndex on in the package has to reach an index that already exists, not only a "
            + "freshly created one");
    }

    private static string ViewDefinitionWithPadIndex(bool padIndex) =>
        @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]"",""FillFactor"":70,""PadIndex"":"
        + (padIndex ? "true" : "false") + @"}]}]";



    /// <summary>
    /// Drops vTestSummary (and any other views) by quenching with an empty array.
    /// </summary>
    private void EnsureViewDropped(IDbCommand cmd)
    {
        RunIndexedViewQuench(cmd, "[]");
    }

    // #242. ReQuench_WithNoChanges_ViewStillExists asserts the view is still THERE, which a drop-and-recreate
    // satisfies just as well as leaving it alone. This asserts the view's object_id is stable across three
    // deploys: re-creating an indexed view rebuilds its clustered index, so churn here is expensive.
    [Test]
    public void ReQuench_WithNoChanges_DoesNotRecreateTheView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());
        cmd.CommandText = @"SELECT v.object_id FROM sys.views v
                            INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
                            WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var firstId = cmd.ExecuteScalar();
        Assert.That(firstId, Is.Not.Null, "setup: the indexed view must exist after the first deploy");

        for (var pass = 2; pass <= 3; pass++)
        {
            RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());
            cmd.CommandText = @"SELECT v.object_id FROM sys.views v
                                INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
                                WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(firstId),
                $"pass {pass}: the indexed view was dropped and re-created for an unchanged declaration");
        }

        conn.Close();
    }

    private void RunIndexedViewQuench(IDbCommand cmd, string indexedViewJson)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"EXEC [SchemaSmith].[IndexedViewQuench]
    @ProductName = '{_productName}',
    @IndexedViewSchema = '{indexedViewJson.Replace("'", "''")}',
    @WhatIf = 0,
    @UpdateFillFactor = 0,
    @TemplateName = N'Main',
    @SchemaName = N''";
        cmd.ExecuteNonQuery();
    }

    private static string ViewDefinitionWithTwoIndexes()
    {
        return @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""},{""Name"":""[IX_vTestSummary_Name]"",""Unique"":false,""Clustered"":false,""IndexColumns"":""[Name]""}]}]";
    }

    private static string ViewDefinitionWithWhereClause()
    {
        return @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable WHERE Amount > 0 GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""},{""Name"":""[IX_vTestSummary_Name]"",""Unique"":false,""Clustered"":false,""IndexColumns"":""[Name]""}]}]";
    }

    private static string ViewDefinitionWithThreeIndexes()
    {
        return @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable WHERE Amount > 0 GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""},{""Name"":""[IX_vTestSummary_Name]"",""Unique"":false,""Clustered"":false,""IndexColumns"":""[Name]""},{""Name"":""[IX_vTestSummary_Amount]"",""Unique"":false,""Clustered"":false,""IndexColumns"":""[Name],[Id]""}]}]";
    }

    private static string ViewDefinitionWithClusteredOnly()
    {
        return @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""}]}]";
    }

    private static string TwoViewDefinitions()
    {
        return @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""}]},{""Schema"":""[Test]"",""Name"":""[vTestSummary2]"",""Definition"":""SELECT Code, Label, COUNT_BIG(*) AS Cnt, SUM(Value) AS TotalValue FROM Test.SourceTable2 GROUP BY Code, Label"",""Indexes"":[{""Name"":""[IX_vTestSummary2_Code]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Code]""}]}]";
    }

    private static string ViewDefinitionWithUniqueNameIndex()
    {
        // Same as TwoIndexes but IX_vTestSummary_Name is now UNIQUE
        return @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""},{""Name"":""[IX_vTestSummary_Name]"",""Unique"":true,""Clustered"":false,""IndexColumns"":""[Name]""}]}]";
    }

    private static string ViewDefinitionWithDifferentClusteredColumns()
    {
        // Same view definition but clustered index now covers both Id and Name
        return @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id],[Name]""},{""Name"":""[IX_vTestSummary_Name]"",""Unique"":false,""Clustered"":false,""IndexColumns"":""[Name]""}]}]";
    }

    /// <summary>
    /// Tags an index with an extended property marker. If the index is dropped, the marker disappears.
    /// </summary>
    private static void TagIndex(IDbCommand cmd, string indexName)
    {
        cmd.CommandText = $@"
IF EXISTS (SELECT 1 FROM sys.extended_properties ep
           INNER JOIN sys.indexes i ON ep.major_id = i.object_id AND ep.minor_id = i.index_id
           INNER JOIN sys.views v ON i.object_id = v.object_id
           INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
           WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = '{indexName}' AND ep.name = 'TestMarker')
    EXEC sp_dropextendedproperty 'TestMarker', 'SCHEMA', 'Test', 'VIEW', 'vTestSummary', 'INDEX', '{indexName}';
EXEC sp_addextendedproperty 'TestMarker', 'alive',
    'SCHEMA', 'Test', 'VIEW', 'vTestSummary', 'INDEX', '{indexName}'";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Checks whether the extended property marker still exists on an index.
    /// Returns true if the index was NOT dropped (marker survives).
    /// </summary>
    private static bool IndexMarkerSurvived(IDbCommand cmd, string indexName)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM sys.extended_properties ep
INNER JOIN sys.indexes i ON ep.major_id = i.object_id AND ep.minor_id = i.index_id
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = '{indexName}'
AND ep.name = 'TestMarker'";
        return (int)cmd.ExecuteScalar()! > 0;
    }

    // --- Index Diff Optimization Tests ---

    [Test]
    public void IndexDiff_UnchangedIndexesSurvive_WhenNonclusteredPropertyChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        // Establish baseline with two indexes
        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());

        // Tag both indexes with markers
        TagIndex(cmd, "IX_vTestSummary_Id");
        TagIndex(cmd, "IX_vTestSummary_Name");

        // Change the nonclustered index to UNIQUE (property change)
        RunIndexedViewQuench(cmd, ViewDefinitionWithUniqueNameIndex());

        // Clustered index should survive (marker intact)
        Assert.That(IndexMarkerSurvived(cmd, "IX_vTestSummary_Id"), Is.True,
            "Clustered index should NOT be dropped when only a nonclustered index changes");

        // Nonclustered index was recreated (marker gone)
        Assert.That(IndexMarkerSurvived(cmd, "IX_vTestSummary_Name"), Is.False,
            "Changed nonclustered index should be dropped and recreated");

        // Verify the nonclustered index is now UNIQUE
        cmd.CommandText = @"
SELECT i.is_unique FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Name'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true), "Nonclustered index should now be unique");

        conn.Close();
    }

    [Test]
    public void IndexDiff_AllIndexesDropped_WhenClusteredIndexChanges()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        // Establish baseline with two indexes
        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());

        // Tag both indexes
        TagIndex(cmd, "IX_vTestSummary_Id");
        TagIndex(cmd, "IX_vTestSummary_Name");

        // Change the clustered index columns
        RunIndexedViewQuench(cmd, ViewDefinitionWithDifferentClusteredColumns());

        // Both indexes should be recreated (markers gone) because clustered changed
        Assert.That(IndexMarkerSurvived(cmd, "IX_vTestSummary_Id"), Is.False,
            "Clustered index should be dropped when its columns change");
        Assert.That(IndexMarkerSurvived(cmd, "IX_vTestSummary_Name"), Is.False,
            "Nonclustered indexes must be dropped when clustered index changes");

        // Verify both indexes exist with correct properties
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Id' AND i.type = 1";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Clustered index should be recreated");

        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Name'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Nonclustered index should be recreated");

        conn.Close();
    }

    [Test]
    public void IndexDiff_AddNewIndex_ExistingIndexesSurvive()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        // Establish baseline with two indexes
        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());

        // Tag both indexes
        TagIndex(cmd, "IX_vTestSummary_Id");
        TagIndex(cmd, "IX_vTestSummary_Name");

        // Add a third index (same definition, extra nonclustered)
        var threeIndexes = @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""},{""Name"":""[IX_vTestSummary_Name]"",""Unique"":false,""Clustered"":false,""IndexColumns"":""[Name]""},{""Name"":""[IX_vTestSummary_Cnt]"",""Unique"":false,""Clustered"":false,""IndexColumns"":""[Cnt]""}]}]";
        RunIndexedViewQuench(cmd, threeIndexes);

        // Both original indexes should survive (markers intact)
        Assert.That(IndexMarkerSurvived(cmd, "IX_vTestSummary_Id"), Is.True,
            "Clustered index should survive when adding a new nonclustered index");
        Assert.That(IndexMarkerSurvived(cmd, "IX_vTestSummary_Name"), Is.True,
            "Existing nonclustered index should survive when adding a new one");

        // New index should exist
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Cnt'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "New nonclustered index should be created");

        conn.Close();
    }

    [Test]
    public void IndexDiff_NoChanges_AllIndexesSurvive()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        // Establish baseline
        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());

        // Tag both indexes
        TagIndex(cmd, "IX_vTestSummary_Id");
        TagIndex(cmd, "IX_vTestSummary_Name");

        // Re-quench with identical definition
        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());

        // Both markers should survive — no indexes were touched
        Assert.That(IndexMarkerSurvived(cmd, "IX_vTestSummary_Id"), Is.True,
            "Clustered index should survive no-op re-quench");
        Assert.That(IndexMarkerSurvived(cmd, "IX_vTestSummary_Name"), Is.True,
            "Nonclustered index should survive no-op re-quench");

        conn.Close();
    }

    // --- ShouldApplyExpression Gating Tests ---

    [Test]
    public void ShouldApplyExpression_False_ViewNotCreated()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);

        // A real expression evaluating false on the target must gate the view out — the
        // old C# filter only treated the literal string "false" as a gate, so "1 = 0"
        // would wrongly deploy.
        var json = @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""ShouldApplyExpression"":""1 = 0"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""}]}]";
        RunIndexedViewQuench(cmd, json);

        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(0), "Indexed view gated by a false ShouldApplyExpression should NOT be created");

        conn.Close();
    }

    [Test]
    public void ShouldApplyExpression_True_ViewCreated()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);

        var json = @"[{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""ShouldApplyExpression"":""1 = 1"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""}]}]";
        RunIndexedViewQuench(cmd, json);

        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'
AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "Indexed view with a true ShouldApplyExpression should be created");

        EnsureViewDropped(cmd);
        conn.Close();
    }

    [Test]
    public void ShouldApplyExpression_SameNamedVariants_MatchingOneCreated()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);

        // Two same-named variants with mutually exclusive expressions: the matching one
        // (1 = 1, with a WHERE clause) must be created; the other (1 = 0) must be gated out.
        var json = @"[
{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""VariantName"":""Legacy"",""ShouldApplyExpression"":""1 = 0"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""}]},
{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""VariantName"":""Modern"",""ShouldApplyExpression"":""1 = 1"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable WHERE Amount > 0 GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""}]}]";
        RunIndexedViewQuench(cmd, json);

        // The view should exist and be the Modern variant (has the WHERE clause)
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'
AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1), "The matching variant should be created");

        cmd.CommandText = @"
SELECT m.definition FROM sys.sql_modules m
INNER JOIN sys.views v ON m.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        var definition = cmd.ExecuteScalar()?.ToString() ?? "";
        Assert.That(definition.ToUpperInvariant(), Does.Contain("WHERE"), "The Modern variant (with WHERE clause) should be the one created");

        EnsureViewDropped(cmd);
        conn.Close();
    }

    [Test]
    public void ShouldApplyExpression_SameNamedVariants_BothSurviving_FailsWithMutuallyExclusiveError()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        EnsureViewDropped(cmd);

        // Two same-named variants whose ShouldApplyExpressions both evaluate true on this target.
        // SQL Server allows one view per (schema, name), so both surviving the gate is unhonorable
        // and the quench must fail with the mutual-exclusivity THROW (51000).
        var json = @"[
{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""VariantName"":""A"",""ShouldApplyExpression"":""1 = 1"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""}]},
{""Schema"":""[Test]"",""Name"":""[vTestSummary]"",""VariantName"":""B"",""ShouldApplyExpression"":""1 = 1"",""Definition"":""SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM Test.SourceTable WHERE Amount > 0 GROUP BY Id, Name"",""Indexes"":[{""Name"":""[IX_vTestSummary_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""}]}]";

        var ex = Assert.Throws<Microsoft.Data.SqlClient.SqlException>(() => RunIndexedViewQuench(cmd, json));
        Assert.That(ex!.Message, Does.Contain("mutually exclusive"));

        // No view should have been created.
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(0), "No view should be created when both variants survive the gate");

        EnsureViewDropped(cmd);
        conn.Close();
    }

    [Test]
    public void IndexDiff_RemoveIndex_OtherIndexesSurvive()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_ivTestDb);
        using var cmd = conn.CreateCommand();

        // Establish baseline with two indexes
        RunIndexedViewQuench(cmd, ViewDefinitionWithTwoIndexes());

        // Tag both indexes
        TagIndex(cmd, "IX_vTestSummary_Id");
        TagIndex(cmd, "IX_vTestSummary_Name");

        // Remove nonclustered index (keep only clustered)
        RunIndexedViewQuench(cmd, ViewDefinitionWithClusteredOnly());

        // Clustered should survive
        Assert.That(IndexMarkerSurvived(cmd, "IX_vTestSummary_Id"), Is.True,
            "Clustered index should survive when a nonclustered index is removed");

        // Nonclustered should be gone
        cmd.CommandText = @"
SELECT COUNT(*) FROM sys.indexes i
INNER JOIN sys.views v ON i.object_id = v.object_id
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = 'Test' AND v.name = 'vTestSummary' AND i.name = 'IX_vTestSummary_Name'";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(0), "Removed nonclustered index should be gone");

        conn.Close();
    }
}
