// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data.Common;
using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Two tables whose names differ ONLY by case must not break a deploy, and quenching one must not touch
/// the other.
/// <para>This was deploy-fatal. The temp tables that snapshot the catalog key on identifier columns, and
/// those columns were declared with the table's <c>utf8mb4_unicode_ci</c> default — a case-INSENSITIVE
/// primary key. Both names come back from <c>INFORMATION_SCHEMA</c> on a server with
/// <c>lower_case_table_names = 0</c> (the Linux default, and what CI and the demo containers run), collide
/// on that key, and the deploy dies with <c>ERROR 1062: Duplicate entry 'CaseProbe' for key 'PRIMARY'</c>
/// before doing anything.</para>
/// <para>The comparisons were never the problem — <c>ModifiedTableQuench</c> alone carries 191 explicit
/// <c>BINARY</c> comparisons, i.e. case-sensitive. Only the KEYS disagreed with them. The fix declares the
/// identifier columns <c>COLLATE utf8mb4_bin</c> so uniqueness matches the comparisons already in place.
/// Under <c>lower_case_table_names >= 1</c> the server folds identifiers anyway, so nothing changes there
/// and this test simply finds one table instead of two.</para>
/// <para>Nothing else in the suite creates case-differing tables, which is exactly why this survived: it
/// costs a deploy, not a test. Asserting the SIBLING's columns and row count is the point — a fix that
/// stopped the error but let the quench reshape the wrong table would be worse than the bug.</para>
/// </summary>
[Category("Integration")]
public abstract class TableQuench_CaseDifferingTableNamesSharedTests : BaseTableQuenchTests
{
    private const string Declared = "CaseDiffProbe";
    private const string Sibling = "casediffprobe";

    private string Json(string table) => $$"""
        {
            "Schema": "{{_mainDb}}",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "id", "DataType": "INT", "Nullable": false },
                { "Name": "a",  "DataType": "INT", "Nullable": true },
                { "Name": "b",  "DataType": "INT", "Nullable": true }
            ]
        }
        """;

    [Test]
    public void ATableWhoseNameDiffersOnlyByCase_DoesNotBreakTheDeployOrGetReshaped()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        Exec(cmd, $"DROP TABLE IF EXISTS `{Declared}`");
        Exec(cmd, $"DROP TABLE IF EXISTS `{Sibling}`");
        Exec(cmd, $"CREATE TABLE `{Declared}` (`id` INT NOT NULL, `a` INT NULL)");

        // On lower_case_table_names >= 1 this folds onto the same table and the CREATE is a no-op/failure;
        // there is then only one table and nothing to conflate, so the test still passes meaningfully.
        var caseSensitiveServer = TryExec(cmd,
            $"CREATE TABLE `{Sibling}` (`id` INT NOT NULL, `x` INT NULL, `y` INT NULL)");
        if (caseSensitiveServer)
            Exec(cmd, $"INSERT INTO `{Sibling}` VALUES (1, 10, 20)");

        try
        {
            // Before the fix this threw 1062 out of the catalog snapshot, before any DDL was attempted.
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(Declared)),
                "a sibling table differing only by case must not abort the deploy");

            Assert.That(Columns(cmd, Declared), Is.EqualTo("id,a,b"),
                "the declared table must receive exactly its declared columns");

            if (caseSensitiveServer)
            {
                Assert.That(Columns(cmd, Sibling), Is.EqualTo("id,x,y"),
                    "the case-differing sibling must not be reshaped — it is a different table");
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM `{Sibling}`"), Is.EqualTo("1"),
                    "the sibling's data must survive untouched");
            }
        }
        finally
        {
            TryExec(cmd, $"DROP TABLE IF EXISTS `{Declared}`");
            TryExec(cmd, $"DROP TABLE IF EXISTS `{Sibling}`");
            conn.Close();
        }
    }

    // BINARY on both sides: without it this lookup is itself case-insensitive and would merge the two
    // tables' columns in the assertion — reporting the very bug it is meant to detect as a pass.
    private string Columns(IDbCommand cmd, string table) =>
        Scalar(cmd, $@"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION)
                         FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_SCHEMA = '{_mainDb}' AND BINARY TABLE_NAME = BINARY '{table}'");

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool TryExec(IDbCommand cmd, string sql)
    {
        try
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }
}
