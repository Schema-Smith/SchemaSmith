// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class ParallelFanOutTests : ParallelFanOutSharedTests
{
    protected override string ProductPlatform => "PostgreSQL";

    protected override IDbConnection OpenServer()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(ConnectionString.Build(Platform.PostgreSQL,
            config["Target:Server"], "postgres", config["Target:User"], config["Target:Password"], config["Target:Port"],
            ConnectionString.ReadProperties(config, "Target:ConnectionProperties")));
        conn.Open();
        return conn;
    }

    protected override string CreateDatabaseSql(string db) => $"CREATE DATABASE \"{db}\"";

    protected override void DropDatabase(IDbCommand cmd, string db)
    {
        cmd.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{db}' AND pid <> pg_backend_pid()";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{db}\"";
        cmd.ExecuteNonQuery();
    }

    protected override string IdentificationScript(string prefix) =>
        $"SELECT datname FROM pg_database WHERE datname LIKE '{prefix}\\_db%' ORDER BY datname";
    protected override string CurrentDatabaseQuery => "SELECT current_database()";
    protected override string ProbeFileName => "public.fanout_probe.sql";
    protected override string ProbeScript => "CREATE OR REPLACE FUNCTION public.fanout_probe() RETURNS text LANGUAGE sql AS $$ SELECT '{{Who}}'::text $$;";
    protected override string ProbeValueSql => "SELECT public.fanout_probe()";
    protected override string AfterScript => "CREATE TABLE IF NOT EXISTS public.after_marker (id INT NULL);";
    protected override string AfterMarkerExistsSql => "SELECT COUNT(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relname = 'after_marker'";
}
