// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class UnchangedModuleSkipTests : UnchangedModuleSkipSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";

    protected override string TableJson => """
        { "Name": "[unchanged_t]", "Schema": "[dbo]", "Columns": [ { "Name": "[x]", "DataType": "INT", "Nullable": false }, { "Name": "[w]", "DataType": "INT", "Nullable": true } ],
          "Indexes": [ { "Name": "[PK_unchanged_t]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[x]" } ] }
        """;

    protected override Dictionary<string, string> Scripts => new()
    {
        ["Views"] = "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nCREATE OR ALTER VIEW [dbo].[v_unchanged] AS SELECT x FROM dbo.unchanged_t\r\n\r\nGO\r\n",
        ["Functions"] = "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nCREATE OR ALTER FUNCTION [dbo].[f_unchanged](@a INT) RETURNS INT AS BEGIN RETURN @a + 1 END\r\n\r\nGO\r\n",
        // The legacy form SchemaTongs writes under ModuleSyntax: Legacy is stamped like any other module.
        ["Procedures"] = "-- Call it from an agent job, e.g. CREATE VIEW dbo.v_doc AS SELECT 1\r\nSET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nIF OBJECT_ID(N'[dbo].[p_unchanged]') IS NULL EXEC('CREATE PROCEDURE [dbo].[p_unchanged] AS RETURN')\r\nGO\r\n\r\nALTER PROCEDURE [dbo].[p_unchanged] AS SELECT 1\r\n\r\nGO\r\n",
        ["Triggers"] = "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nCREATE OR ALTER TRIGGER [dbo].[tr_unchanged] ON [dbo].[unchanged_t] AFTER INSERT AS SELECT 1\r\n\r\nGO\r\n"
    };

    protected override string AlterViewByHand => "ALTER VIEW [dbo].[v_unchanged] AS SELECT x, w FROM dbo.unchanged_t";
    protected override string ChangedFunctionScript => "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nCREATE OR ALTER FUNCTION [dbo].[f_unchanged](@a INT) RETURNS INT AS BEGIN RETURN @a + 2 END\r\n\r\nGO\r\n";
    protected override string[] DropStatements =>
    [
        "DROP TRIGGER IF EXISTS [dbo].[tr_unchanged]", "DROP VIEW IF EXISTS [dbo].[v_unchanged]", "DROP FUNCTION IF EXISTS [dbo].[f_unchanged]",
        "DROP PROCEDURE IF EXISTS [dbo].[p_unchanged]", "DROP TABLE IF EXISTS [dbo].[unchanged_t]"
    ];
    // A trigger's enabled state and first/last order are not in its definition; both are part of what the stamp compares,
    // so a change made on the server by hand brings the script back.
    [Test]
    public void ATriggerDisabledOrReorderedOnTheServer_IsReapplied()
    {
        Deploy();
        var second = Deploy().Outcomes;
        Assert.That(second["Triggers"], Is.EqualTo(ScriptOutcome.Unchanged), Describe(second));

        Exec("DISABLE TRIGGER [dbo].[tr_unchanged] ON [dbo].[unchanged_t]");
        var afterDisable = Deploy().Outcomes;
        Assert.That(afterDisable["Triggers"], Is.EqualTo(ScriptOutcome.Applied), "a trigger disabled by hand must be re-applied: " + Describe(afterDisable));

        Deploy();
        Exec("EXEC sp_settriggerorder @triggername = N'[dbo].[tr_unchanged]', @order = N'First', @stmttype = N'INSERT'");
        var afterReorder = Deploy().Outcomes;
        Assert.That(afterReorder["Triggers"], Is.EqualTo(ScriptOutcome.Applied), "a trigger reordered by hand must be re-applied: " + Describe(afterReorder));
    }

    private static string Describe(Dictionary<string, ScriptOutcome> outcomes) => string.Join(", ", outcomes.Select(kv => $"{kv.Key}={kv.Value}"));

}
