// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.MySQL;

/// <summary>
/// The reserved-word helper must know every word the server it runs against reserves. Each MySQL release adds some
/// (8.0.31 added INTERSECT, 26.7 QUALIFY and TABLESAMPLE), and a list maintained by hand falls behind silently.
/// </summary>
[Category("MySQL")]
[TestFixture]
[Category("Integration")]
public class MySqlReservedWordsPinTests
{
    [Test]
    public void EveryWordTheServerReserves_IsKnownToTheHelper()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'information_schema' AND TABLE_NAME = 'KEYWORDS'";
        if (System.Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            Assert.Ignore("INFORMATION_SCHEMA.KEYWORDS arrived in MySQL 8.0; below that there is no list to pin against.");

        cmd.CommandText = "SELECT WORD FROM INFORMATION_SCHEMA.KEYWORDS WHERE RESERVED = 1";
        var reserved = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) reserved.Add(reader.GetString(0));

        Assert.That(reserved, Is.Not.Empty, "the server reported no reserved words");
        var unknown = reserved.Where(w => !MySqlReservedWords.IsReserved(w)).OrderBy(w => w).ToList();
        Assert.That(unknown, Is.Empty, "reserved by this server but missing from MySqlReservedWords: " + string.Join(", ", unknown));
    }
}
