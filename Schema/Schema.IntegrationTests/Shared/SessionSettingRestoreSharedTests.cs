// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// The MySQL-family session pins restore the server's value when their scope ends. When the connection dies inside the
/// scope, the session is gone and so is the setting; the restore must not replace the error that killed it.
/// </summary>
public abstract class SessionSettingRestoreSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection Open()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        conn.Open();
        return conn;
    }

    private static Exception RunInside(Func<IDbCommand, IDisposable> pin, IDbCommand cmd, string body)
    {
        try
        {
            using (pin(cmd))
            {
                cmd.CommandText = body;
                cmd.ExecuteNonQuery();
            }
        }
        catch (System.Data.Common.DbException e)
        {
            return e;
        }
        return null;
    }

    private static readonly Func<IDbCommand, IDisposable> TimeZonePin = c => MySqlSessionSettings.UseTimeZone(c, "+05:00");
    private static readonly Func<IDbCommand, IDisposable> SqlModePin = c => MySqlSessionSettings.UseParseNeutralSqlMode(c, "NO_AUTO_VALUE_ON_ZERO");

    [Test]
    public void AConnectionKilledInsideATimeZonePin_ReportsWhatKilledIt() => AssertKillIsReported(TimeZonePin);

    [Test]
    public void AConnectionKilledInsideASqlModePin_ReportsWhatKilledIt() => AssertKillIsReported(SqlModePin);

    private void AssertKillIsReported(Func<IDbCommand, IDisposable> pin)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var error = RunInside(pin, cmd, "KILL CONNECTION_ID()");

        Assert.That(error, Is.Not.Null, "killing the connection must fail the statement");
        // MySQL reports the kill as "Query execution was interrupted", MariaDB as "Connection was killed"; the restore's
        // own failure reads "Failed to read the result set".
        Assert.That(error!.Message, Does.Match("(?i)interrupted|killed"),
            "the error that killed the connection must be the one reported, not the restore's");
    }

    [Test]
    public void AnOrdinaryErrorInsideAPin_StillRestoresTheSession()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@SESSION.time_zone";
        var before = cmd.ExecuteScalar()?.ToString();

        var error = RunInside(TimeZonePin, cmd, "SELECT * FROM ss_no_such_table_restore");

        Assert.That(error?.Message, Does.Contain("ss_no_such_table_restore"));
        cmd.CommandText = "SELECT @@SESSION.time_zone";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(before));
    }
}
