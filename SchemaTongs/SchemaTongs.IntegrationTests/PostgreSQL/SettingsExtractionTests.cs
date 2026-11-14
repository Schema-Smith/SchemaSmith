// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using NUnit.Framework;
using Schema.Domain;
using SchemaTongs.IntegrationTests.Shared;

namespace SchemaTongs.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
public class SettingsExtractionTests : SettingsExtractionSharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string AdminDatabase => "postgres";
    protected override (string Kind, string Name, string Value) ChangedSetting => ("Parameter", "work_mem", "12MB");
    protected override (string Kind, string Name) DefaultSetting => ("Parameter", "statement_timeout");
    protected override (string Kind, string Name) MachineIdentitySetting => ("Parameter", "listen_addresses");

    protected override void CreateSourceDatabase(IDbCommand cmd, string name)
    {
        cmd.CommandText = $"CREATE DATABASE \"{name}\"";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"ALTER DATABASE \"{name}\" SET work_mem = '12MB'";
        cmd.ExecuteNonQuery();
    }

    protected override void DropSourceDatabase(IDbCommand cmd, string name)
    {
        // WITH (FORCE) is PostgreSQL 13; ending the sessions first works on every supported version.
        cmd.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{name}' AND pid <> pg_backend_pid()";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\"";
        cmd.ExecuteNonQuery();
    }
}
