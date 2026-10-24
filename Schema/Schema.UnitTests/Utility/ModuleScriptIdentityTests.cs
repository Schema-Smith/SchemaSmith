// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

// The identity is what the unchanged-module stamp keys its rendering on, so a script that creates one recognisable
// module yields it, and anything else (no CREATE, two CREATEs, a kind the reader does not render) yields null and
// the script always runs.
public class ModuleScriptIdentityTests
{
    private static ModuleScriptIdentity Parse(Platform platform, params string[] batches) =>
        ModuleScriptIdentity.Parse(new List<string>(batches), platform);

    [Test]
    public void SqlServer_CreateOrAlterProcedure_WithBrackets()
    {
        var id = Parse(Platform.SqlServer, "SET ANSI_NULLS ON", "CREATE OR ALTER PROCEDURE [Sales].[usp Report] @y INT AS BEGIN SELECT 1 END");
        Assert.Multiple(() =>
        {
            Assert.That(id.Kind, Is.EqualTo(ModuleKind.Procedure));
            Assert.That(id.Schema, Is.EqualTo("Sales"));
            Assert.That(id.Name, Is.EqualTo("usp Report"));
        });
    }

    [Test]
    public void SqlServer_UnqualifiedName_DefaultsToDbo_AndProcShorthandCounts()
    {
        var id = Parse(Platform.SqlServer, "CREATE PROC MyProc AS SELECT 1");
        Assert.That((id.Kind, id.Schema, id.Name), Is.EqualTo((ModuleKind.Procedure, "dbo", "MyProc")));
    }

    [Test]
    public void SqlServer_Trigger_CarriesItsTable()
    {
        var id = Parse(Platform.SqlServer, "CREATE OR ALTER TRIGGER [dbo].[trg_Audit] ON [dbo].[Orders] AFTER INSERT AS SELECT 1");
        Assert.That((id.Kind, id.Schema, id.Name, id.TableSchema, id.TableName), Is.EqualTo((ModuleKind.Trigger, "dbo", "trg_Audit", "dbo", "Orders")));
    }

    // The legacy form SchemaTongs writes for servers below 2016 SP1: the CREATE sits inside an EXEC string, so the
    // ALTER is the statement that names the module.
    [Test]
    public void SqlServer_LegacyForm_GuardedStubThenAlter_IsTheOneModule()
    {
        var id = Parse(Platform.SqlServer,
            "IF OBJECT_ID(N'[dbo].[p]') IS NULL EXEC('CREATE PROCEDURE [dbo].[p] AS RETURN')",
            "ALTER PROCEDURE [dbo].[p] AS SELECT 1");
        Assert.That((id?.Kind, id?.Schema, id?.Name), Is.EqualTo((ModuleKind.Procedure, "dbo", "p")), "the legacy form must be stamped like any other module");
    }

    [Test]
    public void SqlServer_CreateThenAlterOutsideAString_IsTwoStatements_AndSoNone()
    {
        Assert.That(Parse(Platform.SqlServer, "CREATE VIEW dbo.v AS SELECT 1 AS x", "ALTER VIEW dbo.v AS SELECT 2 AS x"), Is.Null);
    }

    [Test]
    public void PostgreSql_OrReplaceFunction_QuotedSchema()
    {
        var id = Parse(Platform.PostgreSQL, "CREATE OR REPLACE FUNCTION \"Test\".\"Fn\"(a int) RETURNS int LANGUAGE sql AS 'SELECT $1';");
        Assert.That((id.Kind, id.Schema, id.Name), Is.EqualTo((ModuleKind.Function, "Test", "Fn")));
    }

    [Test]
    public void PostgreSql_Trigger_TakesTheTablesSchema_AndLowercasesBareNames()
    {
        var id = Parse(Platform.PostgreSQL, "DROP TRIGGER IF EXISTS Tr_Plain ON pgp.T;", "CREATE CONSTRAINT TRIGGER Tr_Plain AFTER INSERT ON pgp.T DEFERRABLE FOR EACH ROW EXECUTE FUNCTION tf();");
        Assert.That((id.Kind, id.Schema, id.Name, id.TableName), Is.EqualTo((ModuleKind.Trigger, "pgp", "tr_plain", "t")));
    }

    [Test]
    public void PostgreSql_MaterializedView_IsNotAModule()
    {
        Assert.That(Parse(Platform.PostgreSQL, "CREATE MATERIALIZED VIEW public.mv AS SELECT 1;"), Is.Null);
    }

    [Test]
    public void MySql_ShowCreateShape_WithPinsAndDrop()
    {
        var id = Parse(Platform.MySQL,
            "DROP FUNCTION IF EXISTS `f_mode`;", "SET @ss_saved_sql_mode = @@SESSION.sql_mode;", "SET SESSION sql_mode = 'ANSI_QUOTES';",
            "CREATE DEFINER=\"root\"@\"localhost\" FUNCTION \"f_mode\"(a VARCHAR(10)) RETURNS varchar(20)\nRETURN a", "SET SESSION sql_mode = @ss_saved_sql_mode;");
        Assert.That((id.Kind, id.Schema, id.Name), Is.EqualTo((ModuleKind.Function, (string)null, "f_mode")));
    }

    [Test]
    public void MySql_OrReplaceViewWithOptions_AndEvents()
    {
        var view = Parse(Platform.MariaDb, "CREATE OR REPLACE ALGORITHM=MERGE SQL SECURITY INVOKER VIEW `v_opts` AS select 1;");
        var evt = Parse(Platform.MySQL, "CREATE EVENT `e1` ON SCHEDULE EVERY 1 DAY DO SELECT 1");
        Assert.Multiple(() =>
        {
            Assert.That((view.Kind, view.Name), Is.EqualTo((ModuleKind.View, "v_opts")));
            Assert.That((evt.Kind, evt.Name), Is.EqualTo((ModuleKind.Event, "e1")));
        });
    }

    [Test]
    public void TwoCreates_OrNone_YieldNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Parse(Platform.SqlServer, "CREATE VIEW a AS SELECT 1", "CREATE VIEW b AS SELECT 2"), Is.Null, "two modules in one script are not one identity");
            Assert.That(Parse(Platform.SqlServer, "EXEC sp_configure 'x', 1"), Is.Null);
            Assert.That(Parse(Platform.PostgreSQL, "CREATE TABLE t (a int);"), Is.Null);
        });
    }

    [Test]
    public void ACreateInsideAStringLiteral_DoesNotCount()
    {
        var id = Parse(Platform.SqlServer, "CREATE OR ALTER PROCEDURE dbo.p AS EXEC('CREATE VIEW v AS SELECT 1')");
        Assert.That((id.Kind, id.Name), Is.EqualTo((ModuleKind.Procedure, "p")));
    }

    // A CREATE named in a comment is not the module: recyclebin_CleanupJob.sql's header shows how to schedule it with a
    // CREATE EVENT, which was taken for the script's module, so the script never stamped and ran on every deploy.
    [Test]
    public void ACreateInsideAComment_DoesNotCount()
    {
        var mySql = Parse(Platform.MySQL,
            "-- Schedule it with a CREATE EVENT, e.g.\n--   CREATE EVENT recyclebin_cleanup ON SCHEDULE EVERY 1 DAY\n"
            + "# CREATE VIEW `v_hash` AS select 1\n/* CREATE FUNCTION `f_block`() RETURNS INT RETURN 1 */\n"
            + "DROP PROCEDURE IF EXISTS `recyclebin_CleanupJob`;",
            "CREATE PROCEDURE `recyclebin_CleanupJob`() BEGIN SELECT 'it''s -- not a comment', 'a\\'b /* nor this */'; END");
        var sqlServer = Parse(Platform.SqlServer,
            "-- CREATE VIEW dbo.v_line AS SELECT 1\r\n/* CREATE PROCEDURE dbo.p_block AS SELECT 1 */\r\nCREATE OR ALTER PROCEDURE dbo.p AS SELECT 1");
        var postgreSql = Parse(Platform.PostgreSQL,
            "/* CREATE VIEW v_block AS SELECT 1 */ -- CREATE FUNCTION f_line() ...\nCREATE OR REPLACE PROCEDURE public.p() LANGUAGE sql AS $$ SELECT 1 $$;");
        Assert.Multiple(() =>
        {
            Assert.That((mySql?.Kind, mySql?.Name), Is.EqualTo(((ModuleKind?)ModuleKind.Procedure, "recyclebin_CleanupJob")));
            Assert.That((sqlServer?.Kind, sqlServer?.Name), Is.EqualTo(((ModuleKind?)ModuleKind.Procedure, "p")));
            Assert.That((postgreSql?.Kind, postgreSql?.Name), Is.EqualTo(((ModuleKind?)ModuleKind.Procedure, "p")));
        });
    }

    // A script that drops the module it creates loses the module's grants on every engine, so the quench captures
    // them before such a script and puts them back after it. Only a DROP of the module's own kind and name counts; a
    // DROP inside a string executed later still drops, so literals are not blanked here -- capturing when nothing
    // was lost is harmless, missing a drop is not.
    private static bool Drops(Platform platform, params string[] batches) =>
        ModuleScriptIdentity.DropsModule(new List<string>(batches), platform);

    [Test]
    public void DropsModule_SqlServer_GuardedDropThenCreate_IsTrue()
    {
        Assert.That(Drops(Platform.SqlServer,
            "IF OBJECT_ID(N'[dbo].[p]') IS NOT NULL DROP PROCEDURE [dbo].[p]",
            "CREATE PROCEDURE [dbo].[p] AS SELECT 1"), Is.True);
    }

    [Test]
    public void DropsModule_SqlServer_CreateOrAlter_AndTheLegacyStubForm_AreFalse()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Drops(Platform.SqlServer, "CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1"), Is.False, "CREATE OR ALTER keeps permissions");
            Assert.That(Drops(Platform.SqlServer,
                "IF OBJECT_ID(N'[dbo].[p]') IS NULL EXEC('CREATE PROCEDURE [dbo].[p] AS RETURN')",
                "ALTER PROCEDURE [dbo].[p] AS SELECT 1"), Is.False, "the legacy form alters in place");
        });
    }

    [Test]
    public void DropsModule_SqlServer_DropInsideAnExecString_StillCounts()
    {
        Assert.That(Drops(Platform.SqlServer, "EXEC('DROP VIEW dbo.v')", "CREATE VIEW dbo.v AS SELECT 1 AS x"), Is.True);
    }

    [Test]
    public void DropsModule_DropOfAnotherObject_OrAnotherKind_IsFalse()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Drops(Platform.SqlServer, "DROP TABLE dbo.v_old", "CREATE VIEW dbo.v AS SELECT 1 AS x"), Is.False, "a different object");
            Assert.That(Drops(Platform.SqlServer, "DROP PROCEDURE dbo.v", "CREATE VIEW dbo.v AS SELECT 1 AS x"), Is.False, "the same name as a different kind");
            Assert.That(Drops(Platform.PostgreSQL, "DROP FUNCTION IF EXISTS public.f_other(integer);", "CREATE FUNCTION public.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;"), Is.False, "a different function");
        });
    }

    [Test]
    public void DropsModule_PostgreSql_DropIfExistsThenCreate_IsTrue_CreateOrReplace_IsFalse()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Drops(Platform.PostgreSQL, "DROP FUNCTION IF EXISTS public.f(integer);\nCREATE FUNCTION public.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;"), Is.True);
            Assert.That(Drops(Platform.PostgreSQL, "DROP PROCEDURE IF EXISTS \"Sales\".\"P\"();\nCREATE PROCEDURE \"Sales\".\"P\"() LANGUAGE sql AS $$ SELECT 1 $$;"), Is.True, "quoted names");
            Assert.That(Drops(Platform.PostgreSQL, "CREATE OR REPLACE FUNCTION public.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;"), Is.False, "CREATE OR REPLACE keeps the ACL");
        });
    }

    [Test]
    public void DropsModule_MySql_DropIfExistsThenCreate_IsTrue_MariaDbCreateOrReplace_IsFalse()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Drops(Platform.MySQL, "DROP PROCEDURE IF EXISTS `p`;", "CREATE PROCEDURE `p`() SELECT 1"), Is.True);
            Assert.That(Drops(Platform.MariaDb, "DROP FUNCTION IF EXISTS f;", "CREATE FUNCTION f(a INT) RETURNS INT DETERMINISTIC RETURN a + 1"), Is.True);
            Assert.That(Drops(Platform.MariaDb, "CREATE OR REPLACE PROCEDURE `p`() SELECT 1"), Is.False, "MariaDB keeps the grants across CREATE OR REPLACE");
        });
    }

    [Test]
    public void DropsModule_WithoutARecognisableModule_IsFalse()
    {
        Assert.That(Drops(Platform.SqlServer, "DROP PROCEDURE dbo.p"), Is.False, "no CREATE: nothing is re-created, nothing to put grants back on");
    }
}
