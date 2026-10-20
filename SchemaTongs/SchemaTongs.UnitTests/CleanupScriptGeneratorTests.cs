// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Schema.Domain;

namespace SchemaTongs.UnitTests;

[TestFixture]
public class CleanupScriptGeneratorTests
{
    [Test]
    public void GenerateDropStatement_SqlServer_ViewFile_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.MyView.sql", ScriptObjectType.Views, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP VIEW IF EXISTS [dbo].[MyView];"));
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_FunctionFile_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.my_func.sql", ScriptObjectType.Functions, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP FUNCTION IF EXISTS \"public\".\"my_func\";"));
    }

    [Test]
    public void GenerateDropStatement_MySQL_ViewFile_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("my_view.sql", ScriptObjectType.Views, Platform.MySQL);
        Assert.That(result, Is.EqualTo("DROP VIEW IF EXISTS `my_view`;"));
    }

    [Test]
    public void GenerateDropStatement_SqlErrorExtension_HandledSameAsSql()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.MyView.sqlerror", ScriptObjectType.Views, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP VIEW IF EXISTS [dbo].[MyView];"));
    }

    [Test]
    public void GenerateDropStatement_JsonTable_SqlServer_ReturnsDropTable()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.MyTable.json", ScriptObjectType.None, Platform.SqlServer, "Tables");
        Assert.That(result, Is.EqualTo("DROP TABLE IF EXISTS [dbo].[MyTable];"));
    }

    [Test]
    public void GenerateDropStatement_JsonIndexedView_SqlServer_ReturnsDropView()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.SalesReport.json", ScriptObjectType.None, Platform.SqlServer, "Indexed Views");
        Assert.That(result, Is.EqualTo("DROP VIEW IF EXISTS [dbo].[SalesReport];"));
    }

    [Test]
    public void GenerateDropStatement_JsonMaterializedView_PostgreSQL_ReturnsDropMaterializedView()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.mv_summary.json", ScriptObjectType.None, Platform.PostgreSQL, "Materialized Views");
        Assert.That(result, Is.EqualTo("DROP MATERIALIZED VIEW IF EXISTS \"public\".\"mv_summary\";"));
    }

    [Test]
    public void GenerateDropStatement_UnexpectedFilename_ReturnsNull()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("readme.txt", ScriptObjectType.Views, Platform.SqlServer);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GenerateDropStatement_Schemas_ReturnsNull()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.sql", ScriptObjectType.Schemas, Platform.SqlServer);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GenerateDropStatement_SqlServer_Procedures_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.usp_GetData.sql", ScriptObjectType.Procedures, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP PROCEDURE IF EXISTS [dbo].[usp_GetData];"));
    }

    [Test]
    public void GenerateDropStatement_SqlServer_Triggers_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.trg_Insert.sql", ScriptObjectType.Triggers, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP TRIGGER IF EXISTS [dbo].[trg_Insert];"));
    }

    [Test]
    public void GenerateDropStatement_SqlServer_DDLTriggers_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("ddl_audit.sql", ScriptObjectType.DDLTriggers, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP TRIGGER IF EXISTS [ddl_audit];"));
    }

    [Test]
    public void GenerateDropStatement_SqlServer_DataTypes_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.PhoneNumber.sql", ScriptObjectType.DataTypes, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP TYPE IF EXISTS [dbo].[PhoneNumber];"));
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_DomainTypes_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.email_addr.sql", ScriptObjectType.DomainTypes, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP DOMAIN IF EXISTS \"public\".\"email_addr\";"));
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_EnumTypes_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.status_type.sql", ScriptObjectType.EnumTypes, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP TYPE IF EXISTS \"public\".\"status_type\";"));
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_Aggregates_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.my_agg.sql", ScriptObjectType.Aggregates, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP AGGREGATE IF EXISTS \"public\".\"my_agg\";"));
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_Sequences_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.my_seq.sql", ScriptObjectType.Sequences, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP SEQUENCE IF EXISTS \"public\".\"my_seq\";"));
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_Rules_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.my_rule.sql", ScriptObjectType.Rules, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP RULE IF EXISTS \"public\".\"my_rule\";"));
    }

    [Test]
    public void GenerateDropStatement_MySQL_Events_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("daily_cleanup.sql", ScriptObjectType.Events, Platform.MySQL);
        Assert.That(result, Is.EqualTo("DROP EVENT IF EXISTS `daily_cleanup`;"));
    }

    [Test]
    public void GenerateDropStatement_SqlServer_Sequences_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.my_seq.sql", ScriptObjectType.Sequences, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP SEQUENCE IF EXISTS [dbo].[my_seq];"));
    }

    [Test]
    public void GenerateDropStatement_SqlServer_Synonyms_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.my_synonym.sql", ScriptObjectType.Synonyms, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP SYNONYM IF EXISTS [dbo].[my_synonym];"));
    }

    [Test]
    public void GenerateDropStatement_MariaDb_Sequences_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("my_seq.sql", ScriptObjectType.Sequences, Platform.MariaDb);
        Assert.That(result, Is.EqualTo("DROP SEQUENCE IF EXISTS `my_seq`;"));
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_Collations_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.my_collation.sql", ScriptObjectType.Collations, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP COLLATION IF EXISTS \"public\".\"my_collation\";"));
    }

    [TestCase("app.floatrange.sql", ScriptObjectType.RangeTypes, "DROP TYPE IF EXISTS \"app\".\"floatrange\";")]
    [TestCase("app.docs.sql", ScriptObjectType.TextSearchConfigurations, "DROP TEXT SEARCH CONFIGURATION IF EXISTS \"app\".\"docs\";")]
    [TestCase("audit_ddl.sql", ScriptObjectType.EventTriggers, "DROP EVENT TRIGGER IF EXISTS \"audit_ddl\";")]
    public void GenerateDropStatement_PostgreSQL_2Point8Kinds_ReturnCorrectDrop(string file, ScriptObjectType type, string expected)
    {
        Assert.That(CleanupScriptGenerator.GenerateDropStatement(file, type, Platform.PostgreSQL), Is.EqualTo(expected));
    }

    // An operator's or a cast's file name cannot be turned back into the signature its DROP needs.
    [TestCase("app.===(app.cents, app.cents).sql", ScriptObjectType.Operators)]
    [TestCase("app.cents AS integer.sql", ScriptObjectType.Casts)]
    public void GenerateDropStatement_PostgreSQL_OperatorsAndCasts_WriteNoDrop(string file, ScriptObjectType type)
    {
        Assert.That(CleanupScriptGenerator.GenerateDropStatement(file, type, Platform.PostgreSQL), Is.Null);
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_Publications_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("my_pub.sql", ScriptObjectType.Publications, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP PUBLICATION IF EXISTS \"my_pub\";"));
    }

    [Test]
    public void GenerateDropStatement_SqlServer_FullTextCatalogs_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("SearchCatalog.sql", ScriptObjectType.FullTextCatalogs, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP FULLTEXT CATALOG IF EXISTS [SearchCatalog];"));
    }

    [Test]
    public void GenerateDropStatement_SqlServer_FullTextStopLists_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("CustomStopList.sql", ScriptObjectType.FullTextStopLists, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP FULLTEXT STOPLIST IF EXISTS [CustomStopList];"));
    }

    [Test]
    public void GenerateDropStatement_SqlServer_XMLSchemaCollections_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("dbo.MyXmlSchema.sql", ScriptObjectType.XMLSchemaCollections, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("DROP XML SCHEMA COLLECTION IF EXISTS [dbo].[MyXmlSchema];"));
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_TriggerFunctions_ReturnsDropFunction()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.trg_func.sql", ScriptObjectType.TriggerFunctions, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP FUNCTION IF EXISTS \"public\".\"trg_func\";"));
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_WindowFunctions_ReturnsDropFunction()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.win_func.sql", ScriptObjectType.WindowFunctions, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP FUNCTION IF EXISTS \"public\".\"win_func\";"));
    }

    [Test]
    public void GenerateDropStatement_PostgreSQL_CompositeTypes_ReturnsDropType()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("public.address_type.sql", ScriptObjectType.CompositeTypes, Platform.PostgreSQL);
        Assert.That(result, Is.EqualTo("DROP TYPE IF EXISTS \"public\".\"address_type\";"));
    }

    [Test]
    public void GenerateDropStatement_MySQL_Table_Json_ReturnsCorrectDrop()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("customers.json", ScriptObjectType.None, Platform.MySQL, "Tables");
        Assert.That(result, Is.EqualTo("DROP TABLE IF EXISTS `customers`;"));
    }

    [Test]
    public void GenerateSummaryHeader_ReturnsFormattedHeader()
    {
        var header = CleanupScriptGenerator.GenerateSummaryHeader(5);
        Assert.That(header, Does.StartWith("-- 5 orphaned objects detected during extraction on "));
        Assert.That(header, Does.Match(@"-- 5 orphaned objects detected during extraction on \d{4}-\d{2}-\d{2}"));
    }

    [Test]
    public void GenerateCleanupScript_ProducesCompleteScript()
    {
        var files = new List<string>
        {
            "dbo.OldView.sql",
            "dbo.LegacyProc.sql"
        };

        var result = CleanupScriptGenerator.GenerateCleanupScript(files, ScriptObjectType.Views, Platform.SqlServer);

        Assert.That(result, Does.Contain("-- 2 orphaned objects detected during extraction on"));
        Assert.That(result, Does.Contain("DROP VIEW IF EXISTS [dbo].[OldView];"));
        Assert.That(result, Does.Contain("DROP VIEW IF EXISTS [dbo].[LegacyProc];"));
    }

    [Test]
    public void GenerateCleanupScript_SkipsUnparsableFiles_IncludesRest()
    {
        var files = new List<string>
        {
            "dbo.GoodView.sql",
            "readme.txt",
            "dbo.AnotherView.sql"
        };

        var result = CleanupScriptGenerator.GenerateCleanupScript(files, ScriptObjectType.Views, Platform.SqlServer);

        Assert.That(result, Does.Contain("DROP VIEW IF EXISTS [dbo].[GoodView];"));
        Assert.That(result, Does.Contain("DROP VIEW IF EXISTS [dbo].[AnotherView];"));
        Assert.That(result, Does.Not.Contain("readme"));
    }

    [Test]
    public void GenerateCleanupScript_JsonFolder_ProducesCorrectStatements()
    {
        var files = new List<string>
        {
            "dbo.Orders.json",
            "dbo.Customers.json"
        };

        var result = CleanupScriptGenerator.GenerateCleanupScript(files, ScriptObjectType.None, Platform.SqlServer, "Tables");

        Assert.That(result, Does.Contain("DROP TABLE IF EXISTS [dbo].[Orders];"));
        Assert.That(result, Does.Contain("DROP TABLE IF EXISTS [dbo].[Customers];"));
    }

    [Test]
    public void GenerateDropStatement_NoneWithoutFolder_ReturnsNull()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("something.sql", ScriptObjectType.None, Platform.SqlServer);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GenerateDropStatement_NoneWithUnknownFolder_ReturnsNull()
    {
        var result = CleanupScriptGenerator.GenerateDropStatement("something.sql", ScriptObjectType.None, Platform.SqlServer, "Before Scripts");
        Assert.That(result, Is.Null);
    }
}
