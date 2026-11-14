// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using NUnit.Framework;
using Schema.Domain;
using SchemaTongs.IntegrationTests.Shared;

namespace SchemaTongs.IntegrationTests.SqlServer;

[Category("SqlServer")]
[TestFixture]
[Category("Integration")]
public class SettingsExtractionTests : SettingsExtractionSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string ConfigPrefix => "SqlServer";
    protected override string AdminDatabase => "master";
    protected override (string Kind, string Name, string Value) ChangedSetting => ("Option", "AUTO_UPDATE_STATISTICS_ASYNC", "ON");
    protected override (string Kind, string Name) DefaultSetting => ("Option", "AUTO_CLOSE");
    protected override (string Kind, string Name) MachineIdentitySetting => ("Configuration", "show advanced options");

    protected override void CreateSourceDatabase(IDbCommand cmd, string name)
    {
        cmd.CommandText = $"CREATE DATABASE [{name}]";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"ALTER DATABASE [{name}] SET AUTO_UPDATE_STATISTICS_ASYNC ON";
        cmd.ExecuteNonQuery();
    }

    protected override void DropSourceDatabase(IDbCommand cmd, string name)
    {
        Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, name);
    }
}
