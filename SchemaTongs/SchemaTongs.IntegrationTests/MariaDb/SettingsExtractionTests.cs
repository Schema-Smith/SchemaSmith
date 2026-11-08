// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using NUnit.Framework;
using Schema.Domain;
using SchemaTongs.IntegrationTests.Shared;

namespace SchemaTongs.IntegrationTests.MariaDb;

[Category("MariaDb")]
[TestFixture]
[Category("Integration")]
public class SettingsExtractionTests : SettingsExtractionSharedTests
{
    private string _collation = "";

    protected override Platform Platform => Platform.MariaDb;
    protected override string ConfigPrefix => "MariaDB";
    protected override string AdminDatabase => "mysql";
    protected override (string Kind, string Name, string Value) ChangedSetting => ("Option", "COLLATE", _collation);
    protected override (string Kind, string Name) DefaultSetting => ("Option", "CHARACTER SET");
    protected override (string Kind, string Name) MachineIdentitySetting => ("Variable", "port");

    // The server's own character set, with a collation of it that is not the server's default.
    protected override void CreateSourceDatabase(IDbCommand cmd, string name)
    {
        cmd.CommandText = "SELECT COLLATION_NAME FROM information_schema.COLLATIONS WHERE CHARACTER_SET_NAME = @@character_set_server " +
                          "AND COLLATION_NAME <> @@collation_server ORDER BY COLLATION_NAME LIMIT 1";
        _collation = cmd.ExecuteScalar()?.ToString();
        cmd.CommandText = $"CREATE DATABASE `{name}` CHARACTER SET @@character_set_server COLLATE {_collation}";
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (System.Data.Common.DbException)
        {
            // CHARACTER SET takes a name, not a variable, on some versions: read it first.
            cmd.CommandText = "SELECT @@character_set_server";
            var charset = cmd.ExecuteScalar()?.ToString();
            cmd.CommandText = $"CREATE DATABASE `{name}` CHARACTER SET {charset} COLLATE {_collation}";
            cmd.ExecuteNonQuery();
        }
    }

    protected override void DropSourceDatabase(IDbCommand cmd, string name)
    {
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{name}`";
        cmd.ExecuteNonQuery();
    }
}
