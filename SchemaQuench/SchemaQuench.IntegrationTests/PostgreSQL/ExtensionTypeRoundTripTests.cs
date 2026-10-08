// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// An extension type's modifier lives only in atttypmod, which information_schema does not expose: geometry(Point,4326)
// extracted as bare geometry, so a redeploy created an unconstrained column. The round trip runs in a database of its
// own, because the extension is installed per database and the shared fixture database should not carry it.
[Category("PostgreSQL")]
public class ExtensionTypeRoundTripTests : BaseTableQuenchTests
{
    [Test]
    public void ExtensionColumns_RedeployWithTheirTypeModifiers()
    {
        var db = $"ss_ext_{Guid.NewGuid():N}"[..24];
        bool withPostGis, withVector;
        using var server = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        server.Open();
        using (var probe = server.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pg_available_extensions WHERE name = 'postgis'";
            withPostGis = Convert.ToInt32(probe.ExecuteScalar()) > 0;
            probe.CommandText = "SELECT COUNT(*) FROM pg_available_extensions WHERE name = 'vector'";
            withVector = Convert.ToInt32(probe.ExecuteScalar()) > 0;
            if (!withPostGis && !withVector) Assert.Ignore("Neither PostGIS nor pgvector is available on this server.");
            probe.CommandText = $"CREATE DATABASE \"{db}\"";
            probe.ExecuteNonQuery();
        }

        var messages = new List<string>();
        try
        {
            using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
            conn.Open();
            conn.ChangeDatabase(db);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            // pgvector, where the server has it, carries its dimension the same way: vector(3).
            cmd.CommandText = (withPostGis ? "CREATE EXTENSION postgis;" : "") + (withVector ? "CREATE EXTENSION vector;" : "");
            cmd.ExecuteNonQuery();
            ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

            cmd.CommandText = "CREATE TABLE public.places (id integer NOT NULL PRIMARY KEY"
                              + (withPostGis ? ", pt geometry(Point, 4326), route geography(LineString, 4326), anything geometry" : "")
                              + (withVector ? ", embedding vector(3)" : "") + ")";
            cmd.ExecuteNonQuery();
            var before = Types(cmd);

            cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', 'places')";
            var extracted = (string)cmd.ExecuteScalar()!;
            cmd.CommandText = "DROP TABLE public.places";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, extracted);
            Assert.That(Types(cmd), Is.EquivalentTo(before),
                "each column must come back with its type modifier. Extracted: " + extracted);

            messages.Clear();
            RunTableQuenchProc(cmd, extracted);
            Assert.That(messages.Where(m => m.Contains("Modified columns found")), Is.Empty,
                "a second deploy must change nothing. Notices: " + string.Join(" | ", messages));
        }
        finally
        {
            // Pooled connections keep a session open in the database; end them before dropping it (WITH (FORCE) is 13+).
            NpgsqlConnection.ClearAllPools();
            using var drop = server.CreateCommand();
            drop.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{db}' AND pid <> pg_backend_pid()";
            drop.ExecuteNonQuery();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{db}\"";
            drop.ExecuteNonQuery();
        }
    }

    private static List<string> Types(System.Data.IDbCommand cmd)
    {
        cmd.CommandText = "SELECT attname || ' ' || format_type(atttypid, atttypmod) FROM pg_attribute "
                          + "WHERE attrelid = 'public.places'::regclass AND attnum > 0 AND NOT attisdropped";
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
}
