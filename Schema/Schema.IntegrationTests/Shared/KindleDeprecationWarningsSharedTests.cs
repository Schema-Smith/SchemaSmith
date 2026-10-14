// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using MySqlConnector;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// Kindling must not use syntax the engine has deprecated. MySQL resolves a stored routine's body when it is created,
/// so a deprecated form is reported then (warning 1287) and becomes a kindle failure in the release that removes it.
/// <para>Uses its own database: kindling into the shared one again would race the tests using it.</para>
/// </summary>
public abstract class KindleDeprecationWarningsSharedTests
{
    private const int DeprecatedSyntax = 1287;

    protected abstract Platform Platform { get; }
    protected abstract string ServerConnectionString { get; }

    [Test]
    public void Kindling_RaisesNoDeprecationWarnings()
    {
        var db = "kindlewarn_" + Guid.NewGuid().ToString("N")[..12];
        using var conn = (MySqlConnection)DbConnectionFactory.ForPlatform(Platform)
            .GetDbConnection(ServerConnectionString + "Database=information_schema;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var deprecations = new List<string>();
        conn.InfoMessage += (_, e) => deprecations.AddRange(
            e.Errors.Where(w => (int)w.ErrorCode == DeprecatedSyntax).Select(w => w.Message));

        try
        {
            cmd.CommandText = $"CREATE DATABASE `{db}`;";
            cmd.ExecuteNonQuery();
            conn.ChangeDatabase(db);

            ForgeKindler.KindleTheForge(cmd, Platform, forceReKindle: true);

            Assert.That(deprecations.Distinct(), Is.Empty,
                "kindling used syntax this server reports as deprecated");
        }
        finally
        {
            try
            {
                conn.ChangeDatabase("information_schema");
                cmd.CommandText = $"DROP DATABASE IF EXISTS `{db}`;";
                cmd.ExecuteNonQuery();
            }
            catch (DbException) { /* best-effort cleanup */ }
            catch (InvalidOperationException) { /* connection already unusable */ }
        }
    }
}
