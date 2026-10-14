// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
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
        ["Procedures"] = "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nIF OBJECT_ID(N'[dbo].[p_unchanged]') IS NULL EXEC('CREATE PROCEDURE [dbo].[p_unchanged] AS RETURN')\r\nGO\r\n\r\nALTER PROCEDURE [dbo].[p_unchanged] AS SELECT 1\r\n\r\nGO\r\n",
        ["Triggers"] = "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nCREATE OR ALTER TRIGGER [dbo].[tr_unchanged] ON [dbo].[unchanged_t] AFTER INSERT AS SELECT 1\r\n\r\nGO\r\n"
    };

    protected override string AlterViewByHand => "ALTER VIEW [dbo].[v_unchanged] AS SELECT x, w FROM dbo.unchanged_t";
    protected override string ChangedFunctionScript => "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nCREATE OR ALTER FUNCTION [dbo].[f_unchanged](@a INT) RETURNS INT AS BEGIN RETURN @a + 2 END\r\n\r\nGO\r\n";
    protected override string[] DropStatements =>
    [
        "DROP TRIGGER IF EXISTS [dbo].[tr_unchanged]", "DROP VIEW IF EXISTS [dbo].[v_unchanged]", "DROP FUNCTION IF EXISTS [dbo].[f_unchanged]",
        "DROP PROCEDURE IF EXISTS [dbo].[p_unchanged]", "DROP TABLE IF EXISTS [dbo].[unchanged_t]"
    ];
}
