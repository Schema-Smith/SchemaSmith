// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using NUnit.Framework;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;

namespace SchemaTongs.UnitTests;

[TestFixture]
public class HelperMethodTests
{
    #region FormatBaseType delimiter-escaping (regression)

    [Test]
    public void FormatBaseType_TypeNameWithBracket_EscapesTheDelimiter()
    {
        // A user-defined type whose name contains the SQL Server delimiter would otherwise
        // generate broken DDL (e.g. [weird]type]). It must be doubled to [weird]]type].
        Assert.That(SchemaTongs.FormatBaseType("weird]type", -1, 0, 0), Is.EqualTo("[weird]]type]"));
    }

    [Test]
    public void FormatBaseType_SystemType_Unaffected()
    {
        Assert.That(SchemaTongs.FormatBaseType("int", -1, 0, 0), Is.EqualTo("[int]"));
    }

    #endregion

    #region EscapeSql Tests

    [Test]
    public void EscapeSql_NoQuotes_ReturnsUnchanged()
    {
        Assert.That(SchemaTongs.EscapeSql("hello"), Is.EqualTo("hello"));
    }

    [Test]
    public void EscapeSql_WithSingleQuote_DoublesIt()
    {
        Assert.That(SchemaTongs.EscapeSql("it's"), Is.EqualTo("it''s"));
    }

    [Test]
    public void EscapeSql_MultipleSingleQuotes_DoublesAll()
    {
        Assert.That(SchemaTongs.EscapeSql("it's a 'test'"), Is.EqualTo("it''s a ''test''"));
    }

    [Test]
    public void EscapeSql_EmptyString_ReturnsEmpty()
    {
        Assert.That(SchemaTongs.EscapeSql(""), Is.EqualTo(""));
    }

    #endregion

    #region FormatBaseType Tests

    [Test]
    public void FormatBaseType_Nvarchar_WithMaxLength_FormatsCorrectly()
    {
        // nvarchar stores 2 bytes per char, so max_length 100 = 50 chars
        Assert.That(SchemaTongs.FormatBaseType("nvarchar", 100, 0, 0), Is.EqualTo("[nvarchar](50)"));
    }

    [Test]
    public void FormatBaseType_Nvarchar_Max_FormatsCorrectly()
    {
        Assert.That(SchemaTongs.FormatBaseType("nvarchar", -1, 0, 0), Is.EqualTo("[nvarchar](max)"));
    }

    [Test]
    public void FormatBaseType_Varchar_WithLength_FormatsCorrectly()
    {
        Assert.That(SchemaTongs.FormatBaseType("varchar", 50, 0, 0), Is.EqualTo("[varchar](50)"));
    }

    [Test]
    public void FormatBaseType_Varchar_Max_FormatsCorrectly()
    {
        Assert.That(SchemaTongs.FormatBaseType("varchar", -1, 0, 0), Is.EqualTo("[varchar](max)"));
    }

    [Test]
    public void FormatBaseType_Varbinary_Max_FormatsCorrectly()
    {
        Assert.That(SchemaTongs.FormatBaseType("varbinary", -1, 0, 0), Is.EqualTo("[varbinary](max)"));
    }

    [Test]
    public void FormatBaseType_Decimal_FormatsWithPrecisionAndScale()
    {
        Assert.That(SchemaTongs.FormatBaseType("decimal", 0, 18, 4), Is.EqualTo("[decimal](18, 4)"));
    }

    [Test]
    public void FormatBaseType_Numeric_FormatsWithPrecisionAndScale()
    {
        Assert.That(SchemaTongs.FormatBaseType("numeric", 0, 10, 2), Is.EqualTo("[numeric](10, 2)"));
    }

    [Test]
    public void FormatBaseType_Datetime2_DefaultScale_NoParens()
    {
        Assert.That(SchemaTongs.FormatBaseType("datetime2", 0, 0, 7), Is.EqualTo("[datetime2]"));
    }

    [Test]
    public void FormatBaseType_Datetime2_CustomScale_WithParens()
    {
        Assert.That(SchemaTongs.FormatBaseType("datetime2", 0, 0, 3), Is.EqualTo("[datetime2](3)"));
    }

    [Test]
    public void FormatBaseType_Int_FormatsSimply()
    {
        Assert.That(SchemaTongs.FormatBaseType("int", 4, 10, 0), Is.EqualTo("[int]"));
    }

    [Test]
    public void FormatBaseType_Bit_FormatsSimply()
    {
        Assert.That(SchemaTongs.FormatBaseType("bit", 1, 1, 0), Is.EqualTo("[bit]"));
    }

    [Test]
    public void FormatBaseType_Nchar_WithLength_FormatsCorrectly()
    {
        Assert.That(SchemaTongs.FormatBaseType("nchar", 20, 0, 0), Is.EqualTo("[nchar](10)"));
    }

    [Test]
    public void FormatBaseType_Char_WithLength_FormatsCorrectly()
    {
        Assert.That(SchemaTongs.FormatBaseType("char", 10, 0, 0), Is.EqualTo("[char](10)"));
    }

    [Test]
    public void FormatBaseType_Binary_WithLength_FormatsCorrectly()
    {
        Assert.That(SchemaTongs.FormatBaseType("binary", 16, 0, 0), Is.EqualTo("[binary](16)"));
    }

    [Test]
    public void FormatBaseType_Time_DefaultScale_NoParens()
    {
        Assert.That(SchemaTongs.FormatBaseType("time", 0, 0, 7), Is.EqualTo("[time]"));
    }

    [Test]
    public void FormatBaseType_Time_CustomScale_WithParens()
    {
        Assert.That(SchemaTongs.FormatBaseType("time", 0, 0, 0), Is.EqualTo("[time](0)"));
    }

    [Test]
    public void FormatBaseType_DatetimeOffset_DefaultScale_NoParens()
    {
        Assert.That(SchemaTongs.FormatBaseType("datetimeoffset", 0, 0, 7), Is.EqualTo("[datetimeoffset]"));
    }

    [Test]
    public void FormatBaseType_DatetimeOffset_CustomScale_WithParens()
    {
        Assert.That(SchemaTongs.FormatBaseType("datetimeoffset", 0, 0, 2), Is.EqualTo("[datetimeoffset](2)"));
    }

    #endregion

    #region ConvertToCreateOrAlter Tests

    // sp_rename changes the catalog name but not the stored text, so the text's own name is what tells them apart.
    [Test]
    public void DefinedObjectName_ReadsTheNameTheStoredTextCreates()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SchemaTongs.DefinedObjectName("CREATE VIEW dbo.vOld AS SELECT 1 AS x"), Is.EqualTo("vOld"));
            Assert.That(SchemaTongs.DefinedObjectName("CREATE   PROC [dbo].[My Proc]\r\nAS BEGIN END"), Is.EqualTo("My Proc"));
            Assert.That(SchemaTongs.DefinedObjectName("CREATE FUNCTION \"dbo\".\"f\"() RETURNS INT AS BEGIN RETURN 1 END"), Is.EqualTo("f"));
            Assert.That(SchemaTongs.DefinedObjectName("CREATE TRIGGER trg ON dbo.t AFTER INSERT AS SELECT 1"), Is.EqualTo("trg"));
            Assert.That(SchemaTongs.DefinedObjectName("-- nothing here"), Is.Null);
        });
    }

    [Test]
    public void ReplaceDefinedName_WritesTheCurrentNameInPlaceOfTheStaleOne()
    {
        var result = SchemaTongs.ReplaceDefinedName("CREATE VIEW dbo.vOld AS SELECT vOld.x FROM dbo.vOld", "dbo", "vNew");
        Assert.That(result, Does.StartWith("CREATE VIEW [dbo].[vNew] AS"), "only the created name changes; the body is the user's to review");
    }

    [Test]
    public void ConvertToCreateOrAlter_ProcShorthand_ConvertsCorrectly()
    {
        var result = SchemaTongs.ConvertToCreateOrAlter("CREATE PROC dbo.MyProc AS BEGIN END", "dbo", "MyProc");
        Assert.That(result, Does.StartWith("CREATE OR ALTER PROC [dbo].[MyProc]"));
    }

    [Test]
    public void ConvertToCreateOrAlter_SimpleProcedure_ConvertsCorrectly()
    {
        var input = "CREATE PROCEDURE dbo.MyProc AS BEGIN END";
        var result = SchemaTongs.ConvertToCreateOrAlter(input, "dbo", "MyProc");
        Assert.That(result, Does.Contain("CREATE OR ALTER PROCEDURE"));
        Assert.That(result, Does.Contain("[dbo].[MyProc]"));
    }

    [Test]
    public void ConvertToCreateOrAlter_SimpleFunction_ConvertsCorrectly()
    {
        var input = "CREATE FUNCTION dbo.MyFunc() RETURNS INT AS BEGIN RETURN 1 END";
        var result = SchemaTongs.ConvertToCreateOrAlter(input, "dbo", "MyFunc");
        Assert.That(result, Does.Contain("CREATE OR ALTER FUNCTION"));
        Assert.That(result, Does.Contain("[dbo].[MyFunc]"));
    }

    [Test]
    public void ConvertToCreateOrAlter_View_ConvertsCorrectly()
    {
        var input = "CREATE VIEW dbo.MyView AS SELECT 1 AS Col";
        var result = SchemaTongs.ConvertToCreateOrAlter(input, "dbo", "MyView");
        Assert.That(result, Does.Contain("CREATE OR ALTER VIEW"));
        Assert.That(result, Does.Contain("[dbo].[MyView]"));
    }

    [Test]
    public void ConvertToCreateOrAlter_AlreadyBracketed_StillBracketed()
    {
        var input = "CREATE PROCEDURE [dbo].[MyProc] AS BEGIN END";
        var result = SchemaTongs.ConvertToCreateOrAlter(input, "dbo", "MyProc");
        Assert.That(result, Does.Contain("CREATE OR ALTER PROCEDURE"));
        Assert.That(result, Does.Contain("[dbo].[MyProc]"));
    }

    [Test]
    public void ConvertToCreateOrAlter_WithLeadingComment_PreservesComment()
    {
        var input = "-- This is a comment\r\nCREATE PROCEDURE dbo.MyProc AS BEGIN END";
        var result = SchemaTongs.ConvertToCreateOrAlter(input, "dbo", "MyProc");
        Assert.That(result, Does.StartWith("-- This is a comment"));
        Assert.That(result, Does.Contain("CREATE OR ALTER PROCEDURE"));
    }

    [Test]
    public void ConvertToCreateOrAlter_CaseInsensitive_Converts()
    {
        var input = "create procedure dbo.MyProc AS BEGIN END";
        var result = SchemaTongs.ConvertToCreateOrAlter(input, "dbo", "MyProc");
        Assert.That(result, Does.Contain("CREATE OR ALTER"));
    }

    #endregion

    #region ConvertToLegacyModuleForm Tests

    // The form every supported version accepts: a guarded stub creates the object when it is missing, then ALTER carries
    // the real body. The stub must be creatable wherever the real object is, so it mirrors the kind and shape.
    [Test]
    public void ConvertToLegacyModuleForm_View_GuardedStubThenAlter()
    {
        var result = SchemaTongs.ConvertToLegacyModuleForm("CREATE OR ALTER VIEW [dbo].[MyView] AS SELECT 1 AS Col", "dbo", "MyView");
        Assert.That(result, Is.EqualTo(
            "IF OBJECT_ID(N'[dbo].[MyView]') IS NULL EXEC('CREATE VIEW [dbo].[MyView] AS SELECT 1 AS x')\r\nGO\r\n\r\n" +
            "ALTER VIEW [dbo].[MyView] AS SELECT 1 AS Col"));
    }

    [TestCase("CREATE OR ALTER PROC [dbo].[P] AS BEGIN END", "P",
        "CREATE PROCEDURE [dbo].[P] AS RETURN", "ALTER PROC [dbo].[P] AS BEGIN END", TestName = "ConvertToLegacyModuleForm_ProcShorthand")]
    [TestCase("CREATE OR ALTER PROCEDURE [dbo].[P] @a INT AS SELECT @a", "P",
        "CREATE PROCEDURE [dbo].[P] AS RETURN", "ALTER PROCEDURE [dbo].[P] @a INT AS SELECT @a", TestName = "ConvertToLegacyModuleForm_Procedure")]
    [TestCase("CREATE OR ALTER FUNCTION [dbo].[F](@a INT) RETURNS INT AS BEGIN RETURN @a END", "F",
        "CREATE FUNCTION [dbo].[F]() RETURNS INT AS BEGIN RETURN 0 END", "ALTER FUNCTION [dbo].[F](@a INT) RETURNS INT AS BEGIN RETURN @a END", TestName = "ConvertToLegacyModuleForm_ScalarFunction")]
    [TestCase("CREATE OR ALTER FUNCTION [dbo].[F]() RETURNS TABLE AS RETURN (SELECT 1 AS x)", "F",
        "CREATE FUNCTION [dbo].[F]() RETURNS TABLE AS RETURN SELECT 1 AS x", "ALTER FUNCTION [dbo].[F]() RETURNS TABLE AS RETURN (SELECT 1 AS x)", TestName = "ConvertToLegacyModuleForm_InlineTableFunction")]
    [TestCase("CREATE OR ALTER FUNCTION [dbo].[F]() RETURNS @t TABLE (x INT) AS BEGIN INSERT @t VALUES (1) RETURN END", "F",
        "CREATE FUNCTION [dbo].[F]() RETURNS @t TABLE (x INT) AS BEGIN RETURN END", "ALTER FUNCTION [dbo].[F]() RETURNS @t TABLE (x INT) AS BEGIN INSERT @t VALUES (1) RETURN END", TestName = "ConvertToLegacyModuleForm_MultiStatementTableFunction")]
    public void ConvertToLegacyModuleForm_StubMatchesTheKindAndShape(string definition, string name, string expectedStub, string expectedAlter)
    {
        var result = SchemaTongs.ConvertToLegacyModuleForm(definition, "dbo", name);
        Assert.That(result, Is.EqualTo($"IF OBJECT_ID(N'[dbo].[{name}]') IS NULL EXEC('{expectedStub}')\r\nGO\r\n\r\n{expectedAlter}"));
    }

    // ALTER FUNCTION cannot change a function's kind, so a stub of the wrong shape would wedge the script.
    [TestCase("CREATE OR ALTER TRIGGER [dbo].[T] ON [dbo].[Orders] AFTER INSERT AS SELECT 1", "AFTER INSERT")]
    [TestCase("CREATE OR ALTER TRIGGER [dbo].[T] ON [dbo].[Orders] FOR UPDATE, DELETE AS SELECT 1", "AFTER UPDATE")]
    [TestCase("CREATE OR ALTER TRIGGER [dbo].[T] ON [dbo].[vOrders] INSTEAD OF DELETE AS SELECT 1", "INSTEAD OF DELETE")]
    public void ConvertToLegacyModuleForm_TableTrigger_StubMirrorsTheModeAndFirstEvent(string definition, string expectedStubEvent)
    {
        var result = SchemaTongs.ConvertToLegacyModuleForm(definition, "dbo", "T", definition.Contains("vOrders") ? "vOrders" : "Orders");
        var table = definition.Contains("vOrders") ? "vOrders" : "Orders";
        Assert.That(result, Is.EqualTo(
            $"IF OBJECT_ID(N'[dbo].[T]') IS NULL EXEC('CREATE TRIGGER [dbo].[T] ON [dbo].[{table}] {expectedStubEvent} AS RETURN')\r\nGO\r\n\r\n" +
            definition.Replace("CREATE OR ALTER TRIGGER", "ALTER TRIGGER")));
    }

    [Test]
    public void ConvertToLegacyModuleForm_DatabaseTrigger_GuardsOnSysTriggers()
    {
        var result = SchemaTongs.ConvertToLegacyModuleForm("CREATE OR ALTER TRIGGER [Audit] ON DATABASE FOR CREATE_TABLE AS SELECT 1", null, "Audit");
        Assert.That(result, Is.EqualTo(
            "IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 0 AND name = N'Audit') EXEC('CREATE TRIGGER [Audit] ON DATABASE FOR DDL_DATABASE_LEVEL_EVENTS AS RETURN')\r\nGO\r\n\r\n" +
            "ALTER TRIGGER [Audit] ON DATABASE FOR CREATE_TABLE AS SELECT 1"));
    }

    [Test]
    public void ConvertToLegacyModuleForm_KeepsALeadingComment_WithTheAlter()
    {
        var result = SchemaTongs.ConvertToLegacyModuleForm("-- why\r\nCREATE PROCEDURE [dbo].[P] AS RETURN 1", "dbo", "P");
        Assert.That(result, Does.EndWith("GO\r\n\r\n-- why\r\nALTER PROCEDURE [dbo].[P] AS RETURN 1"));
    }

    [Test]
    public void ConvertToLegacyModuleForm_EscapesAQuoteInTheNameInsideTheExecString()
    {
        var result = SchemaTongs.ConvertToLegacyModuleForm("CREATE OR ALTER VIEW [dbo].[O'Brien] AS SELECT 1 AS x", "dbo", "O'Brien");
        Assert.That(result, Does.StartWith("IF OBJECT_ID(N'[dbo].[O''Brien]') IS NULL EXEC('CREATE VIEW [dbo].[O''Brien] AS SELECT 1 AS x')"));
    }

    [Test]
    public void ConvertToLegacyModuleForm_NoModuleHeader_IsUnchanged()
    {
        Assert.That(SchemaTongs.ConvertToLegacyModuleForm("-- nothing here", "dbo", "X"), Is.EqualTo("-- nothing here"));
    }

    #endregion

    #region FormatXmlInScript Tests

    [Test]
    public void FormatXmlInScript_NoXmlContent_ReturnsUnchanged()
    {
        var input = "SELECT 1";
        Assert.That(SchemaTongs.FormatXmlInScript(input), Is.EqualTo("SELECT 1"));
    }

    [Test]
    public void FormatXmlInScript_WithAsNPrefix_AttemptsParse()
    {
        // This just verifies the method doesn't throw for XML content
        var input = "CREATE XML SCHEMA COLLECTION [dbo].[TestSchema] AS N'<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"></xsd:schema>'";
        var result = SchemaTongs.FormatXmlInScript(input);
        Assert.That(result, Does.Contain("xsd:schema"));
    }

    #endregion

    #region PromoteCheckConstraintsToTableLevel Tests

    [Test]
    public void PromoteCheckConstraintsToTableLevel_ClearsColumnCheckExpressions()
    {
        var table = new SqlServerTable
        {
            Name = "Orders",
            Columns =
            [
                new SqlServerColumn { Name = "Quantity", CheckExpression = "[Quantity] > 0" },
                new SqlServerColumn { Name = "Price", CheckExpression = "[Price] >= 0" },
                new SqlServerColumn { Name = "Name" }
            ]
        };

        var allConstraints = new List<CheckConstraint>
        {
            new() { Name = "CK_Orders_Quantity", Expression = "[Quantity] > 0" },
            new() { Name = "CK_Orders_Price", Expression = "[Price] >= 0" }
        };

        SchemaTongs.PromoteCheckConstraintsToTableLevel(table, allConstraints);

        foreach (var col in table.Columns)
        {
            var sqlCol = (SqlServerColumn)col;
            Assert.That(sqlCol.CheckExpression, Is.Null, $"Column {col.Name} should have null CheckExpression");
        }
    }

    [Test]
    public void PromoteCheckConstraintsToTableLevel_ReplacesCheckConstraintsWithFullSet()
    {
        var table = new SqlServerTable
        {
            Name = "Orders",
            Columns =
            [
                new SqlServerColumn { Name = "Quantity", CheckExpression = "[Quantity] > 0" }
            ],
            CheckConstraints =
            [
                new CheckConstraint { Name = "CK_Orders_Status", Expression = "[Status] IN ('A','B')" }
            ]
        };

        var allConstraints = new List<CheckConstraint>
        {
            new() { Name = "CK_Orders_Quantity", Expression = "[Quantity] > 0" },
            new() { Name = "CK_Orders_Status", Expression = "[Status] IN ('A','B')" }
        };

        SchemaTongs.PromoteCheckConstraintsToTableLevel(table, allConstraints);

        Assert.That(table.CheckConstraints, Has.Count.EqualTo(2));
        Assert.That(table.CheckConstraints[0].Name, Is.EqualTo("CK_Orders_Quantity"));
        Assert.That(table.CheckConstraints[0].Expression, Is.EqualTo("[Quantity] > 0"));
        Assert.That(table.CheckConstraints[1].Name, Is.EqualTo("CK_Orders_Status"));
        Assert.That(table.CheckConstraints[1].Expression, Is.EqualTo("[Status] IN ('A','B')"));
    }

    [Test]
    public void PromoteCheckConstraintsToTableLevel_NoColumnChecks_StillReplacesTableConstraints()
    {
        var table = new SqlServerTable
        {
            Name = "Orders",
            Columns = [new SqlServerColumn { Name = "Id" }],
            CheckConstraints =
            [
                new CheckConstraint { Name = "CK_Old", Expression = "1=1" }
            ]
        };

        var allConstraints = new List<CheckConstraint>
        {
            new() { Name = "CK_TableLevel", Expression = "[Status] <> 'X'" }
        };

        SchemaTongs.PromoteCheckConstraintsToTableLevel(table, allConstraints);

        Assert.That(table.CheckConstraints, Has.Count.EqualTo(1));
        Assert.That(table.CheckConstraints[0].Name, Is.EqualTo("CK_TableLevel"));
    }

    [Test]
    public void PromoteCheckConstraintsToTableLevel_EmptyConstraintList_ClearsAll()
    {
        var table = new SqlServerTable
        {
            Name = "Orders",
            Columns = [new SqlServerColumn { Name = "Qty", CheckExpression = "[Qty] > 0" }],
            CheckConstraints =
            [
                new CheckConstraint { Name = "CK_Old", Expression = "1=1" }
            ]
        };

        SchemaTongs.PromoteCheckConstraintsToTableLevel(table, []);

        Assert.That(((SqlServerColumn)table.Columns[0]).CheckExpression, Is.Null);
        Assert.That(table.CheckConstraints, Is.Empty);
    }

    #endregion

    #region DemoteSingleColumnChecksToColumnLevel Tests (PostgreSQL)

    private static PostgreSqlTable OrdersWithChecks(params (string Name, string Expression)[] constraints)
    {
        var table = new PostgreSqlTable
        {
            Name = "Orders",
            Columns =
            [
                new PostgreSqlColumn { Name = "Status" },
                new PostgreSqlColumn { Name = "Quantity" }
            ]
        };
        foreach (var (name, expression) in constraints)
            table.CheckConstraints.Add(new CheckConstraint { Name = name, Expression = expression });
        return table;
    }

    [Test]
    public void DemoteSingleColumnChecks_GeneratedName_MovesOntoColumn()
    {
        var table = OrdersWithChecks(("CK_Orders_Status", "\"Status\" >= 0"));

        SchemaTongs.DemoteSingleColumnChecksToColumnLevel(table,
            new Dictionary<string, string> { ["CK_Orders_Status"] = "Status" });

        Assert.That(((PostgreSqlColumn)table.Columns[0]).CheckExpression, Is.EqualTo("\"Status\" >= 0"));
        Assert.That(table.CheckConstraints, Is.Empty, "the constraint moved onto the column, so it must not remain table-level");
    }

    [Test]
    public void DemoteSingleColumnChecks_UserNamedConstraint_StaysTableLevel()
    {
        // PostgreSQL records no marker for how a constraint was declared, so referencing one column
        // is not evidence it was authored column-level. Demoting a user-named constraint would
        // rename it to the generated form on the next apply — a drop/recreate on every deploy.
        var table = OrdersWithChecks(("chk_status_positive", "\"Status\" >= 0"));

        SchemaTongs.DemoteSingleColumnChecksToColumnLevel(table,
            new Dictionary<string, string> { ["chk_status_positive"] = "Status" });

        Assert.That(((PostgreSqlColumn)table.Columns[0]).CheckExpression, Is.Null.Or.Empty);
        Assert.That(table.CheckConstraints, Has.Count.EqualTo(1));
        Assert.That(table.CheckConstraints[0].Name, Is.EqualTo("chk_status_positive"), "the author's name must survive the round-trip");
    }

    [Test]
    public void DemoteSingleColumnChecks_MultiColumnCheck_IsNotOffered_AndStaysTableLevel()
    {
        // conkey length > 1 is filtered out by the query, so a multi-column check never appears in
        // the map — it has no single column to belong to.
        var table = OrdersWithChecks(("CK_Orders_Span", "\"Status\" >= 0 AND \"Quantity\" > 0"));

        SchemaTongs.DemoteSingleColumnChecksToColumnLevel(table, new Dictionary<string, string>());

        Assert.That(table.CheckConstraints, Has.Count.EqualTo(1));
    }

    [Test]
    public void DemoteSingleColumnChecks_UnknownColumnOrConstraint_IsIgnored()
    {
        var table = OrdersWithChecks(("CK_Orders_Status", "\"Status\" >= 0"));

        Assert.DoesNotThrow(() => SchemaTongs.DemoteSingleColumnChecksToColumnLevel(table,
            new Dictionary<string, string> { ["CK_Orders_Missing"] = "Missing" }));
        Assert.That(table.CheckConstraints, Has.Count.EqualTo(1));
    }

    [Test]
    public void DemoteSingleColumnChecks_EmptyOrNullMap_IsNoOp()
    {
        var table = OrdersWithChecks(("CK_Orders_Status", "\"Status\" >= 0"));

        SchemaTongs.DemoteSingleColumnChecksToColumnLevel(table, null);
        SchemaTongs.DemoteSingleColumnChecksToColumnLevel(table, new Dictionary<string, string>());

        Assert.That(table.CheckConstraints, Has.Count.EqualTo(1));
    }

    [Test]
    public void DemoteSingleColumnChecks_QuotedIdentifiers_AreMatchedUnwrapped()
    {
        var table = new PostgreSqlTable
        {
            Name = "\"Orders\"",
            Columns = [new PostgreSqlColumn { Name = "\"Status\"" }]
        };
        table.CheckConstraints.Add(new CheckConstraint { Name = "CK_Orders_Status", Expression = "\"Status\" >= 0" });

        SchemaTongs.DemoteSingleColumnChecksToColumnLevel(table,
            new Dictionary<string, string> { ["CK_Orders_Status"] = "Status" });

        Assert.That(((PostgreSqlColumn)table.Columns[0]).CheckExpression, Is.EqualTo("\"Status\" >= 0"));
        Assert.That(table.CheckConstraints, Is.Empty);
    }

    #endregion

    #region StripOuterParentheses (security policy predicates)

    [TestCase("([sec].[fn]([Col]))", "[sec].[fn]([Col])")]
    [TestCase("[sec].[fn]([Col])", "[sec].[fn]([Col])")]
    [TestCase("((x))", "(x)")]
    [TestCase("(a) AND (b)", "(a) AND (b)")]
    [TestCase(" (x) ", "x")]
    public void StripOuterParentheses_RemovesOnlyAPairThatWrapsTheWhole(string definition, string expected)
    {
        Assert.That(SchemaTongs.StripOuterParentheses(definition), Is.EqualTo(expected));
    }

    #endregion
}
