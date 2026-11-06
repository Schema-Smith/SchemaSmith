// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.MariaDb;

// B16: the MariaDB visibility snapshot grouped table names case-insensitively, so on a case-sensitive server two tables
// whose names differ only in case shared one row, and one table's index visibility was compared against the other's.
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class SnapshotIndexVisibilityTests
{
    [Test]
    public void TablesDifferingOnlyInCase_EachKeepTheirOwnVisibility()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        object Scalar(string sql) { cmd.CommandText = sql; return cmd.ExecuteScalar(); }
        void Exec(string sql) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

        if (Convert.ToInt32(Scalar("SELECT @@lower_case_table_names")) != 0) Assert.Ignore("needs a case-sensitive server (lower_case_table_names=0)");
        if (Convert.ToInt32(Scalar("SELECT SchemaSmith_ServerVersionNum()")) < 1006) Assert.Ignore("ignored indexes arrive in MariaDB 10.6");

        var upper = $"VisCase_{Guid.NewGuid().ToString("N")[..6]}";
        var lower = upper.ToLowerInvariant();
        try
        {
            Exec($"CREATE TABLE `{upper}` (id INT PRIMARY KEY, v INT, KEY ix_v (v))");
            Exec($"CREATE TABLE `{lower}` (id INT PRIMARY KEY, v INT, KEY ix_v (v) IGNORED)");

            Exec($"CALL SchemaSmith_SnapshotIndexVisibility('{FixtureSetup.MainDb}')");
            cmd.CommandText = $"SELECT TableName, IsVisible FROM _SchemaSmith_ExistingIndexVisibility WHERE IndexName = 'ix_v' AND LOWER(TableName) = '{lower}' ORDER BY TableName";
            var rows = new List<string>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) rows.Add($"{reader.GetString(0)}:{reader.GetValue(1)}");

            Assert.That(rows, Is.EqualTo(new[] { $"{upper}:1", $"{lower}:0" }));
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS `{upper}`");
            Exec($"DROP TABLE IF EXISTS `{lower}`");
        }
    }
}
