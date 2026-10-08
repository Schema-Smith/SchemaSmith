// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// A package with no tables, a data-only or scripts-only one, still reaches the expression-map record step. That step
/// read the session's parsed-table work tables, which such a package never creates, and every deploy logged "Could not
/// record the expression map (Unknown prepared statement handler (stmt) given to EXECUTE)".
/// </summary>
public abstract class ExpressionMapRecordSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    [Test]
    public void Record_InASessionWithNoParsedTables_DoesNothingAndDoesNotFail()
    {
        using var connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith_ExpressionMap";
        var before = cmd.ExecuteScalar();

        cmd.CommandText = $"CALL SchemaSmith_ExpressionMapRecord('{MainDb}', 0)";
        Assert.DoesNotThrow(() => cmd.ExecuteNonQuery());

        cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith_ExpressionMap";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(before), "nothing to record");
    }
}
