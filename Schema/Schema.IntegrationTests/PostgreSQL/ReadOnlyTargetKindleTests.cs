// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// The PostgreSQL half of read-only extraction support (the feature is not
/// SQL-Server-specific, so neither is its certification).
/// <para>A hot standby is PostgreSQL's equivalent of an Availability Group readable secondary, and
/// <c>ReadOnlyTargetDetector</c> treats them the same: <c>pg_is_in_recovery()</c> catches the standby,
/// <c>transaction_read_only</c> catches a database deliberately held read-only. This fixture uses the
/// second, because <c>ALTER DATABASE … SET default_transaction_read_only = on</c> is per-database and so
/// cannot disturb anything else on the shared server — which a standby, or a server-wide setting,
/// would.</para>
/// <para><b>MySQL and MariaDB are deliberately not covered here.</b> Their read-only signal is
/// <c>@@read_only</c>, which is global-only — there is no per-database or per-session equivalent — so
/// making a target read-only means making the whole shared container read-only and breaking every other
/// fixture running against it. The platform-specific surface for those engines is the
/// <c>KindleStampStoreExists</c> query, which mirrors the existence check <c>ReadStamp</c> already runs
/// on every MySQL kindle, and the decision itself is engine-agnostic and unit-tested in
/// <c>ReadOnlyKindleClassificationTests</c>.</para>
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ReadOnlyTargetKindleTests
{
    private string _server = "", _user = "", _password = "", _port = "";
    private Dictionary<string, string> _props = new();
    private string _db = "";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _server = config["PostgreSQL:Server"] ?? "127.0.0.1";
        _user = config["PostgreSQL:User"];
        _password = config["PostgreSQL:Password"];
        _port = config["PostgreSQL:Port"];
        _props = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");

        // Pooling OFF for this fixture only. ALTER DATABASE ... SET default_transaction_read_only
        // applies to sessions opened afterwards, but a pooled physical connection opened BEFORE the
        // ALTER is handed straight back and still carries the old setting -- so the target reads as
        // writable and the whole fixture silently exercises the ordinary kindling path instead.
        _props["Pooling"] = "false";
    }

    private IDbConnection Open(string database)
    {
        var c = DbConnectionFactory.ForPlatform(Platform.PostgreSQL)
            .GetDbConnection(ConnectionString.Build(Platform.PostgreSQL, _server, database, _user, _password, _port, _props));
        c.Open();
        return c;
    }

    private static void Exec(IDbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    [SetUp]
    public void CreateThrowawayDatabase()
    {
        _db = $"ss_ro_{Guid.NewGuid():N}"[..30].ToLowerInvariant();
        using var maint = Open("postgres");
        Exec(maint, $"DROP DATABASE IF EXISTS \"{_db}\"");
        Exec(maint, $"CREATE DATABASE \"{_db}\"");
    }

    [TearDown]
    public void DropThrowawayDatabase()
    {
        using var maint = Open("postgres");
        try
        {
            // Read-only is a database-level setting and survives the connections that saw it, so it has
            // to come off before the drop can proceed cleanly.
            Exec(maint, $"ALTER DATABASE \"{_db}\" RESET default_transaction_read_only");
            Exec(maint, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_db}' AND pid <> pg_backend_pid()");
            Exec(maint, $"DROP DATABASE IF EXISTS \"{_db}\"");
        }
        catch
        {
            // Teardown of a throwaway database must never mask an assertion that already ran.
        }
    }

    private void Kindle()
    {
        using var c = Open(_db);
        using var cmd = c.CreateCommand();
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);
    }

    private void MakeReadOnly()
    {
        using var maint = Open("postgres");
        // Takes effect for sessions opened afterwards, which is exactly what the test then does.
        Exec(maint, $"ALTER DATABASE \"{_db}\" SET default_transaction_read_only = on");
    }

    private void OnTheDatabase(Action<IDbCommand> act)
    {
        using var c = Open(_db);
        using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 300;
        act(cmd);
    }

    [Test]
    public void AReadOnlyTarget_IsDetected_AndItsStampStoreIsFound()
    {
        Kindle();
        MakeReadOnly();

        OnTheDatabase(cmd =>
        {
            Assert.That(ReadOnlyTargetDetector.IsReadOnly(cmd, Platform.PostgreSQL), Is.True,
                "the premise: if PostgreSQL does not report this database as read-only, everything below "
                + "is exercising the ordinary kindling path and proving nothing");

            Assert.That(ForgeKindler.KindleStampStoreExists(cmd, Platform.PostgreSQL), Is.True,
                "the PostgreSQL-specific half of this feature is this catalog query -- get it wrong and a "
                + "perfectly good replica is reported as never kindled, which is a hard error");
        });
    }

    [Test]
    public void AReadOnlyTarget_WithCurrentHelpers_ProceedsWithoutWriting()
    {
        Kindle();
        MakeReadOnly();

        OnTheDatabase(cmd =>
            Assert.DoesNotThrow(
                () => ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL, allowReadOnlyTarget: true),
                "extraction from a hot standby is the point; any write attempt here is rejected by the "
                + "server, so a pass proves kindling really was skipped rather than merely succeeding"));
    }

    [Test]
    public void AReadOnlyTarget_WithNoHelpers_FailsHard()
    {
        // Deliberately NOT kindled.
        MakeReadOnly();

        var ex = Assert.Catch(() => OnTheDatabase(cmd =>
            ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL, allowReadOnlyTarget: true)));

        Assert.That(ex, Is.Not.Null,
            "there is nothing to extract with, and PostgreSQL validates the missing relation at parse "
            + "time, so proceeding would fail later with a far less useful message");
        Assert.That(ex.Message, Does.Contain("primary").IgnoreCase,
            "the message has to name the remedy, not just the problem");
    }
}
