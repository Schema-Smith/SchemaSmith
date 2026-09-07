// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// A deploy must work against a database whose collation differs from the routine's
/// <c>collation_connection</c>. That is not an exotic configuration -- it is what every shipped MySQL and
/// MariaDB demo does (<c>utf8mb4_unicode_ci</c>, while the connection default is <c>utf8mb4_general_ci</c>
/// on older servers and <c>utf8mb4_uca1400_ai_ci</c> on MariaDB 11.4).
/// <para><b>The defect this pins.</b> <c>SchemaSmith_ModifiedTableQuench</c>'s encryption step compared
/// <c>COALESCE(SchemaSmith_CreateOption(...), '&lt;literal&gt;')</c> against a table column. The COALESCE
/// combines the function's return collation (the database default, fixed when the function was created)
/// with the literal's (the routine's stored <c>collation_connection</c>); when those differ the engine
/// resolves the result to the charset's BINARY collation with coercibility NONE, which can then be
/// compared against nothing -- <c>Illegal mix of collations (utf8mb4_bin,NONE) and (...,IMPLICIT) for
/// operation '&lt;&gt;'</c>. The step is not gated on encryption being declared, so it killed EVERY deploy
/// into a differently-collated database, on both the 10.2 floor and 11.4.</para>
/// <para><b>Why the rest of the suite missed it.</b> Every other integration fixture deploys into the
/// fixture's own database, which is created at the server default and therefore MATCHES the connection
/// collation -- the comparison degrades only when they differ. Nothing else in the suite deploys into a
/// database with a deliberately different collation, which is exactly why this fixture creates its own.
/// Found by deploying the shipped demo packages, not by the suite (Rule 24).</para>
/// </summary>
public abstract class DatabaseCollationMismatchSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainConnectionString { get; }

    private const string Product = "CollationMismatchProduct";
    private const string TableName = "collation_mismatch_test";

    private IDbConnection _connection = null!;
    private string _testDb = null!;
    private string _dbCollation = null!;

    private static string TableJson() => $$"""
        [{
            "Name": "{{TableName}}",
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Val", "DataType": "VARCHAR(50)", "Nullable": true }
            ],
            "Indexes": [ { "Name": "PRIMARY", "IndexColumns": "Id", "PrimaryKey": true, "Unique": true } ]
        }]
        """;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();

        // Pick a collation the SESSION is not already using, so the mismatch is guaranteed whatever the
        // server's default happens to be. Asserting against a hard-coded collation would silently stop
        // reproducing on a server whose default matched it -- a test that no longer bites.
        var sessionCollation = ScalarStr("SELECT @@collation_connection") ?? "";
        _dbCollation = sessionCollation.Equals("utf8mb4_unicode_ci", StringComparison.OrdinalIgnoreCase)
            ? "utf8mb4_general_ci"
            : "utf8mb4_unicode_ci";

        _testDb = $"ss_collation_mismatch_{(Platform == Platform.MariaDb ? "mariadb" : "mysql")}";

        Exec($"DROP DATABASE IF EXISTS `{_testDb}`");
        Exec($"CREATE DATABASE `{_testDb}` CHARACTER SET utf8mb4 COLLATE {_dbCollation}");
        Exec($"USE `{_testDb}`");

        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        ForgeKindler.KindleTheForge(cmd, Platform);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            if (_connection is { State: ConnectionState.Open } && _testDb != null)
                Exec($"DROP DATABASE IF EXISTS `{_testDb}`");
        }
        finally
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }

    [Test]
    public void ADeployIntoADifferentlyCollatedDatabase_Succeeds()
    {
        Assume.That(_dbCollation, Is.Not.EqualTo(ScalarStr("SELECT @@collation_connection")),
            "the test database's collation must differ from the session's, or this proves nothing");

        Assert.DoesNotThrow(() => RunTableQuench(TableJson()),
            "a deploy must not depend on the target database's collation matching the connection's -- "
            + "an option comparison that degrades to (utf8mb4_bin,NONE) cannot be compared against "
            + "anything and takes the whole quench down.");

        Assert.That(Convert.ToInt32(Scalar(
                $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{TableName}'")),
            Is.EqualTo(1), "and the table must actually be there afterwards");
    }

    [Test]
    public void ARedeployIntoADifferentlyCollatedDatabase_IsAlsoClean()
    {
        // The first deploy creates (MissingTableAndColumnQuench); only the SECOND reaches the modified
        // path, which is where the degraded comparison lives. Without this the fixture could pass while
        // the actual broken step never ran.
        RunTableQuench(TableJson());

        Assert.DoesNotThrow(() => RunTableQuench(TableJson()),
            "the modified-table pass runs on redeploy -- that is the step whose encryption comparison "
            + "degraded, so an unchanged redeploy has to survive it too.");
    }

    private void RunTableQuench(string json)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CALL SchemaSmith_TableQuench('{Product}', '{_testDb}', '{json.Replace("'", "''")}', 0, 0, 0);";
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private string ScalarStr(string sql) => Scalar(sql)?.ToString();
}
