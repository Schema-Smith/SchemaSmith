// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data.Common;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Two products may each own one of a pair of tables whose names differ ONLY by case.
/// <para>This is the SECOND path to the same deploy-fatal outcome as
/// <see cref="TableQuench_CaseDifferingTableNamesSharedTests"/>, and it survived that fix. That one
/// repaired the temp tables that snapshot the catalog; this one is about the PERSISTENT bookkeeping.
/// <c>SchemaSmith_ProductOwnership</c> keys on <c>uk_object (ObjectType, ObjectSchema, ObjectName)</c>
/// with those columns on the table's <c>utf8mb4_unicode_ci</c> default, and STEP 0's ownership check
/// compared them case-insensitively. So on a server with <c>lower_case_table_names = 0</c> a second
/// product declaring the lowercase twin was refused outright:</para>
/// <code>Table CaseOwnProbe is already owned by another product: &lt;first product&gt;</code>
/// <para>Note it names a table the refused package never declared — the clearest sign the comparison
/// had conflated two different tables rather than found a real conflict.</para>
/// <para>Both halves are needed and this test fails without either. Fixing only the comparison lets the
/// deploy through, but <c>INSERT IGNORE</c> then collides on the still-case-insensitive unique key and
/// the twin silently gets NO ownership row — unprotected, and invisible to drop-by-absence. Hence the
/// second assertion: it is the one that catches a half-fix that merely stops the error.</para>
/// <para>Under <c>lower_case_table_names >= 1</c> the server folds identifiers, so the two names are one
/// table and one owner is the correct answer; the test asserts that instead.</para>
/// </summary>
[Category("Integration")]
public abstract class TableQuench_CaseDifferingOwnershipSharedTests : BaseTableQuenchTests
{
    private const string Upper = "CaseOwnProbe";
    private const string Lower = "caseownprobe";
    private const string ProductUpper = "Case Ownership Upper";
    private const string ProductLower = "Case Ownership Lower";

    private string Json(string table) => $$"""
        {
            "Schema": "{{_mainDb}}",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "id", "DataType": "INT", "Nullable": false }
            ]
        }
        """;

    [Test]
    public void TwoProducts_MayEachOwnOneOfAPairDifferingOnlyByCase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        CleanUp(cmd);
        Exec(cmd, $"CREATE TABLE `{Upper}` (`id` INT NOT NULL)");

        // Ask the server what it does, rather than inferring it from a CREATE that was allowed to fail.
        // The old form ran `CREATE TABLE <lower>` and read a swallowed DbException as "this server
        // folds identifiers" -- so ANY unrelated failure of that statement (a leftover table from an
        // interrupted run, a permission change, a future image defaulting the variable to 1) silently
        // downgraded this test to a two-line no-op that skips the entire bug it exists to cover, with
        // nothing in the output to say so.
        var caseSensitiveServer = Scalar(cmd, "SELECT @@lower_case_table_names") == "0";

        if (caseSensitiveServer)
            Exec(cmd, $"CREATE TABLE `{Lower}` (`id` INT NOT NULL)");

        try
        {
            RunTableQuenchProc(cmd, Json(Upper), productName: ProductUpper);

            if (!caseSensitiveServer)
            {
                Assert.That(OwnerOf(cmd, Upper), Is.EqualTo(ProductUpper),
                    "on a folding server the two names are one table, owned by the product that declared it");
                return;
            }

            // Before the fix this threw out of STEP 0 ownership validation, naming the OTHER table.
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(Lower), productName: ProductLower),
                "a different table that merely differs by case is not an ownership conflict, and refusing "
                + "it blocks a legitimate deploy");

            Assert.Multiple(() =>
            {
                Assert.That(OwnerOf(cmd, Upper), Is.EqualTo(ProductUpper),
                    "the first product must keep ownership of the table it declared");
                Assert.That(OwnerOf(cmd, Lower), Is.EqualTo(ProductLower),
                    "and the second must actually GET a row -- a comparison-only fix stops the error but "
                    + "loses this one to the case-insensitive unique key, leaving the table unowned");
            });
        }
        finally
        {
            CleanUp(cmd);
            conn.Close();
        }
    }

    // BINARY on ObjectName: without it this lookup is itself case-insensitive and would report the
    // first product for both tables -- reading the bug back as a pass.
    private string OwnerOf(IDbCommand cmd, string table) =>
        Scalar(cmd, $@"SELECT ProductName FROM SchemaSmith_ProductOwnership
                        WHERE ObjectType = 'TABLE' AND BINARY ObjectName = BINARY '{table}'
                          AND BINARY ObjectSchema = BINARY '{_mainDb}'");

    private void CleanUp(IDbCommand cmd)
    {
        TryExec(cmd, $"DROP TABLE IF EXISTS `{Upper}`");
        TryExec(cmd, $"DROP TABLE IF EXISTS `{Lower}`");
        TryExec(cmd, $"DELETE FROM SchemaSmith_ProductOwnership WHERE ProductName IN ('{ProductUpper}', '{ProductLower}')");
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Best-effort cleanup: run it, and do not care whether the object was there.</summary>
    private static void TryExec(IDbCommand cmd, string sql)
    {
        try
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (DbException)
        {
            // Nothing to clean up, which is a fine outcome for cleanup.
        }
    }

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }
}
