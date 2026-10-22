// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// VerboseLogging tells SchemaSmith's own notices from a user script's by the notice context. This pins that the driver
// hands that context over in the shape the decision expects, for both kinds. Everything is rolled back.
[Category("PostgreSQL")]
public class NoticeContextTests
{
    [Test]
    public void NoticeContext_NamesTheRoutine_SoSchemaSmithsAndAUsersNoticesAreToldApart()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"],
            ConnectionString.ReadProperties(config, "Target:ConnectionProperties"));
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(connectionString);
        var notices = new Dictionary<string, string>();
        conn.Notice += (_, e) => notices[e.Notice.MessageText] = e.Notice.Where;
        conn.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE SCHEMA IF NOT EXISTS "SchemaSmith";
            CREATE OR REPLACE FUNCTION "SchemaSmith"."NoticeContextProbe"() RETURNS void LANGUAGE plpgsql AS $$ BEGIN RAISE NOTICE 'own-progress'; END $$;
            SELECT "SchemaSmith"."NoticeContextProbe"();
            CREATE OR REPLACE PROCEDURE "SchemaSmith"."NoticeContextDynamicProbe"() LANGUAGE plpgsql AS $$ BEGIN EXECUTE 'DO $x$ BEGIN RAISE NOTICE ''own-dynamic''; END $x$'; END $$;
            CALL "SchemaSmith"."NoticeContextDynamicProbe"();
            DO $$ BEGIN RAISE NOTICE 'user-note'; END $$;
            """;
        cmd.ExecuteNonQuery();
        tx.Rollback();

        Assert.Multiple(() =>
        {
            Assert.That(DatabaseQuench.ShouldLogPostgreSqlNotice("own-progress", notices["own-progress"], verboseLogging: false), Is.True,
                $"SchemaSmith's notice would be hidden; context was: {notices["own-progress"]}");
            Assert.That(DatabaseQuench.ShouldLogPostgreSqlNotice("own-dynamic", notices["own-dynamic"], verboseLogging: false), Is.True,
                $"a notice SchemaSmith raises from a block it builds and EXECUTEs would be hidden; context was: {notices["own-dynamic"]}");
            Assert.That(DatabaseQuench.ShouldLogPostgreSqlNotice("user-note", notices["user-note"], verboseLogging: false), Is.False,
                $"a user's notice would log without VerboseLogging; context was: {notices["user-note"]}");
        });
    }
}
