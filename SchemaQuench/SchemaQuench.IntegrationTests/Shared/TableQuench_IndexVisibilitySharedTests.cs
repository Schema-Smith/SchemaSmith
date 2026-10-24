// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

#nullable enable
using System;
using Schema.DataAccess;
using Schema.Domain;
using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for index visibility changes during table quench.
/// Tests that visibility changes (VISIBLE to INVISIBLE and vice versa) are detected and applied.
/// </summary>
public abstract class TableQuench_IndexVisibilitySharedTests : BaseTableQuenchTests
{
    private const string TestSchema = "IdxVisibilityTests";

    [Test]
    public void ShouldModifyIndexVisibilityViaIndexOnly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Read via the engine's SchemaSmith_IndexIsVisible helper (returns 1/0) rather than
        // INFORMATION_SCHEMA.STATISTICS.IS_VISIBLE — MariaDB has no IS_VISIBLE column (it exposes the
        // inverted IGNORED); the helper normalizes both engines.
        cmd.CommandText = $"SELECT `{_mainDb}`.SchemaSmith_IndexIsVisible('{TestSchema}', 'ModifyVisibilityIO', 'IDX_VisibilityIO')";
        var isVisible = Convert.ToInt32(cmd.ExecuteScalar());
        if (TargetSupportsInvisibleIndexes())
            Assert.That(isVisible, Is.EqualTo(0), "Index should be invisible after IndexOnly quench (at/above the floor)");
        else
            // Below MySQL 8.0 / MariaDB 10.6 the INVISIBLE/IGNORED keyword doesn't exist; the declared invisible
            // index degrades to visible (the clause is suppressed and the compare ignores the visibility diff).
            Assert.That(isVisible, Is.EqualTo(1), "Invisible index degrades to visible below the floor (MySQL 8.0 / MariaDB 10.6)");

        conn.Close();
    }

    [Test]
    public void ShouldModifyIndexVisibilityViaTableQuench()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Engine-neutral read; see the IndexOnly test above.
        cmd.CommandText = $"SELECT `{_mainDb}`.SchemaSmith_IndexIsVisible('{TestSchema}', 'ModifyVisibilityTQ', 'IDX_VisibilityTQ')";
        var isVisible = Convert.ToInt32(cmd.ExecuteScalar());
        if (TargetSupportsInvisibleIndexes())
        {
            Assert.That(isVisible, Is.EqualTo(0), "Index should be invisible after table quench (at/above the floor)");
        }
        else
        {
            Assert.That(isVisible, Is.EqualTo(1), "Invisible index degrades to visible below the floor (MySQL 8.0 / MariaDB 10.6)");
            // The degrade is surfaced as a 'downgraded' manifest row (MissingIndexesAndConstraintsQuench STEP 0.6).
            cmd.CommandText = $"SELECT COUNT(*) FROM `{_mainDb}`.SchemaSmith_ChangeAudit WHERE ActionType = 'downgraded' AND ObjectName LIKE '%IDX_VisibilityTQ%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThanOrEqualTo(1), "A 'downgraded' manifest row should record the invisible-index degrade below the floor");
        }

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        // Runs on every supported version: at/above MySQL 8.0 / MariaDB 10.6 the index becomes invisible;
        // below the floor the quench degrades the declared invisible index to visible instead of parse-erroring
        // on the INVISIBLE/IGNORED keyword (the tests assert the version-adaptive outcome). The raw setup below
        // never emits INVISIBLE itself — only the quench does, and that emit is now version-gated.
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Drop and recreate to ensure clean state
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{TestSchema}`";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CREATE DATABASE `{TestSchema}`";
        cmd.ExecuteNonQuery();

        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"
-- ShouldModifyIndexVisibilityViaIndexOnly
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyVisibilityIO` (`Column1` INT NOT NULL, `Column2` INT NOT NULL);
CREATE INDEX `IDX_VisibilityIO` ON `{TestSchema}`.`ModifyVisibilityIO` (`Column1`);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyVisibilityIO');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'INDEX', 'ModifyVisibilityIO.IDX_VisibilityIO');
-- ShouldModifyIndexVisibilityViaTableQuench
CREATE TABLE IF NOT EXISTS `{TestSchema}`.`ModifyVisibilityTQ` (`Column1` INT NOT NULL, `Column2` INT NOT NULL);
CREATE INDEX `IDX_VisibilityTQ` ON `{TestSchema}`.`ModifyVisibilityTQ` (`Column1`);
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'TABLE', 'ModifyVisibilityTQ');
INSERT INTO `{_mainDb}`.SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{TestSchema}', 'INDEX', 'ModifyVisibilityTQ.IDX_VisibilityTQ');
";
        cmd.ExecuteNonQuery();

        // Table quench path: make IDX_VisibilityTQ invisible. The modified-index detection (and the
        // drop half of a drop-and-recreate) lives in ModifiedTableQuench; MissingIndexesAndConstraintsQuench
        // does the create half. Both calls below are required for this path.
        var jsonTQ = """
        [
            {
                "Name": "ModifyVisibilityTQ",
                "Columns": [
                    { "Name": "Column1", "DataType": "INT", "Nullable": false },
                    { "Name": "Column2", "DataType": "INT", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "IDX_VisibilityTQ", "IndexColumns": "Column1", "Visible": false }
                ]
            }
        ]
        """;

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{jsonTQ.Replace("'", "''")}')";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{TestSchema}', 0)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 0, 0, 1, 1, 1, 1, 0, 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{TestSchema}', 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Index Only path: make IDX_VisibilityIO invisible via IndexOnlyQuench
        var jsonIO = """
        [
            {
                "Name": "ModifyVisibilityIO",
                "Indexes": [
                    { "Name": "IDX_VisibilityIO", "IndexColumns": "Column1", "Visible": false }
                ]
            }
        ]
        """;

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{jsonIO.Replace("'", "''")}')";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_IndexOnlyQuench('{_productName}', '{TestSchema}', 0, 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        conn.Close();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{TestSchema}`";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch { /* Ignore cleanup errors */ }
    }
}
