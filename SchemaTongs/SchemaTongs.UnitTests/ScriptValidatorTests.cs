// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Schema.Domain;

namespace SchemaTongs.UnitTests;

[TestFixture]
public class ScriptValidatorTests
{
    private ScriptValidator _validator;

    // What the driver throws: the MySQL-family validator catches the database's errors, not every exception.
    private sealed class ServerErrorException(string message) : System.Data.Common.DbException(message);

    [SetUp]
    public void SetUp()
    {
        _validator = new ScriptValidator();
    }

    #region RewriteWithTempName — SQL Server

    [Test]
    public void RewriteWithTempName_SqlServer_Function_RewritesCorrectly()
    {
        var script = "CREATE FUNCTION [dbo].[fn_MyFunc](@x INT)\nRETURNS INT\nAS\nBEGIN RETURN @x END";
        var result = _validator.RewriteWithTempName(script, Platform.SqlServer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE FUNCTION \[dbo\]\.\[_validate_[a-f0-9]+\]"));
        Assert.That(result.RewrittenScript, Does.Contain("RETURNS INT"));
        Assert.That(result.TempName, Does.Match(@"_validate_[a-f0-9]+"));
    }

    [Test]
    public void RewriteWithTempName_SqlServer_OrAlterView_RewritesCorrectly()
    {
        var script = "CREATE OR ALTER VIEW [dbo].[vw_MyView]\nAS\nSELECT 1 AS Col";
        var result = _validator.RewriteWithTempName(script, Platform.SqlServer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE OR ALTER VIEW \[dbo\]\.\[_validate_[a-f0-9]+\]"));
        Assert.That(result.RewrittenScript, Does.Contain("SELECT 1 AS Col"));
    }

    [Test]
    public void RewriteWithTempName_SqlServer_Procedure_RewritesCorrectly()
    {
        var script = "CREATE PROCEDURE [dbo].[usp_MyProc]\nAS\nSELECT 1";
        var result = _validator.RewriteWithTempName(script, Platform.SqlServer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE PROCEDURE \[dbo\]\.\[_validate_[a-f0-9]+\]"));
        Assert.That(result.RewrittenScript, Does.Contain("SELECT 1"));
    }

    [Test]
    public void RewriteWithTempName_SqlServer_NoSchema_RewritesCorrectly()
    {
        var script = "CREATE VIEW [MyView]\nAS\nSELECT 1 AS Col";
        var result = _validator.RewriteWithTempName(script, Platform.SqlServer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE VIEW \[_validate_[a-f0-9]+\]"));
        Assert.That(result.RewrittenScript, Does.Not.Contain("[MyView]"));
    }

    #endregion

    #region RewriteWithTempName — PostgreSQL

    [Test]
    public void RewriteWithTempName_PostgreSQL_OrReplaceFunction_RewritesCorrectly()
    {
        var script = "CREATE OR REPLACE FUNCTION \"public\".\"my_func\"(x integer)\nRETURNS integer\nAS $$ BEGIN RETURN x; END; $$ LANGUAGE plpgsql;";
        var result = _validator.RewriteWithTempName(script, Platform.PostgreSQL);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE OR REPLACE FUNCTION ""public""\.""" + @"_validate_[a-f0-9]+"""));
        Assert.That(result.RewrittenScript, Does.Contain("RETURNS integer"));
    }

    [Test]
    public void RewriteWithTempName_PostgreSQL_View_RewritesCorrectly()
    {
        var script = "CREATE VIEW \"public\".\"my_view\"\nAS\nSELECT 1 AS col;";
        var result = _validator.RewriteWithTempName(script, Platform.PostgreSQL);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE VIEW ""public""\.""" + @"_validate_[a-f0-9]+"""));
        Assert.That(result.RewrittenScript, Does.Contain("SELECT 1 AS col;"));
    }

    #endregion

    #region RewriteWithTempName — MySQL

    [Test]
    public void RewriteWithTempName_MySQL_Function_RewritesCorrectly()
    {
        var script = "CREATE FUNCTION `my_func`(x INT)\nRETURNS INT\nBEGIN RETURN x; END";
        var result = _validator.RewriteWithTempName(script, Platform.MySQL);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE FUNCTION `_validate_[a-f0-9]+`"));
        Assert.That(result.RewrittenScript, Does.Contain("RETURNS INT"));
    }

    [Test]
    public void RewriteWithTempName_MySQL_DefinerClause_RewritesCorrectly()
    {
        var script = "CREATE DEFINER=`root`@`%` FUNCTION `my_func`(x INT)\nRETURNS INT\nBEGIN RETURN x; END";
        var result = _validator.RewriteWithTempName(script, Platform.MySQL);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE DEFINER=`root`@`%` FUNCTION `_validate_[a-f0-9]+`"));
        Assert.That(result.RewrittenScript, Does.Contain("RETURNS INT"));
    }

    // The extracted script opens with the real object's DROP. Validating that line as written would drop the
    // source's object, and no rollback restores DDL on this family -- so the DROP is renamed with the CREATE.
    [Test]
    public void RewriteWithTempName_MySQL_RenamesTheDropWithTheCreate_AndNothingElse()
    {
        var script = "DROP FUNCTION IF EXISTS `my_func`;\nSET SESSION sql_mode = 'STRICT_TRANS_TABLES';\nDELIMITER //\n" +
                     "CREATE FUNCTION `my_func`(x INT)\nRETURNS INT\nBEGIN RETURN my_func_helper(x); END //\nDELIMITER ;";
        var result = _validator.RewriteWithTempName(script, Platform.MySQL);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"DROP FUNCTION IF EXISTS `_validate_[a-f0-9]+`;"));
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE FUNCTION `_validate_[a-f0-9]+`\(x INT\)"));
        Assert.That(result.RewrittenScript, Does.Not.Contain("`my_func`"));
        Assert.That(result.RewrittenScript, Does.Contain("my_func_helper(x)"));
        Assert.That(result.ObjectKind, Is.EqualTo("FUNCTION"));
    }

    [Test]
    public void RewriteWithTempName_MySQL_OrReplaceViewWithOptions_RewritesCorrectly()
    {
        var script = "CREATE OR REPLACE ALGORITHM=MERGE SQL SECURITY INVOKER VIEW `v_opts` AS select `t`.`x` AS `x` from `t` WITH CASCADED CHECK OPTION";
        var result = _validator.RewriteWithTempName(script, Platform.MySQL);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE OR REPLACE ALGORITHM=MERGE SQL SECURITY INVOKER VIEW `_validate_[a-f0-9]+` AS select"));
        Assert.That(result.ObjectKind, Is.EqualTo("VIEW"));
    }

    [Test]
    public void RewriteWithTempName_MySQL_DualityView_RewritesCorrectly()
    {
        var script = "CREATE OR REPLACE ALGORITHM=UNDEFINED SQL SECURITY DEFINER JSON RELATIONAL DUALITY VIEW `dv` AS select json_duality_object('_id':`t`.`x`) AS `d` from `t`";
        var result = _validator.RewriteWithTempName(script, Platform.MySQL);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"JSON RELATIONAL DUALITY VIEW `_validate_[a-f0-9]+` AS select"));
        Assert.That(result.ObjectKind, Is.EqualTo("VIEW"));
    }

    [Test]
    public void RewriteWithTempName_MySQL_AggregateFunction_RewritesCorrectly()
    {
        var script = "DROP FUNCTION IF EXISTS `f_agg`;\nDELIMITER //\nCREATE AGGREGATE FUNCTION `f_agg`(x INT) RETURNS int(11)\nBEGIN RETURN 1; END //\nDELIMITER ;";
        var result = _validator.RewriteWithTempName(script, Platform.MySQL);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"CREATE AGGREGATE FUNCTION `_validate_[a-f0-9]+`\(x INT\)"));
        Assert.That(result.RewrittenScript, Does.Match(@"DROP FUNCTION IF EXISTS `_validate_[a-f0-9]+`;"));
    }

    // SHOW CREATE renders a routine created under ANSI_QUOTES with double-quoted identifiers.
    [Test]
    public void RewriteWithTempName_MySQL_AnsiQuotedDefinerAndName_RewritesCorrectly()
    {
        var script = "DROP FUNCTION IF EXISTS `f_mode`;\nCREATE DEFINER=\"root\"@\"localhost\" FUNCTION \"f_mode\"(a VARCHAR(10)) RETURNS varchar(20)\nRETURN a";
        var result = _validator.RewriteWithTempName(script, Platform.MySQL);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match("CREATE DEFINER=\"root\"@\"localhost\" FUNCTION `_validate_[a-f0-9]+`\\(a VARCHAR"));
        Assert.That(result.RewrittenScript, Does.Match(@"DROP FUNCTION IF EXISTS `_validate_[a-f0-9]+`;"));
    }

    #endregion

    #region RewriteWithTempName — Edge Cases

    [Test]
    public void RewriteWithTempName_MultiLine_NameOnNextLine_RewritesCorrectly()
    {
        var script = "CREATE FUNCTION\n[dbo].[fn_Foo](@x INT)\nRETURNS INT\nAS\nBEGIN RETURN @x END";
        var result = _validator.RewriteWithTempName(script, Platform.SqlServer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Match(@"\[dbo\]\.\[_validate_[a-f0-9]+\]"));
        Assert.That(result.RewrittenScript, Does.Contain("RETURNS INT"));
    }

    [Test]
    public void RewriteWithTempName_PreservesScriptBody()
    {
        var body = "(@Param1 INT, @Param2 VARCHAR(100))\nRETURNS TABLE\nAS\nRETURN SELECT * FROM dbo.SomeTable WHERE Id = @Param1";
        var script = $"CREATE FUNCTION [dbo].[fn_Test]{body}";
        var result = _validator.RewriteWithTempName(script, Platform.SqlServer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.RewrittenScript, Does.Contain(body));
    }

    [Test]
    public void RewriteWithTempName_UnrecognizedPattern_ReturnsFailure()
    {
        var script = "SELECT 1";
        var result = _validator.RewriteWithTempName(script, Platform.SqlServer);

        Assert.That(result.Success, Is.False);
    }

    #endregion

    #region GenerateParseOnlyWrapper

    [Test]
    public void GenerateParseOnlyWrapper_SqlServer_Trigger_WrapsWithParseOnly()
    {
        var script = "CREATE TRIGGER [dbo].[trg_Insert] ON [dbo].[MyTable] AFTER INSERT AS SELECT 1";
        var result = _validator.GenerateParseOnlyWrapper(script, Platform.SqlServer);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.StartWith("SET PARSEONLY ON;"));
        Assert.That(result, Does.Contain(script));
        Assert.That(result, Does.EndWith("SET PARSEONLY OFF;"));
    }

    [Test]
    public void GenerateParseOnlyWrapper_PostgreSQL_Trigger_ReturnsNull()
    {
        var script = "CREATE TRIGGER my_trigger AFTER INSERT ON my_table FOR EACH ROW EXECUTE FUNCTION my_func();";
        var result = _validator.GenerateParseOnlyWrapper(script, Platform.PostgreSQL);

        Assert.That(result, Is.Null);
    }

    // CREATE TRIGGER is not a preparable statement on MySQL or MariaDB, so a PREPARE wrapper can never parse one,
    // and a temporary trigger would sit live on the source table while it is checked. Triggers are not validated.
    [Test]
    public void GenerateParseOnlyWrapper_MySQL_Trigger_ReturnsNull()
    {
        var script = "CREATE TRIGGER my_trigger AFTER INSERT ON my_table FOR EACH ROW BEGIN INSERT INTO log VALUES(NEW.id); END";
        Assert.That(_validator.GenerateParseOnlyWrapper(script, Platform.MySQL), Is.Null);
    }

    #endregion

    #region ValidateScript — MySQL

    [Test]
    public void ValidateScript_MySQL_Trigger_IsSkipped()
    {
        var connection = Substitute.For<IDbConnection>();

        var result = _validator.ValidateScript(connection, "CREATE TRIGGER tr BEFORE INSERT ON t FOR EACH ROW SET NEW.x = 1", ScriptObjectType.Triggers, Platform.MySQL);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Skipped, Is.True);
        connection.DidNotReceive().CreateCommand();
    }

    // No transaction on this family: DDL commits implicitly, so the temp object is dropped explicitly instead,
    // and DELIMITER blocks are split the way the deploy splits them rather than sent to the server as text.
    [Test]
    public void ValidateScript_MySQL_RunsTheBatchesWithoutATransaction_AndDropsTheTempObject()
    {
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        connection.CreateCommand().Returns(command);
        var sent = new System.Collections.Generic.List<string>();
        command.When(c => c.CommandText = Arg.Any<string>()).Do(ci => sent.Add(ci.ArgAt<string>(0)));

        var script = "DROP FUNCTION IF EXISTS `f`;\nSET @ss_saved_sql_mode = @@SESSION.sql_mode;\nSET SESSION sql_mode = 'STRICT_TRANS_TABLES';\n" +
                     "DELIMITER //\nCREATE FUNCTION `f`(x INT) RETURNS INT\nDETERMINISTIC\nBEGIN RETURN x; END //\nDELIMITER ;\nSET SESSION sql_mode = @ss_saved_sql_mode;";
        var result = _validator.ValidateScript(connection, script, ScriptObjectType.Functions, Platform.MySQL);

        Assert.That(result.IsValid, Is.True);
        connection.DidNotReceive().BeginTransaction();
        Assert.That(sent, Has.Count.EqualTo(6));
        Assert.That(sent[0], Does.Match(@"^DROP FUNCTION IF EXISTS `_validate_[a-f0-9]+`;$"));
        Assert.That(sent[3], Does.StartWith("CREATE FUNCTION `_validate_"));
        Assert.That(sent[3], Does.Contain("BEGIN RETURN x; END"));
        Assert.That(sent[4], Is.EqualTo("SET SESSION sql_mode = @ss_saved_sql_mode;"));
        Assert.That(sent[5], Does.Match(@"^DROP FUNCTION IF EXISTS `_validate_[a-f0-9]+`$"), "the temp object is dropped last");
        Assert.That(sent, Has.None.Contains("DELIMITER"));
        Assert.That(sent, Has.None.Contains("`f`"), "the source's own object is never named");
    }

    [Test]
    public void ValidateScript_MySQL_DropsTheTempObjectAndRestoresTheMode_EvenWhenTheCreateFails()
    {
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        connection.CreateCommand().Returns(command);
        var sent = new System.Collections.Generic.List<string>();
        command.When(c => c.CommandText = Arg.Any<string>()).Do(ci => sent.Add(ci.ArgAt<string>(0)));
        command.When(c => c.ExecuteNonQuery()).Do(ci =>
        {
            if (command.CommandText.StartsWith("CREATE")) throw new ServerErrorException("near 'order int(11))'");
        });

        var script = "DROP PROCEDURE IF EXISTS `p`;\nSET @ss_saved_sql_mode = @@SESSION.sql_mode;\nSET SESSION sql_mode = 'ANSI_QUOTES';\n" +
                     "DELIMITER //\nCREATE PROCEDURE `p`(order INT)\nSELECT 1 //\nDELIMITER ;\nSET SESSION sql_mode = @ss_saved_sql_mode;";
        var result = _validator.ValidateScript(connection, script, ScriptObjectType.Procedures, Platform.MySQL);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("near 'order int(11))'"));
        Assert.That(sent[^2], Is.EqualTo("SET SESSION sql_mode = @ss_saved_sql_mode;"), "the session mode is restored after a failed CREATE");
        Assert.That(sent[^1], Does.Match(@"^DROP PROCEDURE IF EXISTS `_validate_[a-f0-9]+`$"));
    }

    [Test]
    public void ValidateScript_MySQL_UnrecognisedCreate_ReturnsError()
    {
        var connection = Substitute.For<IDbConnection>();

        var result = _validator.ValidateScript(connection, "SELECT 1", ScriptObjectType.Functions, Platform.MySQL);

        Assert.That(result.IsValid, Is.False);
        connection.DidNotReceive().CreateCommand();
    }

    #endregion

    #region ValidateScript

    [Test]
    public void ValidateScript_ValidScript_ReturnsIsValid()
    {
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var command = Substitute.For<IDbCommand>();
        connection.BeginTransaction().Returns(transaction);
        connection.CreateCommand().Returns(command);

        var script = "CREATE VIEW [dbo].[vw_Test]\nAS\nSELECT 1 AS Col";
        var result = _validator.ValidateScript(connection, script, ScriptObjectType.Views, Platform.SqlServer);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.ErrorMessage, Is.Null);
    }

    [Test]
    public void ValidateScript_InvalidScript_ReturnsError()
    {
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var command = Substitute.For<IDbCommand>();
        connection.BeginTransaction().Returns(transaction);
        connection.CreateCommand().Returns(command);
        command.When(c => c.ExecuteNonQuery()).Do(_ => throw new Exception("Invalid SQL syntax"));

        var script = "CREATE VIEW [dbo].[vw_Test]\nAS\nSELECT * FROM NonExistentTable";
        var result = _validator.ValidateScript(connection, script, ScriptObjectType.Views, Platform.SqlServer);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Invalid SQL syntax"));
    }

    [Test]
    public void ValidateScript_SkippedObjectType_ReturnsValid()
    {
        var connection = Substitute.For<IDbConnection>();

        var result = _validator.ValidateScript(connection, "CREATE SCHEMA [test]", ScriptObjectType.Schemas, Platform.SqlServer);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Skipped, Is.True);
        connection.DidNotReceive().BeginTransaction();
    }

    [Test]
    public void ValidateScript_TransactionAlwaysRolledBack()
    {
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var command = Substitute.For<IDbCommand>();
        connection.BeginTransaction().Returns(transaction);
        connection.CreateCommand().Returns(command);

        var script = "CREATE VIEW [dbo].[vw_Test]\nAS\nSELECT 1 AS Col";
        _validator.ValidateScript(connection, script, ScriptObjectType.Views, Platform.SqlServer);

        transaction.Received(1).Rollback();
        transaction.DidNotReceive().Commit();
    }

    [Test]
    public void ValidateScript_TransactionRolledBack_EvenOnError()
    {
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var command = Substitute.For<IDbCommand>();
        connection.BeginTransaction().Returns(transaction);
        connection.CreateCommand().Returns(command);
        command.When(c => c.ExecuteNonQuery()).Do(_ => throw new Exception("Error"));

        var script = "CREATE VIEW [dbo].[vw_Test]\nAS\nSELECT 1 AS Col";
        _validator.ValidateScript(connection, script, ScriptObjectType.Views, Platform.SqlServer);

        transaction.Received(1).Rollback();
    }

    [Test]
    public void ValidateScript_ParseOnlyType_UsesParseOnlyWrapper()
    {
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var command = Substitute.For<IDbCommand>();
        connection.BeginTransaction().Returns(transaction);
        connection.CreateCommand().Returns(command);

        var script = "CREATE TRIGGER [dbo].[trg_Test] ON [dbo].[MyTable] AFTER INSERT AS SELECT 1";
        _validator.ValidateScript(connection, script, ScriptObjectType.Triggers, Platform.SqlServer);

        command.Received().CommandText = Arg.Is<string>(s => s.Contains("SET PARSEONLY ON;"));
    }

    [Test]
    public void ValidateScript_PostgreSQL_ParseOnlyType_SkipsValidation()
    {
        var connection = Substitute.For<IDbConnection>();

        var script = "CREATE TRIGGER my_trigger AFTER INSERT ON my_table FOR EACH ROW EXECUTE FUNCTION my_func();";
        var result = _validator.ValidateScript(connection, script, ScriptObjectType.Triggers, Platform.PostgreSQL);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Skipped, Is.True);
        connection.DidNotReceive().BeginTransaction();
    }

    [Test]
    public void ValidateScript_SkippedTypes_IncludesAllNonValidatable()
    {
        var connection = Substitute.For<IDbConnection>();

        var skippedTypes = new[]
        {
            ScriptObjectType.Schemas, ScriptObjectType.DataTypes, ScriptObjectType.FullTextCatalogs,
            ScriptObjectType.FullTextStopLists, ScriptObjectType.XMLSchemaCollections,
            ScriptObjectType.DomainTypes, ScriptObjectType.EnumTypes, ScriptObjectType.CompositeTypes,
            ScriptObjectType.Sequences, ScriptObjectType.Events, ScriptObjectType.None,
            ScriptObjectType.IndexedViews, ScriptObjectType.MaterializedViews
        };

        foreach (var objectType in skippedTypes)
        {
            var result = _validator.ValidateScript(connection, "script", objectType, Platform.SqlServer);
            Assert.That(result.Skipped, Is.True, $"Expected {objectType} to be skipped");
        }
    }

    #endregion
}
