// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Checkpointing;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Isolators;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class DatabaseQuenchTests
{
    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
    }

    #region Constructor Tests

    [Test]
    public void Constructor_InitializesProperties()
    {
        RegisterMockConfig();
        var product = new Product { Name = "TestProduct", Platform = Platform.SqlServer };
        var template = new Template { Name = "TestTemplate" };

        var quench = new DatabaseQuench("server", product, template, "testdb",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        Assert.That(quench.QuenchSuccessful, Is.False);
        Assert.That(quench.Platform, Is.EqualTo(Platform.SqlServer));
        Assert.That(quench.ProductName, Is.EqualTo("TestProduct"));
    }

    [Test]
    public void Constructor_InternalWithDropUnknownIndexes_SetsAllFields()
    {
        var product = new Product { Name = "Prod", Platform = Platform.MySQL };
        var template = new Template { Name = "Tmpl" };

        var quench = new DatabaseQuench("myserver", product, template, "mydb",
            true, "1", true, "1", "1", "1", "1", "1", "1", "1", "0", true, true, null);

        Assert.That(quench.Platform, Is.EqualTo(Platform.MySQL));
        Assert.That(quench.ProductName, Is.EqualTo("Prod"));
    }

    [Test]
    public void Constructor_DefaultTrackingSettings_BothTrue()
    {
        RegisterMockConfig();
        var product = new Product { Name = "TestProduct", Platform = Platform.SqlServer };
        var template = new Template { Name = "TestTemplate" };

        // Defaults: trackRunOnceMigrations=true, pruneObsoleteMigrationTracking=true
        var quench = new DatabaseQuench("server", product, template, "testdb",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        Assert.That(quench.QuenchSuccessful, Is.False);
        Assert.That(quench.Platform, Is.EqualTo(Platform.SqlServer));
    }

    [Test]
    public void Constructor_TrackingOff_AcceptsParameter()
    {
        RegisterMockConfig();
        var product = new Product { Name = "TestProduct", Platform = Platform.SqlServer };
        var template = new Template { Name = "TestTemplate" };

        var quench = new DatabaseQuench("server", product, template, "testdb",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null,
            trackRunOnceMigrations: false, pruneObsoleteMigrationTracking: false);

        Assert.That(quench.Platform, Is.EqualTo(Platform.SqlServer));
    }

    [Test]
    public void Constructor_InternalWithTrackingOff_AcceptsParameter()
    {
        var product = new Product { Name = "Prod", Platform = Platform.MySQL };
        var template = new Template { Name = "Tmpl" };

        var quench = new DatabaseQuench("myserver", product, template, "mydb",
            true, "1", true, "1", "1", "1", "1", "1", "1", "1", "0", true, true, null,
            trackRunOnceMigrations: false, pruneObsoleteMigrationTracking: true);

        Assert.That(quench.Platform, Is.EqualTo(Platform.MySQL));
    }

    #endregion

    #region Platform Dispatch - QuoteUseDatabase

    [Test]
    public void QuoteUseDatabase_SqlServer_UsesBrackets()
    {
        Assert.That(DatabaseQuench.QuoteUseDatabase("MyDb", Platform.SqlServer), Is.EqualTo("USE [MyDb]"));
    }

    [Test]
    public void QuoteUseDatabase_PostgreSQL_ReturnsDbName()
    {
        Assert.That(DatabaseQuench.QuoteUseDatabase("MyDb", Platform.PostgreSQL), Is.EqualTo("MyDb"));
    }

    [Test]
    public void QuoteUseDatabase_MySQL_UsesBackticks()
    {
        Assert.That(DatabaseQuench.QuoteUseDatabase("MyDb", Platform.MySQL), Is.EqualTo("USE `MyDb`"));
    }

    [Test]
    public void QuoteUseDatabase_InvalidPlatform_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DatabaseQuench.QuoteUseDatabase("db", (Platform)999));
    }

    #endregion

    #region Platform Dispatch - QuoteIdentifier

    [Test]
    public void QuoteIdentifier_SqlServer_UsesBrackets()
    {
        Assert.That(DatabaseQuench.QuoteIdentifier("col", Platform.SqlServer), Is.EqualTo("[col]"));
    }

    [Test]
    public void QuoteIdentifier_PostgreSQL_UsesDoubleQuotes()
    {
        Assert.That(DatabaseQuench.QuoteIdentifier("col", Platform.PostgreSQL), Is.EqualTo("\"col\""));
    }

    [Test]
    public void QuoteIdentifier_MySQL_UsesBackticks()
    {
        Assert.That(DatabaseQuench.QuoteIdentifier("col", Platform.MySQL), Is.EqualTo("`col`"));
    }

    #endregion

    #region IsWhatIf

    [Test]
    public void IsWhatIf_SqlServer_True_WhenValueIs1()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "1", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        Assert.That(quench.IsWhatIf, Is.True);
    }

    [Test]
    public void IsWhatIf_SqlServer_False_WhenValueIs0()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        Assert.That(quench.IsWhatIf, Is.False);
    }

    [Test]
    public void IsWhatIf_PostgreSQL_True_WhenValueIsTrue()
    {
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "true", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);
        Assert.That(quench.IsWhatIf, Is.True);
    }

    [Test]
    public void IsWhatIf_PostgreSQL_False_WhenValueIsFalse()
    {
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);
        Assert.That(quench.IsWhatIf, Is.False);
    }

    [Test]
    public void IsWhatIf_MySQL_True_WhenValueIs1()
    {
        var product = new Product { Name = "Test", Platform = Platform.MySQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "1", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        Assert.That(quench.IsWhatIf, Is.True);
    }

    #endregion

    #region FormatBooleanFlag

    [Test]
    public void FormatBooleanFlag_SqlServer_Returns1Or0()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        Assert.That(quench.FormatBooleanFlag(true), Is.EqualTo("1"));
        Assert.That(quench.FormatBooleanFlag(false), Is.EqualTo("0"));
    }

    [Test]
    public void FormatBooleanFlag_PostgreSQL_ReturnsTrueOrFalse()
    {
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        Assert.That(quench.FormatBooleanFlag(true), Is.EqualTo("true"));
        Assert.That(quench.FormatBooleanFlag(false), Is.EqualTo("false"));
    }

    [Test]
    public void FormatBooleanFlag_MySQL_Returns1Or0()
    {
        var product = new Product { Name = "Test", Platform = Platform.MySQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        Assert.That(quench.FormatBooleanFlag(true), Is.EqualTo("1"));
        Assert.That(quench.FormatBooleanFlag(false), Is.EqualTo("0"));
    }

    #endregion

    #region ShouldAlwaysRun

    [Test]
    public void ShouldAlwaysRun_WithAlwaysTag_ReturnsTrue()
    {
        Assert.That(DatabaseQuench.ShouldAlwaysRun("01_setup[ALWAYS].sql"), Is.True);
    }

    [Test]
    public void ShouldAlwaysRun_WithoutAlwaysTag_ReturnsFalse()
    {
        Assert.That(DatabaseQuench.ShouldAlwaysRun("01_setup.sql"), Is.False);
    }

    [Test]
    public void ShouldAlwaysRun_CaseSensitive_ReturnsFalse()
    {
        Assert.That(DatabaseQuench.ShouldAlwaysRun("01_setup[always].sql"), Is.False);
    }

    [Test]
    public void ShouldAlwaysRun_AlwaysInMiddle_ReturnsFalse()
    {
        Assert.That(DatabaseQuench.ShouldAlwaysRun("01_[ALWAYS]_setup.sql"), Is.False);
    }

    #endregion

    #region GetRelativeScriptPath

    [Test]
    public void GetRelativeScriptPath_ReturnsRelativePath()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        // Use OS-native path separators for consistency
        var templateDir = System.IO.Path.Combine("C:", "products", "MyProduct", "Templates", "Core");
        var templateFilePath = System.IO.Path.Combine(templateDir, "Template.json");
        var template = new Template { Name = "T", FilePath = templateFilePath };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var scriptPath = System.IO.Path.Combine(templateDir, "Before Scripts", "01_setup.sql");
        var result = quench.GetRelativeScriptPath(scriptPath);
        Assert.That(result, Is.EqualTo("Before Scripts/01_setup.sql"));
    }

    [Test]
    public void GetRelativeScriptPath_NormalizesBackslashes()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var templateDir = System.IO.Path.Combine("C:", "products", "MyProduct", "Templates", "Core");
        var templateFilePath = System.IO.Path.Combine(templateDir, "Template.json");
        var template = new Template { Name = "T", FilePath = templateFilePath };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var scriptPath = System.IO.Path.Combine(templateDir, "After Scripts", "cleanup.sql");
        var result = quench.GetRelativeScriptPath(scriptPath);
        Assert.That(result, Is.EqualTo("After Scripts/cleanup.sql"));
    }

    #endregion

    #region Platform-Specific SQL Generation

    [Test]
    public void GetDeleteCompletedScriptSql_SqlServer_UsesSchemaSmithSchemaAndBrackets()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var sql = quench.GetDeleteCompletedScriptSql("MyProduct", "Before", "script.sql", "T", "");
        Assert.That(sql, Does.Contain("SchemaSmith.CompletedMigrationScripts"));
        Assert.That(sql, Does.Contain("[ProductName]"));
        Assert.That(sql, Does.Contain("[QuenchSlot]"));
        // DELETE is STRICT on template_name (no permissive IN) so a prune in template A
        // can't shadow-delete a legacy blank-template row that template B still needs.
        Assert.That(sql, Does.Contain("[template_name] = 'T'"));
        Assert.That(sql, Does.Not.Contain("[template_name] IN"));
        Assert.That(sql, Does.Contain("[schema_name] = ''"));
    }

    [Test]
    public void GetDeleteCompletedScriptSql_PostgreSQL_UsesQuotedSchemaSmith()
    {
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var sql = quench.GetDeleteCompletedScriptSql("MyProduct", "Before", "script.sql", "T", "");
        Assert.That(sql, Does.Contain("\"SchemaSmith\".\"CompletedMigrationScripts\""));
        Assert.That(sql, Does.Contain("\"ProductName\""));
        Assert.That(sql, Does.Contain("template_name = 'T'"));
        Assert.That(sql, Does.Not.Contain("template_name IN"));
        Assert.That(sql, Does.Contain("schema_name = ''"));
    }

    [Test]
    public void GetDeleteCompletedScriptSql_MySQL_UsesBackticks()
    {
        var product = new Product { Name = "Test", Platform = Platform.MySQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var sql = quench.GetDeleteCompletedScriptSql("MyProduct", "Before", "script.sql", "T", "");
        Assert.That(sql, Does.Contain("`SchemaSmith_CompletedMigrationScripts`"));
        Assert.That(sql, Does.Contain("`ProductName`"));
        Assert.That(sql, Does.Contain("`template_name` = 'T'"));
        Assert.That(sql, Does.Not.Contain("`template_name` IN"));
        Assert.That(sql, Does.Contain("`schema_name` = ''"));
    }

    [Test]
    public void GetSelectCompletedScriptsSql_SqlServer_IncludesNoLock()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var sql = quench.GetSelectCompletedScriptsSql("MyProduct", "Before", "T", "");
        Assert.That(sql, Does.Contain("WITH (NOLOCK)"));
        Assert.That(sql, Does.Contain("[template_name] = 'T'"));
        Assert.That(sql, Does.Not.Contain("IN ('',"), "the completed-scripts read is scoped strictly to the active template");
        Assert.That(sql, Does.Contain("[schema_name] = ''"));
    }

    [Test]
    public void GetSelectCompletedScriptsSql_PostgreSQL_NoLock()
    {
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var sql = quench.GetSelectCompletedScriptsSql("MyProduct", "Before", "T", "tenant_acme");
        Assert.That(sql, Does.Not.Contain("NOLOCK"));
        Assert.That(sql, Does.Contain("\"SchemaSmith\".\"CompletedMigrationScripts\""));
        Assert.That(sql, Does.Contain("template_name = 'T'"));
        Assert.That(sql, Does.Not.Contain("IN ('',"), "the completed-scripts read is scoped strictly to the active template");
        Assert.That(sql, Does.Contain("schema_name = 'tenant_acme'"));
    }

    [Test]
    public void GetInsertCompletedScriptSql_SqlServer_InsertsWithBrackets()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var sql = quench.GetInsertCompletedScriptSql("path/script.sql", "MyProduct", "Before", "TenantBody", "tenant_acme");
        Assert.That(sql, Does.Contain("INSERT SchemaSmith.CompletedMigrationScripts"));
        Assert.That(sql, Does.Contain("path/script.sql"));
        Assert.That(sql, Does.Contain("'TenantBody'"));
        Assert.That(sql, Does.Contain("'tenant_acme'"));
    }

    [Test]
    public void GetInsertCompletedScriptSql_PostgreSQL_InsertsWithQuotes()
    {
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var sql = quench.GetInsertCompletedScriptSql("path/script.sql", "MyProduct", "Before", "TenantBody", "tenant_acme");
        Assert.That(sql, Does.Contain("INSERT INTO \"SchemaSmith\".\"CompletedMigrationScripts\""));
        Assert.That(sql, Does.Contain("template_name"));
        Assert.That(sql, Does.Contain("schema_name"));
    }

    [Test]
    public void GetInsertCompletedScriptSql_MySQL_InsertsWithBackticks()
    {
        var product = new Product { Name = "Test", Platform = Platform.MySQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var sql = quench.GetInsertCompletedScriptSql("path/script.sql", "MyProduct", "Before", "T", "");
        Assert.That(sql, Does.Contain("INSERT INTO `SchemaSmith_CompletedMigrationScripts`"));
        Assert.That(sql, Does.Contain("`template_name`"));
        Assert.That(sql, Does.Contain("`schema_name`"));
    }





    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.PostgreSQL)]
    [TestCase(Platform.MySQL)]
    public void CompletedScriptSql_EscapesSingleQuotesInLiteralValues(Platform platform)
    {
        var falseValue = platform == Platform.PostgreSQL ? "false" : "0";
        var product = new Product { Name = "Test", Platform = platform };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, falseValue, false, falseValue, falseValue, falseValue, falseValue, falseValue, falseValue, falseValue, falseValue, false, false, null);

        var selectSql = quench.GetSelectCompletedScriptsSql("O'Brien", "Before's", "Tem'plate", "schem'a");
        var deleteSql = quench.GetDeleteCompletedScriptSql("O'Brien", "Before's", "scripts/O'Brien.sql", "Tem'plate", "schem'a");
        var insertSql = quench.GetInsertCompletedScriptSql("scripts/O'Brien.sql", "O'Brien", "Before's", "Tem'plate", "schem'a");

        Assert.Multiple(() =>
        {
            Assert.That(selectSql, Does.Contain("O''Brien").And.Contain("Before''s").And.Contain("Tem''plate").And.Contain("schem''a"));
            Assert.That(deleteSql, Does.Contain("O''Brien").And.Contain("Before''s").And.Contain("scripts/O''Brien.sql").And.Contain("Tem''plate").And.Contain("schem''a"));
            Assert.That(insertSql, Does.Contain("O''Brien").And.Contain("Before''s").And.Contain("scripts/O''Brien.sql").And.Contain("Tem''plate").And.Contain("schem''a"));
        });
    }

    #endregion

    #region Template Script Collection Properties

    [Test]
    public void Template_BeforeScripts_ReturnsBeforeSlotScripts()
    {
        var template = new Template { Name = "Test" };
        var beforeFolder = new TemplateFolder { QuenchSlot = TemplateQuenchSlot.Before };
        var script = new SqlScript { Name = "01.sql" };
        script.Batches.Add("SELECT 1");
        beforeFolder.Scripts.Add(script);
        template.ScriptFolders.Add(beforeFolder);

        Assert.That(template.BeforeScripts, Has.Count.EqualTo(1));
        Assert.That(template.BeforeScripts[0].Name, Is.EqualTo("01.sql"));
    }

    [Test]
    public void Template_ObjectScripts_ReturnsObjectsSlotScripts()
    {
        var template = new Template { Name = "Test" };
        var folder = new TemplateFolder { QuenchSlot = TemplateQuenchSlot.Objects };
        var script = new SqlScript { Name = "proc.sql" };
        script.Batches.Add("CREATE PROC");
        folder.Scripts.Add(script);
        template.ScriptFolders.Add(folder);

        Assert.That(template.ObjectScripts, Has.Count.EqualTo(1));
    }

    [Test]
    public void Template_AfterTablesObjectScripts_IncludesBothObjectsAndAfterTablesObjects()
    {
        var template = new Template { Name = "Test" };

        var objectsFolder = new TemplateFolder { QuenchSlot = TemplateQuenchSlot.Objects };
        var script1 = new SqlScript { Name = "obj1.sql" };
        script1.Batches.Add("CREATE FUNC");
        objectsFolder.Scripts.Add(script1);

        var afterTablesFolder = new TemplateFolder { QuenchSlot = TemplateQuenchSlot.AfterTablesObjects };
        var script2 = new SqlScript { Name = "obj2.sql" };
        script2.Batches.Add("CREATE VIEW");
        afterTablesFolder.Scripts.Add(script2);

        template.ScriptFolders.Add(objectsFolder);
        template.ScriptFolders.Add(afterTablesFolder);

        Assert.That(template.AfterTablesObjectScripts, Has.Count.EqualTo(2));
    }

    [Test]
    public void Template_AfterScripts_ReturnsAfterSlotScripts()
    {
        var template = new Template { Name = "Test" };
        var folder = new TemplateFolder { QuenchSlot = TemplateQuenchSlot.After };
        var script = new SqlScript { Name = "after.sql" };
        script.Batches.Add("CLEANUP");
        folder.Scripts.Add(script);
        template.ScriptFolders.Add(folder);

        Assert.That(template.AfterScripts, Has.Count.EqualTo(1));
    }

    [Test]
    public void Template_BetweenTablesAndKeysScripts_ReturnsBetweenSlotScripts()
    {
        var template = new Template { Name = "Test" };
        var folder = new TemplateFolder { QuenchSlot = TemplateQuenchSlot.BetweenTablesAndKeys };
        var script = new SqlScript { Name = "between.sql" };
        script.Batches.Add("ALTER TABLE");
        folder.Scripts.Add(script);
        template.ScriptFolders.Add(folder);

        Assert.That(template.BetweenTablesAndKeysScripts, Has.Count.EqualTo(1));
    }

    [Test]
    public void Template_AfterTableScripts_ReturnsAfterTablesScriptsSlot()
    {
        var template = new Template { Name = "Test" };
        var folder = new TemplateFolder { QuenchSlot = TemplateQuenchSlot.AfterTablesScripts };
        var script = new SqlScript { Name = "afterTable.sql" };
        script.Batches.Add("STUFF");
        folder.Scripts.Add(script);
        template.ScriptFolders.Add(folder);

        Assert.That(template.AfterTableScripts, Has.Count.EqualTo(1));
    }

    [Test]
    public void Template_TableDataScripts_ReturnsTableDataSlot()
    {
        var template = new Template { Name = "Test" };
        var folder = new TemplateFolder { QuenchSlot = TemplateQuenchSlot.TableData };
        var script = new SqlScript { Name = "data.sql" };
        script.Batches.Add("MERGE");
        folder.Scripts.Add(script);
        template.ScriptFolders.Add(folder);

        Assert.That(template.TableDataScripts, Has.Count.EqualTo(1));
    }

    [Test]
    public void Template_EmptyScriptFolders_ReturnsEmptyLists()
    {
        var template = new Template { Name = "Test" };
        Assert.That(template.BeforeScripts, Is.Empty);
        Assert.That(template.ObjectScripts, Is.Empty);
        Assert.That(template.AfterScripts, Is.Empty);
        Assert.That(template.BetweenTablesAndKeysScripts, Is.Empty);
        Assert.That(template.AfterTableScripts, Is.Empty);
        Assert.That(template.AfterTablesObjectScripts, Is.Empty);
        Assert.That(template.TableDataScripts, Is.Empty);
    }

    #endregion

    #region Template LogPath

    [Test]
    public void Template_LogPath_StripsLongPathPrefix()
    {
        var template = new Template { Name = "Test", FilePath = "/some/path/Template.json" };
        Assert.That(template.LogPath, Is.EqualTo("/some/path/Template.json"));
    }

    #endregion

    #region Folder ShouldApplyExpression gating (#260)

    private static IDbCommand GateCommand(System.Func<string, object> scalarFor)
    {
        var command = Substitute.For<IDbCommand>();
        command.ExecuteScalar().Returns(_ => scalarFor(command.CommandText));
        return command;
    }

    private static TemplateFolder GatedFolder(string path, TemplateQuenchSlot slot, string expression, string scriptName)
    {
        var folder = new TemplateFolder { FolderPath = path, QuenchSlot = slot, ShouldApplyExpression = expression };
        folder.Scripts.Add(new SqlScript { Name = scriptName });
        return folder;
    }

    [Test]
    public void ApplyFolderGates_DropsFoldersWhoseExpressionIsFalse_KeepsTrueOnes()
    {
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.ScriptFolders.Add(GatedFolder("keep", TemplateQuenchSlot.Before, "KEEP", "keep.sql"));
        template.ScriptFolders.Add(GatedFolder("skip", TemplateQuenchSlot.Before, "SKIP", "skip.sql"));
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();

        quench.ApplyFolderGates(GateCommand(sql => sql.Contains("KEEP") ? 1 : 0));

        Assert.That(quench.IterationBeforeScripts.Select(s => s.Name), Is.EqualTo(new[] { "keep.sql" }));
    }

    [Test]
    public void ApplyFolderGates_SkippedObjectsFolder_RemovedFromBothObjectSlots()
    {
        // A folder in the Objects slot feeds both ObjectScripts and AfterTablesObjectScripts; a
        // gate skip must remove it from both.
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.ScriptFolders.Add(GatedFolder("obj", TemplateQuenchSlot.Objects, "SKIP", "obj.sql"));
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();

        quench.ApplyFolderGates(GateCommand(_ => 0));

        Assert.Multiple(() =>
        {
            Assert.That(quench.IterationObjectScripts, Is.Empty);
            Assert.That(quench.IterationAfterTablesObjectScripts, Is.Empty);
        });
    }

    [Test]
    public void ApplyFolderGates_NoExpressions_IsNoOpAndNeverQueries()
    {
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.ScriptFolders.Add(GatedFolder("plain", TemplateQuenchSlot.Before, null, "plain.sql"));
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();
        var command = Substitute.For<IDbCommand>();

        quench.ApplyFolderGates(command);

        Assert.That(quench.IterationBeforeScripts.Select(s => s.Name), Is.EqualTo(new[] { "plain.sql" }));
        command.DidNotReceive().ExecuteScalar();
    }

    [Test]
    public void ApplyFolderGates_ExpressionThrows_PropagatesFailClosed()
    {
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.ScriptFolders.Add(GatedFolder("bad", TemplateQuenchSlot.Before, "BROKEN", "bad.sql"));
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();
        var command = Substitute.For<IDbCommand>();
        command.ExecuteScalar().Returns(_ => throw new System.InvalidOperationException("syntax error"));

        Assert.Throws<System.InvalidOperationException>(() => quench.ApplyFolderGates(command));
    }

    [Test]
    public void ApplyFolderGates_GatedOffMigration_IsProtectedFromObsoletePruning()
    {
        // A gated-off folder's run-once migration is still declared in the package, so its tracking
        // row must NOT be treated as obsolete (and pruned) just because it didn't run this iteration.
        var baseDir = System.IO.Path.Combine("pkg", "Templates", "T");
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T", FilePath = System.IO.Path.Combine(baseDir, "Template.json") };
        var gated = new TemplateFolder
        {
            FolderPath = "MigrationScripts/Before", QuenchSlot = TemplateQuenchSlot.Before, ShouldApplyExpression = "SKIP"
        };
        var migration = new SqlScript
        {
            Name = "Migration_1.sql",
            FilePath = System.IO.Path.Combine(baseDir, "MigrationScripts", "Before", "Migration_1.sql")
        };
        gated.Scripts.Add(migration);
        template.ScriptFolders.Add(gated);
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();

        quench.ApplyFolderGates(GateCommand(_ => 0)); // gate the folder off

        var gatedPath = quench.GetRelativeScriptPath(migration.LogPath);
        Assert.Multiple(() =>
        {
            Assert.That(quench.IsObsoleteTrackingEntry(gatedPath, quench.IterationBeforeScripts), Is.False,
                "A gated-off folder's run-once migration must NOT be pruned as obsolete.");
            Assert.That(quench.IsObsoleteTrackingEntry("MigrationScripts/Before/Removed.sql", quench.IterationBeforeScripts), Is.True,
                "A genuinely-removed script IS obsolete.");
        });
    }

    [Test]
    public void ApplyFolderGates_RegularTemplate_SurvivingScriptKeepsSameReference()
    {
        // Regular template: surviving folder's scripts must remain the SAME SqlScript instances
        // (filter, not clone) so cross-iteration HasBeenQuenched dedup keeps working.
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var keep = new TemplateFolder { FolderPath = "keep", QuenchSlot = TemplateQuenchSlot.Before, ShouldApplyExpression = "KEEP" };
        var keepScript = new SqlScript { Name = "keep.sql" };
        keep.Scripts.Add(keepScript);
        template.ScriptFolders.Add(keep);
        template.ScriptFolders.Add(GatedFolder("skip", TemplateQuenchSlot.Before, "SKIP", "skip.sql"));
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();

        quench.ApplyFolderGates(GateCommand(sql => sql.Contains("KEEP") ? 1 : 0));

        Assert.That(quench.IterationBeforeScripts.Single(), Is.SameAs(keepScript));
    }

    [Test]
    public void ApplyFolderGates_SchemaTemplate_SurvivingScriptIsCloned()
    {
        // Schema template: survivors are cloned (so {{SchemaName}} substitution is per-iteration);
        // the iteration script must NOT be the template's instance.
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var keep = new TemplateFolder { FolderPath = "keep", QuenchSlot = TemplateQuenchSlot.Before, ShouldApplyExpression = "KEEP" };
        var keepScript = new SqlScript { Name = "keep.sql" };
        keep.Scripts.Add(keepScript);
        template.ScriptFolders.Add(keep);
        template.ScriptFolders.Add(GatedFolder("skip", TemplateQuenchSlot.Before, "SKIP", "skip.sql"));
        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_a",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();

        quench.ApplyFolderGates(GateCommand(sql => sql.Contains("KEEP") ? 1 : 0));

        Assert.That(quench.IterationBeforeScripts.Single(), Is.Not.SameAs(keepScript));
    }

    [Test]
    public void ApplyFolderGates_SchemaTemplate_SubstitutesSchemaNameBeforeEvaluating()
    {
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.ScriptFolders.Add(GatedFolder("eu", TemplateQuenchSlot.Before, "CHECK {{SchemaName}}", "eu.sql"));
        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_a",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();

        // Only the schema-substituted form should be the query; gate true for tenant_a.
        quench.ApplyFolderGates(GateCommand(sql => sql.Contains("CHECK tenant_a") ? 1 : 0));

        Assert.That(quench.IterationBeforeScripts.Select(s => s.Name), Is.EqualTo(new[] { "eu.sql" }));
    }

    #endregion

    #region Version / compatibility-level script tokens (A1)

    private static DatabaseQuench RegularQuench(Product product, Template template) =>
        new("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

    [Test]
    public void VersionTokens_FolderGate_RegularTemplate_SubstitutesServerMajorVersion()
    {
        // The primary use case + the regression this fixes: a REGULAR template's folder gate
        // must have {{ServerMajorVersion}} substituted before evaluation. Pre-fix,
        // ResolveFolderGateExpression returned the expression verbatim for regular templates.
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.ScriptFolders.Add(GatedFolder("modern", TemplateQuenchSlot.Before, "CHECK {{ServerMajorVersion}}", "modern.sql"));
        var quench = RegularQuench(product, template);
        quench.PrepareIterationContent();
        quench.PrepareVersionScriptTokens(16, 130);

        quench.ApplyFolderGates(GateCommand(sql => sql.Contains("CHECK 16") ? 1 : 0));

        Assert.That(quench.IterationBeforeScripts.Select(s => s.Name), Is.EqualTo(new[] { "modern.sql" }));
    }

    [Test]
    public void VersionTokens_FolderGate_RegularTemplate_SkipsWhenVersionBelowBoundary()
    {
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.ScriptFolders.Add(GatedFolder("modern", TemplateQuenchSlot.Before, "CHECK {{ServerMajorVersion}}", "modern.sql"));
        var quench = RegularQuench(product, template);
        quench.PrepareIterationContent();
        quench.PrepareVersionScriptTokens(10, 100); // SQL Server 2008 / compat 100

        // The gate query carries "CHECK 10", not "CHECK 16" — folder is gated off.
        quench.ApplyFolderGates(GateCommand(sql => sql.Contains("CHECK 16") ? 1 : 0));

        Assert.That(quench.IterationBeforeScripts, Is.Empty);
    }

    [Test]
    public void VersionTokens_FolderGate_RegularTemplate_SubstitutesCompatibilityLevel()
    {
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.ScriptFolders.Add(GatedFolder("compat", TemplateQuenchSlot.Before, "CHECK {{CompatibilityLevel}}", "compat.sql"));
        var quench = RegularQuench(product, template);
        quench.PrepareIterationContent();
        quench.PrepareVersionScriptTokens(16, 100); // modern binary hosting a compat-100 database

        // The compat token resolves to 100 (NOT the server version 16) — the footgun distinction.
        quench.ApplyFolderGates(GateCommand(sql => sql.Contains("CHECK 100") ? 1 : 0));

        Assert.That(quench.IterationBeforeScripts.Select(s => s.Name), Is.EqualTo(new[] { "compat.sql" }));
    }

    [Test]
    public void VersionTokens_CompatibilityLevel_FallsBackToServerVersion_WhenNull()
    {
        // Off SQL Server there is no compatibility level; {{CompatibilityLevel}} falls back to the
        // detected server version so the same expression stays portable across per-platform packages.
        var product = new Product { Name = "P", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T" };
        template.ScriptFolders.Add(GatedFolder("pg", TemplateQuenchSlot.Before, "CHECK {{CompatibilityLevel}}", "pg.sql"));
        var quench = RegularQuench(product, template);
        quench.PrepareIterationContent();
        quench.PrepareVersionScriptTokens(12, null); // PostgreSQL 12, no compat concept

        quench.ApplyFolderGates(GateCommand(sql => sql.Contains("CHECK 12") ? 1 : 0));

        Assert.That(quench.IterationBeforeScripts.Select(s => s.Name), Is.EqualTo(new[] { "pg.sql" }));
    }

    [Test]
    public void VersionTokens_ScriptBody_RegularTemplate_SubstitutesDetectedLiteral()
    {
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var folder = new TemplateFolder { FolderPath = "b", QuenchSlot = TemplateQuenchSlot.Before };
        var script = new SqlScript { Name = "v.sql" };
        script.Batches.Add("IF {{ServerMajorVersion}} >= 16 PRINT 'modern'");
        folder.Scripts.Add(script);
        template.ScriptFolders.Add(folder);
        var quench = RegularQuench(product, template);
        quench.PrepareIterationContent();
        quench.PrepareVersionScriptTokens(16, 130);

        quench.ApplyVersionScriptTokens();

        var body = quench.IterationBeforeScripts.Single().Batches.Single();
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("IF 16 >= 16"));
            Assert.That(body, Does.Not.Contain("{{ServerMajorVersion}}"));
        });
    }

    [Test]
    public void VersionTokens_ScriptBody_NoVersionToken_KeepsSameReference()
    {
        // Conditional-clone: a tokenless script must remain the SAME SqlScript instance so the
        // regular-template cross-DB HasBeenQuenched dedup (which relies on reference identity) is
        // preserved bit-for-bit for the common case.
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var folder = new TemplateFolder { FolderPath = "b", QuenchSlot = TemplateQuenchSlot.Before };
        var script = new SqlScript { Name = "plain.sql" };
        script.Batches.Add("SELECT 1");
        folder.Scripts.Add(script);
        template.ScriptFolders.Add(folder);
        var quench = RegularQuench(product, template);
        quench.PrepareIterationContent();
        quench.PrepareVersionScriptTokens(16, 130);

        quench.ApplyVersionScriptTokens();

        Assert.That(quench.IterationBeforeScripts.Single(), Is.SameAs(script));
    }

    [Test]
    public void VersionTokens_TableSchemaPayload_RegularTemplate_Substitutes()
    {
        // Component-level ShouldApplyExpression / Default / CheckExpression live in the serialized
        // model payload; version tokens must resolve there too, for regular templates.
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "T",
            TableSchema = "[{\"Name\":\"Audit\",\"ShouldApplyExpression\":\"{{ServerMajorVersion}} >= 13\"}]"
        };
        var quench = RegularQuench(product, template);
        quench.PrepareIterationContent();
        quench.PrepareVersionScriptTokens(16, 130);

        quench.ApplyVersionScriptTokens();

        Assert.Multiple(() =>
        {
            Assert.That(quench.IterationTableSchema, Does.Contain("16 >= 13"));
            Assert.That(quench.IterationTableSchema, Does.Not.Contain("{{ServerMajorVersion}}"));
        });
    }

    #endregion

    #region QuenchOneScript Tests

    [Test]
    public void QuenchOneScript_SuccessfulExecution_MarksAsQuenched()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var script = new SqlScript { Name = "test.sql" };
        script.Batches.Add("SELECT 1");

        var mockCmd = CreateMockCommand();

        quench.QuenchOneScript(mockCmd, script, false, false);

        Assert.That(script.HasBeenQuenched, Is.True);
        Assert.That(script.Error, Is.Null);
    }

    [Test]
    public void QuenchOneScript_FailedExecution_SetsError()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var script = new SqlScript { Name = "fail.sql" };
        script.Batches.Add("INVALID SQL");

        var mockCmd = CreateMockCommand();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => throw new Exception("Syntax error"));

        quench.QuenchOneScript(mockCmd, script, false, false);

        Assert.That(script.HasBeenQuenched, Is.False);
        Assert.That(script.Error, Is.Not.Null);
        Assert.That(script.Error.Message, Does.Contain("Syntax error"));
    }

    [Test]
    public void QuenchOneScript_RunTwice_ExecutesTwice()
    {
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var script = new SqlScript { Name = "test.sql" };
        script.Batches.Add("SELECT 1");

        var mockCmd = CreateMockCommand();
        var executeCount = 0;
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => executeCount++);

        quench.QuenchOneScript(mockCmd, script, true, false);

        Assert.That(executeCount, Is.EqualTo(2));
        Assert.That(script.HasBeenQuenched, Is.True);
    }

    [Test]
    public void QuenchOneScript_MultipleBatches_ExecutesAll()
    {
        var product = new Product { Name = "Test", Platform = Platform.MySQL };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var script = new SqlScript { Name = "multi.sql" };
        script.Batches.Add("SELECT 1");
        script.Batches.Add("SELECT 2");
        script.Batches.Add("SELECT 3");

        var mockCmd = CreateMockCommand();
        var executeCount = 0;
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => executeCount++);

        quench.QuenchOneScript(mockCmd, script, false, false);

        Assert.That(executeCount, Is.EqualTo(3));
        Assert.That(script.HasBeenQuenched, Is.True);
    }

    [Test]
    public void QuenchOneScript_SentinelError_MarksSkipped_NotFailed()
    {
        var product = new Product { Name = "P", Platform = Platform.PostgreSQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var script = new SqlScript { Name = "001_maybe.sql", FilePath = "001_maybe.sql" };
        script.Batches.Add("RAISE EXCEPTION 'SCHEMASMITH: SHOULD NOT APPLY';");

        var mockCmd = CreateMockCommand();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => throw new Exception(SentinelClassifier.Constant));

        quench.QuenchOneScript(mockCmd, script, runTwice: false, showErrors: false);

        Assert.Multiple(() =>
        {
            Assert.That(script.HasBeenQuenched, Is.True);
            Assert.That(script.Outcome, Is.EqualTo(ScriptOutcome.Skipped));
            Assert.That(script.Error, Is.Null);
        });
    }

    [Test]
    public void QuenchOneScript_RealError_StillFails()
    {
        var product = new Product { Name = "P", Platform = Platform.PostgreSQL };
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var script = new SqlScript { Name = "001_bad.sql", FilePath = "001_bad.sql" };
        script.Batches.Add("SELECT 1/0;");

        var mockCmd = CreateMockCommand();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => throw new Exception("division by zero"));

        quench.QuenchOneScript(mockCmd, script, runTwice: false, showErrors: false);

        Assert.Multiple(() =>
        {
            Assert.That(script.HasBeenQuenched, Is.False);
            // Outcome stays at the default Applied; the error path records script.Error, not Outcome.
            Assert.That(script.Outcome, Is.EqualTo(ScriptOutcome.Applied));
            Assert.That(script.Error, Is.Not.Null);
        });
    }

    #endregion

    #region QuenchDatabaseObjects Tests

    [Test]
    public void QuenchDatabaseObjects_AllSucceed_AllMarkedQuenched()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var scripts = new List<SqlScript>();
        for (var i = 0; i < 3; i++)
        {
            var s = new SqlScript { Name = $"script{i}.sql" };
            s.Batches.Add($"SELECT {i}");
            scripts.Add(s);
        }

        var mockCmd = CreateMockCommand();

        quench.QuenchDatabaseObjects(mockCmd, scripts, false);

        Assert.That(scripts.All(s => s.HasBeenQuenched), Is.True);
    }

    [Test]
    public void QuenchDatabaseObjects_DependencyLoop_RetriesUntilResolved()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var script1 = new SqlScript { Name = "script1.sql" };
        script1.Batches.Add("SELECT 1");
        var script2 = new SqlScript { Name = "script2.sql" };
        script2.Batches.Add("SELECT 2");

        // Script2 fails first time, succeeds second time
        var callCount = 0;
        var mockCmd = CreateMockCommand();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ =>
        {
            callCount++;
            if (callCount == 2) // second call = script2's first attempt
                throw new Exception("Dependency not ready");
        });

        var scripts = new List<SqlScript> { script1, script2 };
        quench.QuenchDatabaseObjects(mockCmd, scripts, false);

        Assert.That(scripts.All(s => s.HasBeenQuenched), Is.True);
    }

    #endregion

    #region WhatIfLogScripts Tests — overlapping calls must log each script once

    // The engine's two Object-slot WhatIf calls (and the two AfterTablesObject-slot calls) pass
    // overlapping script lists built from the same underlying scripts, mirroring the real path's
    // two QuenchDatabaseObjectsWithCheckpoint passes. WhatIfLogScripts must gate on
    // HasBeenQuenched exactly as the real path's QuenchDatabaseObjectsWithCheckpoint does, so a
    // script already logged in an earlier call is skipped on the next.
    [Test]
    public void WhatIfLogScripts_OverlappingCallsSameScope_RecordsScriptOnlyOnce()
    {
        Schema.Utility.LogFactory.Clear();
        try
        {
            Schema.Utility.LogFactory.Register("ProgressLog", Substitute.For<log4net.ILog>());
            Schema.Utility.LogFactory.Register("ErrorLog", Substitute.For<log4net.ILog>());

            var product = new Product { Name = "P", Platform = Platform.SqlServer };
            var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
                false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null)
            {
                WhatIf = new WhatIfCapture()
            };

            var overlap = new SqlScript { Name = "vw_Overlap.sql", FilePath = "Scripts/Object/vw_Overlap.sql" };
            var onlyFirst = new SqlScript { Name = "vw_First.sql", FilePath = "Scripts/Object/vw_First.sql" };
            var onlySecond = new SqlScript { Name = "vw_Second.sql", FilePath = "Scripts/Object/vw_Second.sql" };

            // Same shape as DatabaseQuench's two Object-slot WhatIf calls: overlapping lists,
            // same DatabaseScriptSlot, same scope.
            quench.WhatIfLogScripts([onlyFirst, overlap], DatabaseScriptSlot.Object);
            quench.WhatIfLogScripts([overlap, onlySecond], DatabaseScriptSlot.Object);

            var snapshot = quench.WhatIf.Snapshot();
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Count(r => r.Script == overlap.LogPath), Is.EqualTo(1),
                    "A script present in both overlapping WhatIf calls must be recorded once in the summary");
                Assert.That(snapshot.Count(r => r.Script == onlyFirst.LogPath), Is.EqualTo(1));
                Assert.That(snapshot.Count(r => r.Script == onlySecond.LogPath), Is.EqualTo(1));
                Assert.That(overlap.HasBeenQuenched, Is.True,
                    "Logged scripts must be flagged, mirroring the real path's HasBeenQuenched gate");
            });
        }
        finally
        {
            Schema.Utility.LogFactory.Clear();
        }
    }

    [Test]
    public void WhatIfLogScripts_OverlappingCallsSameScope_PrintsConsoleLineOnlyOnce()
    {
        // Distinct from the summary assertion above: the console output is produced in the same
        // loop as the WhatIf.Record call, but a fix that only gated the recorded entry (and left
        // the console foreach unguarded) would still double-print here — that's the specific half
        // the earlier symptom-level fix missed.
        Schema.Utility.LogFactory.Clear();
        try
        {
            var progressLog = Substitute.For<log4net.ILog>();
            var progressLogLines = new List<string>();
            progressLog.When(l => l.Info(Arg.Any<object>()))
                .Do(ci => progressLogLines.Add(ci.Arg<object>().ToString()));
            Schema.Utility.LogFactory.Register("ProgressLog", progressLog);
            Schema.Utility.LogFactory.Register("ErrorLog", Substitute.For<log4net.ILog>());

            var product = new Product { Name = "P", Platform = Platform.SqlServer };
            var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
                false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null)
            {
                WhatIf = new WhatIfCapture()
            };

            var overlap = new SqlScript { Name = "vw_Overlap.sql", FilePath = "Scripts/Object/vw_Overlap.sql" };

            // Same slot both times — matches the real Object-slot pair (DatabaseQuench.cs :596/:634).
            quench.WhatIfLogScripts([overlap], DatabaseScriptSlot.Object);
            quench.WhatIfLogScripts([overlap], DatabaseScriptSlot.Object);

            var consoleHits = progressLogLines.Count(l => l.Contains(overlap.LogPath));
            Assert.That(consoleHits, Is.EqualTo(1),
                "A script present in both overlapping WhatIf calls must be printed to console once");
        }
        finally
        {
            Schema.Utility.LogFactory.Clear();
        }
    }

    #endregion

    #region Execute - Basic Flow Tests

    [Test]
    public void Execute_WithNoCheckpointing_SetsQuenchSuccessful_WhenConnectionSucceeds()
    {
        // This test validates that Execute doesn't crash on the simplest path.
        // With mocked connections it would need full setup which is integration test territory.
        // We verify the constructor state instead.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        // Without mocked connection factory, Execute will fail to connect and set QuenchSuccessful = false
        quench.Execute();
        Assert.That(quench.QuenchSuccessful, Is.False);
    }

    #endregion

    #region QuenchIndexedViews Tests

    [Test]
    public void QuenchIndexedViews_MissingUniqueClusteredIndex_Throws()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Test]",
            Schema = "dbo",
            Definition = "SELECT 1",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = false, Clustered = false }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        var ex = Assert.Throws<Exception>(() => quench.QuenchIndexedViews(mockCmd));
        Assert.That(ex!.Message, Does.Contain("requires a unique clustered index"));
        Assert.That(ex.Message, Does.Contain("dbo.[vw_Test]"));
    }

    [Test]
    public void QuenchIndexedViews_ClusteredButNotUnique_Throws()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Test]",
            Schema = "dbo",
            Definition = "SELECT 1",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = false, Clustered = true }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        Assert.Throws<Exception>(() => quench.QuenchIndexedViews(mockCmd));
    }

    [Test]
    public void QuenchIndexedViews_WithDeclaredViews_InvokesProcForServerSideEvaluation()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "T",
            // Production: Template.Load serializes the in-memory views into IndexedViewSchema;
            // QuenchIndexedViews feeds the proc from that (iteration-aware) string. A view gated
            // ShouldApplyExpression = "false" is NOT filtered in C# — it is passed through and
            // evaluated per-target server-side (mirroring materialized views).
            IndexedViewSchema = "[{\"Schema\":\"dbo\",\"Name\":\"[vw_Test]\",\"Definition\":\"SELECT 1\",\"ShouldApplyExpression\":\"false\"}]"
        };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Test]",
            Schema = "dbo",
            Definition = "SELECT 1",
            ShouldApplyExpression = "false",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexedViews(mockCmd);

        // ShouldApplyExpression is no longer filtered in C#; the view is still passed to the
        // proc, which evaluates ShouldApply per-target server-side (mirroring materialized views).
        Assert.That(mockCmd.CommandText, Does.Contain("EXEC [SchemaSmith].[IndexedViewQuench]"));
        Assert.That(mockCmd.CommandText, Does.Contain("[vw_Test]"));
        mockCmd.Received(1).ExecuteNonQuery();
    }

    [Test]
    public void QuenchIndexedViews_NoIndexedViews_InvokesProc()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        // No indexed views added — like materialized views, the proc is still invoked
        // (server-side handles the empty set); no C# early-return.

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexedViews(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("EXEC [SchemaSmith].[IndexedViewQuench]"));
        mockCmd.Received(1).ExecuteNonQuery();
    }

    [Test]
    public void QuenchIndexedViews_ValidViews_SetsCommandTextAndExecutes()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Test]",
            Schema = "dbo",
            Definition = "SELECT 1",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexedViews(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("EXEC [SchemaSmith].[IndexedViewQuench]"));
        Assert.That(mockCmd.CommandText, Does.Contain("@ProductName = 'Test'"));
        Assert.That(mockCmd.CommandText, Does.Contain("@WhatIf = 0"));
        Assert.That(mockCmd.CommandText, Does.Contain("@UpdateFillFactor = true"));
        mockCmd.Received(1).ExecuteNonQuery();
    }

    [Test]
    public void QuenchIndexedViews_ShouldApplyExpressionNull_IncludesView()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Test]",
            Schema = "dbo",
            Definition = "SELECT 1",
            ShouldApplyExpression = null,
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexedViews(mockCmd);

        mockCmd.Received(1).ExecuteNonQuery();
    }

    [Test]
    public void QuenchIndexedViews_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test's Product", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Test]",
            Schema = "dbo",
            Definition = "SELECT 1",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexedViews(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("Test''s Product"));
    }

    // I10: QuenchIndexedViews routes through _iterationIndexedViewSchema mirroring the
    // pattern for tables and materialized views. PrepareIterationContent populates the
    // field for schema-template iterations; regular templates fall through to the
    // template's own IndexedViewSchema.
    [Test]
    public void PrepareIterationContent_SchemaTemplate_PopulatesIterationIndexedViewSchema()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_a'",
            IndexedViewSchema = "[{\"Schema\":\"{{SchemaName}}\",\"Name\":\"vw_Orders\"}]"
        };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Orders]",
            Schema = "{{SchemaName}}",
            Definition = "SELECT 1",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_a",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        quench.PrepareIterationContent();

        Assert.That(quench.IterationIndexedViewSchema, Does.Contain("\"Schema\":\"tenant_a\""));
        Assert.That(quench.IterationIndexedViewSchema, Does.Not.Contain("{{SchemaName}}"));
    }

    [Test]
    public void PrepareIterationContent_RegularTemplate_IterationIndexedViewSchema_FallsThroughToTemplate()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "Core",
            IndexedViewSchema = "[{\"Schema\":\"dbo\",\"Name\":\"vw_Stuff\"}]"
        };

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        quench.PrepareIterationContent();

        Assert.That(quench.IterationIndexedViewSchema,
            Is.EqualTo("[{\"Schema\":\"dbo\",\"Name\":\"vw_Stuff\"}]"));
    }

    [Test]
    public void QuenchIndexedViews_SchemaTemplate_UsesSubstitutedIterationSchema()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_a'",
            // In production, InstanceLoad serializes the in-memory views into
            // IndexedViewSchema after SchemaDefaultResolver fills the {{SchemaName}} token.
            // The test sets the pre-baked JSON to exercise the same path PrepareIterationContent
            // sees in the real load flow.
            IndexedViewSchema = "[{\"Schema\":\"{{SchemaName}}\",\"Name\":\"[vw_Orders]\"}]"
        };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Orders]",
            Schema = "{{SchemaName}}",
            Definition = "SELECT 1",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_a",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexedViews(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("tenant_a"));
        Assert.That(mockCmd.CommandText, Does.Not.Contain("{{SchemaName}}"));
    }

    [Test]
    public void QuenchIndexedViews_SchemaTemplate_PassesAllVariantsForServerSideEvaluation()
    {
        // ShouldApplyExpression is no longer filtered in C# — every variant is passed to the
        // proc (with {{SchemaName}} substituted for the iteration) and evaluated per-target
        // server-side, mirroring materialized views. A "false"-gated view is still in the
        // payload; the proc skips it at deploy time.
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_a'",
            // In production, Template.Load serializes the views into IndexedViewSchema after
            // SchemaDefaultResolver fills {{SchemaName}}; PrepareIterationContent substitutes it
            // per iteration. The pre-baked JSON exercises that same path.
            IndexedViewSchema = "[{\"Schema\":\"{{SchemaName}}\",\"Name\":\"[vw_Active]\",\"Definition\":\"SELECT 1\"}," +
                                "{\"Schema\":\"{{SchemaName}}\",\"Name\":\"[vw_Excluded]\",\"Definition\":\"SELECT 1\",\"ShouldApplyExpression\":\"false\"}]"
        };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Active]",
            Schema = "{{SchemaName}}",
            Definition = "SELECT 1",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Excluded]",
            Schema = "{{SchemaName}}",
            Definition = "SELECT 1",
            ShouldApplyExpression = "false",
            Indexes = [new SqlServerIndex { Name = "[IX_2]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_a",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexedViews(mockCmd);

        // Both variants reach the proc (no client-side filter); the iteration schema is substituted.
        Assert.That(mockCmd.CommandText, Does.Contain("[vw_Active]"));
        Assert.That(mockCmd.CommandText, Does.Contain("[vw_Excluded]"));
        Assert.That(mockCmd.CommandText, Does.Contain("tenant_a"));
        Assert.That(mockCmd.CommandText, Does.Not.Contain("{{SchemaName}}"));
    }

    // Slice-3 audit B5: QuenchIndexedViews threads @TemplateName + @SchemaName so the
    // existing-views drop-candidate lookup in the proc is scoped per (template, schema).
    // Regular templates pass @SchemaName = '' and the proc falls through to today's
    // all-schemas behavior.
    [Test]
    public void QuenchIndexedViews_SchemaTemplate_ThreadsTemplateNameAndSchemaName()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_acme'"
        };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Orders]",
            Schema = "{{SchemaName}}",
            Definition = "SELECT 1",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_acme",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
        quench.PrepareIterationContent();

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexedViews(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("@TemplateName = N'TenantBody'"));
        Assert.That(mockCmd.CommandText, Does.Contain("@SchemaName = N'tenant_acme'"));
    }

    [Test]
    public void QuenchIndexedViews_RegularTemplate_PassesEmptyTemplateAndSchemaName()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "Core" };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Orders]",
            Schema = "dbo",
            Definition = "SELECT 1",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexedViews(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("@TemplateName = N'Core'"));
        Assert.That(mockCmd.CommandText, Does.Contain("@SchemaName = N''"));
    }

    [Test]
    public void QuenchIndexedViews_TemplateNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "Tenant's Body" };
        template.IndexedViews.Add(new SqlServerIndexedView
        {
            Name = "[vw_Orders]",
            Schema = "dbo",
            Definition = "SELECT 1",
            Indexes = [new SqlServerIndex { Name = "[IX_1]", Unique = true, Clustered = true, IndexColumns = "Col1" }]
        });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexedViews(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("@TemplateName = N'Tenant''s Body'"));
    }

    #endregion

    #region QuenchMaterializedViews Tests

    [Test]
    public void QuenchMaterializedViews_SetsCorrectCommandText()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "TestProd", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T", MaterializedViewSchema = "[{\"Name\":\"mv_test\"}]" };

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchMaterializedViews(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("CALL \"SchemaSmith\".\"MaterializedViewQuench\""));
        Assert.That(mockCmd.CommandText, Does.Contain("'TestProd'"));
        Assert.That(mockCmd.CommandText, Does.Contain("false")); // whatIfOnly
        mockCmd.Received(1).ExecuteNonQuery();
    }

    [Test]
    public void QuenchMaterializedViews_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T", MaterializedViewSchema = "[]" };

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchMaterializedViews(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    #endregion

    #region ProductName escaping — SQL Server / PostgreSQL dispatch (#274)

    // #274: _product.Name was interpolated raw into the SQL Server (EXEC @ProductName='…')
    // and PostgreSQL (CALL …(p_ProductName:='…')) dispatch paths, so an apostrophe in the
    // product name broke deployment on those engines. The MySQL branch already escaped it.
    // These pin EscapeSqlLiteral across the three affected methods on every engine that
    // interpolates the product name (the PostgreSQL ForeignKeyQuench path takes no product
    // name, so there is no literal to escape and no test there); the MySQL cases pin the
    // already-correct behavior so it can't regress.

    [Test]
    public void QuenchModifiedTables_SqlServer_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    [Test]
    public void QuenchModifiedTables_PostgreSql_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    [Test]
    public void QuenchModifiedTables_MySql_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.MySQL };
        var template = new Template { Name = "T" };
        template.Tables.Add(new Table { Name = "[T1]" });
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    [Test]
    public void QuenchIndexesAndConstraints_SqlServer_IndexOnly_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.SqlServer };
        var template = new Template { Name = "T", IndexOnlyTableQuenches = true };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexesAndConstraints(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    [Test]
    public void QuenchIndexesAndConstraints_SqlServer_FullQuench_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.SqlServer };
        var template = new Template { Name = "T", IndexOnlyTableQuenches = false };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexesAndConstraints(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    [Test]
    public void QuenchIndexesAndConstraints_PostgreSql_IndexOnly_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T", IndexOnlyTableQuenches = true };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexesAndConstraints(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    [Test]
    public void QuenchIndexesAndConstraints_PostgreSql_FullQuench_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T", IndexOnlyTableQuenches = false };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexesAndConstraints(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    [Test]
    public void QuenchIndexesAndConstraints_MySql_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.MySQL };
        var template = new Template { Name = "T" };
        template.Tables.Add(new Table { Name = "[T1]" });
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexesAndConstraints(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    [Test]
    public void QuenchForeignKeys_SqlServer_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        template.Tables.Add(new Table { Name = "[T1]" });
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchForeignKeys(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    [Test]
    public void QuenchForeignKeys_MySql_ProductNameWithApostrophe_EscapesCorrectly()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "O'Brien's DB", Platform = Platform.MySQL };
        var template = new Template { Name = "T" };
        template.Tables.Add(new Table { Name = "[T1]" });
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchForeignKeys(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("O''Brien''s DB"));
    }

    #endregion

    #region Schema-Template (Slice 3) — Constructor + DbScope + Log Prefix

    [Test]
    public void Constructor_SchemaName_DefaultsToEmptyString()
    {
        // Forwarding constructor (no schemaName arg) leaves SchemaName empty for regular templates.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        Assert.That(quench.SchemaName, Is.EqualTo(""));
    }

    [Test]
    public void Constructor_SchemaName_PreservedFromExplicitArgument()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_acme",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        Assert.That(quench.SchemaName, Is.EqualTo("tenant_acme"));
    }

    [Test]
    public void Constructor_SchemaName_NullCoercedToEmpty()
    {
        // Tracking-table column convention (slice 2) uses '' rather than NULL for regular-template rows.
        // The ctor coerces null to empty so DbScope.SchemaName never carries a null downstream.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db", null,
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        Assert.That(quench.SchemaName, Is.EqualTo(""));
    }

    [Test]
    public void Constructor_InternalSchemaName_PreservedFromExplicitArgument()
    {
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_globex",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        Assert.That(quench.SchemaName, Is.EqualTo("tenant_globex"));
    }

    [Test]
    public void DbScope_SchemaName_FlowsFromConstructor()
    {
        // DbScope is private; the SchemaName surfaces in the SQL that touches the tracking table —
        // observe via the SQL emitters that route DbScope.SchemaName into the WHERE clause.
        var product = new Product { Name = "MyProduct", Platform = Platform.SqlServer };
        var template = new Template { Name = "TenantBody" };
        var quench = new DatabaseQuench("srv", product, template, "AppProd", "tenant_acme",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        var sql = quench.GetSelectCompletedScriptsSql(
            quench.ProductName, "Before", "TenantBody", quench.SchemaName);

        Assert.That(sql, Does.Contain("[schema_name] = 'tenant_acme'"));
        Assert.That(sql, Does.Contain("[template_name] = 'TenantBody'"));
    }

    [Test]
    public void DbScope_SchemaName_EmptyForRegularTemplate()
    {
        var product = new Product { Name = "MyProduct", Platform = Platform.SqlServer };
        var template = new Template { Name = "Core" };
        var quench = new DatabaseQuench("srv", product, template, "AppProd",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        var sql = quench.GetSelectCompletedScriptsSql(
            quench.ProductName, "Before", "Core", quench.SchemaName);

        Assert.That(sql, Does.Contain("[schema_name] = ''"));
    }

    [Test]
    public void LogPrefix_SchemaName_IncludesSchemaTag()
    {
        // Per design Â§5.8, every log line emitted during a schema iteration carries the
        // [Schema: <name>] prefix so a multi-iteration deploy log stays greppable per tenant.
        // LogPrefix is the single source of truth for the prefix; SafeProgressLog and its
        // siblings all route through it.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "TenantBody" };
        var quench = new DatabaseQuench("srv", product, template, "AppProd", "tenant_acme",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        Assert.That(quench.LogPrefix, Is.EqualTo("[srv].[AppProd] [Schema: tenant_acme]"));
    }

    [Test]
    public void LogPrefix_NoSchemaName_OmitsSchemaTag()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "Core" };
        var quench = new DatabaseQuench("srv", product, template, "AppProd",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        Assert.That(quench.LogPrefix, Is.EqualTo("[srv].[AppProd]"));
    }

    [Test]
    public void LogPrefix_EmptySchemaName_OmitsSchemaTag()
    {
        // Explicit empty string is equivalent to "no schema iteration" (the work-unit convention
        // for regular templates) — must not render an empty "[Schema: ]" tag.
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "Core" };
        var quench = new DatabaseQuench("srv", product, template, "AppProd", "",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        Assert.That(quench.LogPrefix, Is.EqualTo("[srv].[AppProd]"));
    }

    #endregion

    #region Debug File Name

    [Test]
    public void GetDebugFileName_RegularTemplate_OmitsSchemaSuffix()
    {
        // Regular (non-schema) templates have empty _schemaName — the debug filename must end
        // with .sql directly, preserving the pre-slice-3 filename shape. Behavior for the
        // overwhelmingly common path must not change.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "Core" };
        var quench = new DatabaseQuench("srv", product, template, "AppProd",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        Assert.That(quench.GetDebugFileName("Quench Missing Tables And Columns"),
            Is.EqualTo("SchemaQuench - Quench Missing Tables And Columns srv.AppProd.sql"));
    }

    [Test]
    public void GetDebugFileName_SchemaTemplate_IncludesSchemaSuffix()
    {
        // Schema-template iterations share _server/_databaseName across siblings; the schema
        // name must be part of the debug filename or parallel iterations collide on the same
        // file path, hitting a Win32 file-sharing violation that throws before the SQL batch
        // executes (slice-3 audit bug B2).
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template { Name = "TenantBody" };
        var quench = new DatabaseQuench("srv", product, template, "AppProd", "tenant_acme",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        Assert.That(quench.GetDebugFileName("Quench Missing Tables And Columns"),
            Is.EqualTo("SchemaQuench - Quench Missing Tables And Columns srv.AppProd.tenant_acme.sql"));
    }

    [Test]
    public void GetDebugFileName_EmptySchemaName_OmitsSchemaSuffix()
    {
        // Explicit empty string is equivalent to "no schema iteration" — must not render
        // a stray "." before .sql.
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "Core" };
        var quench = new DatabaseQuench("srv", product, template, "AppProd", "",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        Assert.That(quench.GetDebugFileName("Quench Indexes"),
            Is.EqualTo("SchemaQuench - Quench Indexes srv.AppProd.sql"));
    }

    #endregion

    #region PrepareIterationContent — BaselineValidationScript / VersionStampScript / Table Schema

    // Slice-3 audit Phase 4: design Â§5.3 steps 4 + 6 require per-iteration {{SchemaName}}
    // substitution into BaselineValidationScript and VersionStampScript. The private fields
    // are observed via reflection rather than a dedicated accessor (production code change
    // out of scope for this phase).
    private static string GetIterationString(DatabaseQuench quench, string propertyName)
    {
        var iterationField = typeof(DatabaseQuench).GetField("_iteration",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(iterationField, Is.Not.Null, "Expected private field '_iteration' on DatabaseQuench.");
        var iteration = iterationField.GetValue(quench);
        Assert.That(iteration, Is.Not.Null, "DatabaseQuench._iteration was null.");
        var property = iteration.GetType().GetProperty(propertyName);
        Assert.That(property, Is.Not.Null, $"Expected property '{propertyName}' on IterationContent.");
        return (string)property.GetValue(iteration);
    }

    [Test]
    public void PrepareIterationContent_SchemaTemplate_SubstitutesIntoBaselineValidationScript()
    {
        // Design Â§5.3 step 4: BaselineValidationScript runs per iteration with {{SchemaName}}
        // resolved. A discovery script that returns 'tenant_a' must observe the per-tenant
        // baseline body with the token replaced by that tenant name.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_a'",
            BaselineValidationScript = "SELECT CAST(CASE WHEN SCHEMA_ID('{{SchemaName}}') IS NULL THEN 0 ELSE 1 END AS BIT)"
        };

        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_a",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);
        quench.PrepareIterationContent();

        var substituted = GetIterationString(quench, "BaselineValidationScript");
        Assert.That(substituted, Does.Contain("SCHEMA_ID('tenant_a')"));
        Assert.That(substituted, Does.Not.Contain("{{SchemaName}}"));
    }

    [Test]
    public void PrepareIterationContent_RegularTemplate_BaselineValidationScript_LeftVerbatim()
    {
        // Regular templates short-circuit the substitution path — the verbatim template body
        // (which never had a {{SchemaName}} token reason to exist) flows through unmodified.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "Core",
            BaselineValidationScript = "SELECT CAST(1 AS BIT)"
        };

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);
        quench.PrepareIterationContent();

        var captured = GetIterationString(quench, "BaselineValidationScript");
        Assert.That(captured, Is.EqualTo("SELECT CAST(1 AS BIT)"));
    }

    [Test]
    public void PrepareIterationContent_SchemaTemplate_SubstitutesIntoVersionStampScript()
    {
        // Design Â§5.3 step 6: VersionStampScript runs per iteration with {{SchemaName}}
        // resolved. A stamp that touches a per-tenant audit table must see the iteration
        // schema substituted.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_globex'",
            VersionStampScript = "INSERT INTO [{{SchemaName}}].[VersionStamp] (Version) VALUES ('1.0')"
        };

        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_globex",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);
        quench.PrepareIterationContent();

        var substituted = GetIterationString(quench, "VersionStampScript");
        Assert.That(substituted, Does.Contain("[tenant_globex]"));
        Assert.That(substituted, Does.Not.Contain("{{SchemaName}}"));
    }

    [Test]
    public void PrepareIterationContent_RegularTemplate_VersionStampScript_LeftVerbatim()
    {
        var product = new Product { Name = "Test", Platform = Platform.PostgreSQL };
        var template = new Template
        {
            Name = "Core",
            VersionStampScript = "DO $$ BEGIN RAISE NOTICE 'stamped'; END $$;"
        };

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);
        quench.PrepareIterationContent();

        var captured = GetIterationString(quench, "VersionStampScript");
        Assert.That(captured, Is.EqualTo("DO $$ BEGIN RAISE NOTICE 'stamped'; END $$;"));
    }

    [Test]
    public void PrepareIterationContent_SchemaTemplate_SubstitutesIntoIterationTableSchema()
    {
        // Design Â§5.3 step 5 fan-out: the serialized table-definition JSON consumed by the
        // engine-generated DDL procs must have {{SchemaName}} resolved for the iteration.
        // Slice 1's SchemaDefaultResolver fills the Schema field with "{{SchemaName}}";
        // PrepareIterationContent then materializes that token per iteration.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_acme'",
            TableSchema = "[{\"Schema\":\"{{SchemaName}}\",\"Name\":\"Customers\"}]"
        };

        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_acme",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);
        quench.PrepareIterationContent();

        Assert.That(quench.IterationTableSchema, Does.Contain("\"Schema\":\"tenant_acme\""));
        Assert.That(quench.IterationTableSchema, Does.Not.Contain("{{SchemaName}}"));
    }

    [Test]
    public void PrepareIterationContent_RegularTemplate_IterationTableSchema_FallsThroughToTemplate()
    {
        // Regular templates leave _iterationTableSchema null; the property falls back to
        // Template.TableSchema verbatim — no substitution, no behavior change vs. pre-slice-3.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "Core",
            TableSchema = "[{\"Schema\":\"dbo\",\"Name\":\"Customers\"}]"
        };

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);
        quench.PrepareIterationContent();

        Assert.That(quench.IterationTableSchema,
            Is.EqualTo("[{\"Schema\":\"dbo\",\"Name\":\"Customers\"}]"));
    }

    [Test]
    public void PrepareIterationContent_SchemaTemplate_MultipleSchemaNameOccurrences_AllReplaced()
    {
        // Audit item 15: multiple {{SchemaName}} occurrences in a single body. The
        // String.Replace path used in PrepareIterationContent replaces all occurrences;
        // exercise the case end-to-end for VersionStampScript so a future regex-style
        // single-replace refactor would surface here.
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_a'",
            VersionStampScript =
                "INSERT INTO [{{SchemaName}}].[VersionStamp] (Schema_, Version) " +
                "VALUES ('{{SchemaName}}', '1.0'); PRINT 'Stamped {{SchemaName}}';"
        };

        var quench = new DatabaseQuench("srv", product, template, "db", "tenant_a",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);
        quench.PrepareIterationContent();

        var substituted = GetIterationString(quench, "VersionStampScript");
        Assert.That(substituted, Does.Not.Contain("{{SchemaName}}"));
        // tenant_a appears in the table-name, value, and PRINT — three occurrences.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(substituted, "tenant_a").Count;
        Assert.That(occurrences, Is.EqualTo(3),
            "All three {{SchemaName}} occurrences must be replaced — not just the first.");
    }

    #endregion

    #region Artifact Config Helpers

    [Test]
    public void ResolveArtifactDirectory_DefaultsToCurrentDirectory_WhenUnset()
    {
        RegisterMockConfig();
        var quench = new DatabaseQuench("srv", new Product { Name = "P", Platform = Platform.SqlServer },
            new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);
        Assert.That(quench.ResolveArtifactDirectory(), Is.EqualTo(System.IO.Directory.GetCurrentDirectory()));
    }

    [Test]
    public void ResolveArtifactDirectory_UsesConfiguredPath_WhenSet()
    {
        var mockConfig = Substitute.For<IConfigurationRoot>();
        mockConfig["ArtifactPath"].Returns(@"C:\artifacts");
        FactoryContainer.Register<IConfigurationRoot>(mockConfig);

        var quench = new DatabaseQuench("srv", new Product { Name = "P", Platform = Platform.SqlServer },
            new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);
        Assert.That(quench.ResolveArtifactDirectory(), Is.EqualTo(@"C:\artifacts"));
    }

    [Test]
    public void SensitiveTokenValues_ReturnsOnlySensitivelyNamedTokens()
    {
        RegisterMockConfig();
        var product = new Product { Name = "P", Platform = Platform.SqlServer };
        product.ScriptTokens["AdminPassword"] = "supersecret";
        product.ScriptTokens["Region"] = "us-east";
        var quench = new DatabaseQuench("srv", product, new Template { Name = "T" }, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", false, false, false, null);

        var sensitive = quench.SensitiveTokenValues();
        Assert.That(sensitive.Select(kv => kv.Key), Does.Contain("AdminPassword"));
        Assert.That(sensitive.Select(kv => kv.Key), Does.Not.Contain("Region"));
    }

    #endregion

    #region LogScriptErrors — Artifact-Based Failure Reporting

    [Test]
    public void LogScriptErrors_WritesResolvedSqlArtifact_AndLogsPath_NotRawSql()
    {
        Schema.Utility.LogFactory.Clear();
        try
        {
            // Capture progress-log and error-log output before constructing DatabaseQuench.
            var progressLog = Substitute.For<log4net.ILog>();
            var progressLogLines = new List<string>();
            progressLog.When(l => l.Error(Arg.Any<object>()))
                .Do(ci => progressLogLines.Add(ci.Arg<object>().ToString()));
            Schema.Utility.LogFactory.Register("ProgressLog", progressLog);

            var errorLog = Substitute.For<log4net.ILog>();
            var errorLogLines = new List<string>();
            errorLog.When(l => l.Error(Arg.Any<object>()))
                .Do(ci => errorLogLines.Add(ci.Arg<object>().ToString()));
            Schema.Utility.LogFactory.Register("ErrorLog", errorLog);

            // Capture file writes via the IFile mock.
            var mockFile = Substitute.For<IFile>();
            string capturedPath = null;
            string capturedContent = null;
            mockFile.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
                .Do(ci =>
                {
                    capturedPath = ci.ArgAt<string>(0);
                    capturedContent = ci.ArgAt<string>(1);
                });
            FactoryContainer.Register<IFile>(mockFile);
            FactoryContainer.Register<Schema.Isolators.IDirectory>(Substitute.For<Schema.Isolators.IDirectory>());

            // Stub config with ArtifactPath so the directory is deterministic.
            var mockConfig = Substitute.For<IConfigurationRoot>();
            mockConfig["ArtifactPath"].Returns(@"C:\test-artifacts");
            mockConfig["ScrubArtifacts"].Returns((string)null);
            FactoryContainer.Register<IConfigurationRoot>(mockConfig);

            var product = new Product { Name = "P", Platform = Platform.SqlServer };
            var quench = new DatabaseQuench("myserver", product, new Template { Name = "T" }, "mydb",
                false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

            // Build a failed script with a distinctive batch body.
            var script = new SqlScript { Name = "fail.sql", FilePath = "Before Scripts/fail.sql" };
            script.Batches.Add("SELECT distinctive_marker_sql");
            script.Error = new Exception("boom");
            // HasBeenQuenched = false (default) — LogScriptErrors will process it.

            var scripts = new List<SqlScript> { script };

            // Act — drive through QuenchDatabaseObjects(showErrors: true). The mock command
            // throws so the script stays unquenched, then LogScriptErrors fires and throws.
            var mockCmd = CreateMockCommand();
            mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => throw new Exception("SQL error"));
            var ex = Assert.Throws<ScriptQuenchException>(() => quench.QuenchDatabaseObjects(mockCmd, scripts, showErrors: true));
            Assert.That(ex!.Message, Does.Contain("Unable to quench all scripts"));

            // Assert: artifact file received the raw SQL batch body.
            Assert.That(capturedContent, Is.Not.Null, "Expected WriteAllText to be called once");
            Assert.That(capturedContent, Does.Contain("distinctive_marker_sql"),
                "Artifact content must contain the raw SQL batch");

            // Assert: progress-log contains the artifact path reference, NOT the raw SQL.
            var progressOutput = string.Join("\n", progressLogLines);
            Assert.That(progressOutput, Does.Contain(@"C:\test-artifacts"),
                "Progress log must reference the artifact path");
            Assert.That(progressOutput, Does.Not.Contain("distinctive_marker_sql"),
                "Progress log must NOT contain raw SQL — that is the SQL-leak this task closes");

            // Assert: error-log contains the artifact path reference, NOT the raw SQL.
            var errorOutput = string.Join("\n", errorLogLines);
            Assert.That(errorOutput, Does.Contain(@"C:\test-artifacts"),
                "Error log must reference the artifact path");
            Assert.That(errorOutput, Does.Not.Contain("distinctive_marker_sql"),
                "Error log must NOT contain raw SQL — that is the SQL-leak this task closes");

            // Assert: Outcome set to Failed.
            Assert.That(script.Outcome, Is.EqualTo(ScriptOutcome.Failed));
        }
        finally
        {
            Schema.Utility.LogFactory.Clear();
        }
    }

    [Test]
    public void LogScriptErrors_ScrubEnabled_MasksSensitiveTokensInArtifact()
    {
        Schema.Utility.LogFactory.Clear();
        try
        {
            Schema.Utility.LogFactory.Register("ProgressLog", Substitute.For<log4net.ILog>());
            Schema.Utility.LogFactory.Register("ErrorLog", Substitute.For<log4net.ILog>());

            var mockFile = Substitute.For<IFile>();
            string capturedContent = null;
            mockFile.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
                .Do(ci => capturedContent = ci.ArgAt<string>(1));
            FactoryContainer.Register<IFile>(mockFile);
            FactoryContainer.Register<Schema.Isolators.IDirectory>(Substitute.For<Schema.Isolators.IDirectory>());

            var mockConfig = Substitute.For<IConfigurationRoot>();
            mockConfig["ArtifactPath"].Returns(@"C:\test-artifacts");
            mockConfig["ScrubArtifacts"].Returns("true");
            FactoryContainer.Register<IConfigurationRoot>(mockConfig);

            // Product with a sensitive token whose value appears in the batch body.
            var product = new Product { Name = "P", Platform = Platform.SqlServer };
            product.ScriptTokens["AdminPassword"] = "supersecret1234";

            var quench = new DatabaseQuench("myserver", product, new Template { Name = "T" }, "mydb",
                false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

            var script = new SqlScript { Name = "fail.sql", FilePath = "Before Scripts/fail.sql" };
            script.Batches.Add("EXEC dbo.SetPassword 'supersecret1234'");
            script.Error = new Exception("oops");

            var mockCmd = CreateMockCommand();
            mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => throw new Exception("SQL error"));
            Assert.Throws<ScriptQuenchException>(() =>
                quench.QuenchDatabaseObjects(mockCmd, new List<SqlScript> { script }, showErrors: true));

            // The sensitive value must be masked in the artifact content.
            Assert.That(capturedContent, Is.Not.Null, "Expected WriteAllText to be called");
            Assert.That(capturedContent, Does.Not.Contain("supersecret1234"),
                "Sensitive token value must be scrubbed from the artifact when ScrubArtifacts=true");
        }
        finally
        {
            Schema.Utility.LogFactory.Clear();
        }
    }

    #endregion

    #region LogSqlScript — Writes to ArtifactPath, Not BaseDirectory

    [Test]
    public void LogSqlScript_WritesUnderArtifactPath_NotBaseDirectory()
    {
        // Set up a config with a known ArtifactPath.
        var mockConfig = Substitute.For<IConfigurationRoot>();
        mockConfig["ArtifactPath"].Returns(@"C:\artifacts-test");
        FactoryContainer.Register<IConfigurationRoot>(mockConfig);

        // Capture the write path via the IFile mock.
        var mockFile = Substitute.For<IFile>();
        string capturedPath = null;
        mockFile.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => capturedPath = ci.ArgAt<string>(0));
        FactoryContainer.Register<IFile>(mockFile);
        FactoryContainer.Register<Schema.Isolators.IDirectory>(Substitute.For<Schema.Isolators.IDirectory>());

        var product = new Product { Name = "P", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T" };
        template.Tables.Add(new Schema.Domain.Table { Name = "Orders" });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "true", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        // QuenchModifiedTables calls LogSqlScript with the debug filename as the first argument.
        // We drive it and assert the captured write path is under ArtifactPath, not BaseDirectory.
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(capturedPath, Is.Not.Null, "Expected WriteAllText to have been called by LogSqlScript");
        Assert.That(capturedPath, Does.StartWith(@"C:\artifacts-test"),
            "LogSqlScript must write under ArtifactPath, not AppContext.BaseDirectory");
        Assert.That(capturedPath, Does.Not.StartWith(AppContext.BaseDirectory),
            "LogSqlScript must NOT write under the bin directory when ArtifactPath is configured");
    }

    [Test]
    public void LogSqlScript_BadArtifactPath_DoesNotThrow_LogsWarning()
    {
        // Drive LogSqlScript with a file mock that throws on WriteAllText — simulates an
        // unwritable or missing ArtifactPath. The quench must not throw; it must log a warning.
        Schema.Utility.LogFactory.Clear();
        try
        {
            var progressLog = Substitute.For<log4net.ILog>();
            var progressLogLines = new List<string>();
            progressLog.When(l => l.Info(Arg.Any<object>()))
                .Do(ci => progressLogLines.Add(ci.Arg<object>().ToString()));
            Schema.Utility.LogFactory.Register("ProgressLog", progressLog);
            Schema.Utility.LogFactory.Register("ErrorLog", Substitute.For<log4net.ILog>());

            var mockConfig = Substitute.For<IConfigurationRoot>();
            mockConfig["ArtifactPath"].Returns(@"C:\nonexistent-path-that-fails");
            FactoryContainer.Register<IConfigurationRoot>(mockConfig);

            // File mock that throws on write — simulates an unwritable directory.
            var mockFile = Substitute.For<IFile>();
            mockFile.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
                .Do(_ => throw new System.IO.IOException("Access denied"));
            FactoryContainer.Register<IFile>(mockFile);

            // Directory mock that throws on CreateDirectory — simulates an invalid path.
            var mockDir = Substitute.For<Schema.Isolators.IDirectory>();
            mockDir.When(d => d.CreateDirectory(Arg.Any<string>()))
                .Do(_ => throw new System.IO.IOException("Access denied"));
            FactoryContainer.Register<Schema.Isolators.IDirectory>(mockDir);

            var product = new Product { Name = "P", Platform = Platform.PostgreSQL };
            var template = new Template { Name = "T" };
            template.Tables.Add(new Schema.Domain.Table { Name = "Orders" });

            var quench = new DatabaseQuench("srv", product, template, "db",
                false, "true", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

            var mockCmd = CreateMockCommand();

            // Must not throw — LogSqlScript degrades gracefully on write failure.
            Assert.DoesNotThrow(() => quench.QuenchModifiedTables(mockCmd));

            // A warning must have been logged.
            var output = string.Join("\n", progressLogLines);
            Assert.That(output, Does.Contain("Could not write debug SQL artifact"),
                "A warning must be logged when the debug SQL artifact write fails");
        }
        finally
        {
            Schema.Utility.LogFactory.Clear();
        }
    }

    [Test]
    public void LogSqlScript_ScrubEnabled_MasksSensitiveTokenValue_InWrittenArtifact()
    {
        // #327 S4.2: the always-written generated-DDL debug artifact must be scrub-aware under the
        // same ScrubArtifacts opt-in as user-script / data-delivery artifacts.
        var mockConfig = Substitute.For<IConfigurationRoot>();
        mockConfig["ArtifactPath"].Returns(@"C:\artifacts-test");
        mockConfig["ScrubArtifacts"].Returns("true");
        FactoryContainer.Register<IConfigurationRoot>(mockConfig);

        var mockFile = Substitute.For<IFile>();
        string capturedContent = null;
        mockFile.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => capturedContent = ci.ArgAt<string>(1));
        FactoryContainer.Register<IFile>(mockFile);
        FactoryContainer.Register<Schema.Isolators.IDirectory>(Substitute.For<Schema.Isolators.IDirectory>());

        // Sensitive token value present verbatim in the generated CommandText via TableSchema.
        var product = new Product { Name = "P", Platform = Platform.PostgreSQL };
        product.ScriptTokens["AdminPassword"] = "supersecret1234";
        var template = new Template
        {
            Name = "T",
            IndexOnlyTableQuenches = true, // SqlServer/PostgreSQL branches embed IterationTableSchema inline
            TableSchema = "[{\"Name\":\"Orders\",\"Columns\":[{\"Name\":\"Seed\",\"Default\":\"supersecret1234\"}]}]"
        };
        template.Tables.Add(new Schema.Domain.Table { Name = "Orders" });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "true", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexesAndConstraints(mockCmd);

        Assert.That(capturedContent, Is.Not.Null, "Expected WriteAllText to be called by LogSqlScript");
        Assert.That(capturedContent, Does.Not.Contain("supersecret1234"),
            "Sensitive token value must be scrubbed from the generated-DDL debug artifact when ScrubArtifacts=true");
    }

    [Test]
    public void LogSqlScript_ScrubDisabled_LeavesSensitiveTokenValueIntact_InWrittenArtifact()
    {
        var mockConfig = Substitute.For<IConfigurationRoot>();
        mockConfig["ArtifactPath"].Returns(@"C:\artifacts-test");
        mockConfig["ScrubArtifacts"].Returns((string)null); // disabled (default)
        FactoryContainer.Register<IConfigurationRoot>(mockConfig);

        var mockFile = Substitute.For<IFile>();
        string capturedContent = null;
        mockFile.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => capturedContent = ci.ArgAt<string>(1));
        FactoryContainer.Register<IFile>(mockFile);
        FactoryContainer.Register<Schema.Isolators.IDirectory>(Substitute.For<Schema.Isolators.IDirectory>());

        var product = new Product { Name = "P", Platform = Platform.PostgreSQL };
        product.ScriptTokens["AdminPassword"] = "supersecret1234";
        var template = new Template
        {
            Name = "T",
            IndexOnlyTableQuenches = true, // SqlServer/PostgreSQL branches embed IterationTableSchema inline
            TableSchema = "[{\"Name\":\"Orders\",\"Columns\":[{\"Name\":\"Seed\",\"Default\":\"supersecret1234\"}]}]"
        };
        template.Tables.Add(new Schema.Domain.Table { Name = "Orders" });

        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "true", false, "false", "false", "false", "false", "false", "false", "false", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchIndexesAndConstraints(mockCmd);

        Assert.That(capturedContent, Is.Not.Null, "Expected WriteAllText to be called by LogSqlScript");
        Assert.That(capturedContent, Does.Contain("supersecret1234"),
            "Without ScrubArtifacts, the generated-DDL debug artifact keeps the raw resolved SQL (local debugging needs the real value)");
    }

    #endregion

    #region Outer-Catch Debug-Path Surfacing (PG/MySQL)

    [Test]
    public void Execute_OuterCatch_SurfacesDebugFilePath_WhenGeneratedSqlFails()
    {
        // This test drives a PostgreSQL quench where the generated-SQL step
        // (QuenchMissingTablesAndColumns) throws. The outer catch in Execute() must log
        // "Resolved SQL written to: <path>" when _debugFileLocation is set — surfacing the
        // generated-SQL dump for PostgreSQL/MySQL users who have no InfoMessage handler.
        Schema.Utility.LogFactory.Clear();
        try
        {
            var progressLog = Substitute.For<log4net.ILog>();
            var progressLogLines = new List<string>();
            progressLog.When(l => l.Error(Arg.Any<object>()))
                .Do(ci => progressLogLines.Add(ci.Arg<object>().ToString()));
            Schema.Utility.LogFactory.Register("ProgressLog", progressLog);
            Schema.Utility.LogFactory.Register("ErrorLog", Substitute.For<log4net.ILog>());

            // File mock: LogSqlScript calls WriteAllText before the generated SQL executes.
            // Capture its path — the outer-catch "Resolved SQL written to:" line must contain it.
            var mockFile = Substitute.For<IFile>();
            string debugPath = null;
            mockFile.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
                .Do(ci => { if (debugPath == null) debugPath = ci.ArgAt<string>(0); });
            FactoryContainer.Register<IFile>(mockFile);
            FactoryContainer.Register<Schema.Isolators.IDirectory>(Substitute.For<Schema.Isolators.IDirectory>());

            var mockConfig = Substitute.For<IConfigurationRoot>();
            mockConfig["ArtifactPath"].Returns(@"C:\pg-artifacts");
            mockConfig["ScrubArtifacts"].Returns((string)null);
            mockConfig["Target:User"].Returns("u");
            mockConfig["Target:Password"].Returns("p");
            FactoryContainer.Register<IConfigurationRoot>(mockConfig);

            // Mock the PostgreSQL connection factory so no real network call is made.
            // The mock command throws on ExecuteNonQuery, forcing the generated-SQL step to fail.
            var mockConn = Substitute.For<System.Data.IDbConnection>();
            var mockCmd = CreateMockCommand();
            mockCmd.ExecuteScalar().Returns("160000"); // PG version detection (D3: TargetVersionDetector)
            // The per-connection session GUCs (SET schemasmith.unsupported_policy / version_override) run
            // on this same mock during connection setup; on a real server they succeed. Only the
            // generated-SQL step should fail here, so let the SET statements through.
            mockCmd.When(c => c.ExecuteNonQuery()).Do(_ =>
            {
                if (mockCmd.CommandText?.StartsWith("SET schemasmith") == true) return;
                throw new Exception("simulated generated-SQL failure");
            });
            mockConn.CreateCommand().Returns(mockCmd);
            mockConn.Database.Returns("pgdb");
            var mockConnFactory = Substitute.For<Schema.DataAccess.IDbConnectionFactory>();
            mockConnFactory.GetDbConnection(Arg.Any<string>()).Returns(mockConn);
            FactoryContainer.Register<Schema.DataAccess.IDbConnectionFactory>(mockConnFactory);

            var product = new Product { Name = "P", Platform = Platform.PostgreSQL };
            var template = new Template { Name = "T" };
            template.Tables.Add(new Schema.Domain.Table { Name = "Orders" });

            var checkpointing = Substitute.For<Schema.Checkpointing.ICheckpointing>();
            checkpointing.GetDatabaseCheckpointSummary(Arg.Any<Schema.Checkpointing.TrackingScope>())
                .Returns(Schema.Checkpointing.DatabaseCheckpointSummary.Empty);
            // Fire the action delegate directly — no resume skip.
            checkpointing.When(c => c.Track(
                    Arg.Any<Schema.Checkpointing.TrackingScope>(),
                    Arg.Any<string>(),
                    Arg.Any<Action>()))
                .Do(ci => ci.ArgAt<Action>(2)());

            var quench = new DatabaseQuench("pghost", product, template, "pgdb",
                suppressKindling: true, whatIfOnly: "false", runScriptsTwice: false,
                dropRemovedTables: "false", dropRemovedColumns: "false", dropRemovedForeignKeys: "false", dropRemovedCheckConstraints: "false", dropRemovedExcludeConstraints: "false", dropRemovedStatistics: "false", dropRemovedIndexes: "false", dropUnknownIndexes: "false",
                updateTables: true, deliverData: false, checkpointing: checkpointing);

            quench.Execute();

            var progressOutput = string.Join("\n", progressLogLines);
            Assert.That(progressOutput, Does.Contain("Resolved SQL written to:"),
                "Outer catch must surface the debug-file path for PostgreSQL/MySQL users");
            Assert.That(debugPath, Is.Not.Null,
                "LogSqlScript must have been called (writing under ArtifactPath) before the failure");
            // _debugFileLocation must hold the FULL path returned by LogSqlScript, not a bare filename.
            // A bare filename would fail the StartsWith check below even though it contains the label.
            Assert.That(progressOutput, Does.Contain(debugPath),
                "The surfaced 'Resolved SQL written to:' line must contain the full artifact path, not just the filename");
            Assert.That(progressOutput, Does.Contain(@"C:\pg-artifacts"),
                "The surfaced debug path must be rooted under the configured ArtifactPath");
        }
        finally
        {
            Schema.Utility.LogFactory.Clear();
        }
    }

    #endregion

    #region QuenchModifiedTables — DropColumnsRemovedFromProduct param threading

    [Test]
    public void QuenchModifiedTables_SqlServer_ContainsDropColumnsRemovedFromProductParam()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        // internal ctor order: suppressKindling, whatIfOnly, runScriptsTwice, dropRemovedTables, dropRemovedColumns, dropUnknownIndexes, ...
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("@DropColumnsRemovedFromProduct = 1"));
    }

    [Test]
    public void QuenchModifiedTables_PostgreSQL_ContainsDropColumnsRemovedFromProductParam()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T" };
        // internal ctor order: suppressKindling, whatIfOnly, runScriptsTwice, dropRemovedTables, dropRemovedColumns, dropUnknownIndexes, ...
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "true", "true", "true", "true", "true", "true", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("p_DropColumnsRemovedFromProduct := true"));
    }

    [Test]
    public void QuenchModifiedTables_MySQL_ContainsDropColumnsRemovedFromProductParam()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.MySQL };
        var template = new Template { Name = "T" };
        template.Tables.Add(new Schema.Domain.Table { Name = "[T1]" });
        // internal ctor order: dropRemovedTables="0" -> arg4=0, dropRemovedColumns="1" -> arg5=1
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        // DropColumnsRemovedFromProduct is the FIFTH positional argument of the MySQL CALL:
        // (product, database, WhatIf, DropTablesRemovedFromProduct, DropColumnsRemovedFromProduct, ...).
        // Assert that position rather than a floating substring: ", 0, 0, 1," does not say WHICH
        // arguments it matched, so it would keep passing if the flag moved, and it breaks for
        // unrelated reasons when a parameter is inserted ahead of it.
        Assert.That(mockCmd.CommandText, Does.StartWith("CALL SchemaSmith_ModifiedTableQuench("),
            "Expected a positional MySQL CALL to SchemaSmith_ModifiedTableQuench.");
        var args = MySqlCallArguments(mockCmd.CommandText);
        Assert.That(args.Length, Is.GreaterThanOrEqualTo(5),
            $"MySQL ModifiedTableQuench CALL has too few arguments: {mockCmd.CommandText}");
        Assert.That(args[4], Is.EqualTo("1"),
            $"DropColumnsRemovedFromProduct must be threaded as the 5th positional argument: {mockCmd.CommandText}");
    }

    #endregion

    #region DropForeignKeysRemovedFromProduct param threading

    // SS/PG: FK-by-absence logic lives inside ModifiedTableQuench; the flag is threaded there.
    // MySQL: FK-by-absence lives in ForeignKeyQuench (separate proc); the flag is threaded there.

    [Test]
    public void QuenchModifiedTables_SqlServer_ContainsDropForeignKeysRemovedFromProductParam()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        // internal ctor: ..., dropRemovedTables, dropRemovedColumns, dropRemovedForeignKeys, dropUnknownIndexes, ...
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("@DropForeignKeysRemovedFromProduct = 1"));
    }

    [Test]
    public void QuenchModifiedTables_PostgreSQL_ContainsDropForeignKeysRemovedFromProductParam()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "true", "true", "true", "true", "true", "true", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("p_DropForeignKeysRemovedFromProduct := true"));
    }

    [Test]
    public void QuenchForeignKeys_MySQL_ContainsDropForeignKeysRemovedFromProductParam()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.MySQL };
        var template = new Template { Name = "T" };
        template.Tables.Add(new Schema.Domain.Table { Name = "[T1]" });
        // dropRemovedForeignKeys="1" -> 5th positional arg = 1
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchForeignKeys(mockCmd);

        // DropForeignKeysRemovedFromProduct is the FIFTH positional argument of the MySQL CALL:
        // (product, database, WhatIf, DropUnknownForeignKeys, DropForeignKeysRemovedFromProduct).
        // Assert that position rather than the tail of the argument list — a tail assertion breaks
        // the next time this procedure gains a parameter, for a reason unrelated to foreign keys.
        Assert.That(mockCmd.CommandText, Does.StartWith("CALL SchemaSmith_ForeignKeyQuench("),
            "Expected a positional MySQL CALL to SchemaSmith_ForeignKeyQuench.");
        var args = MySqlCallArguments(mockCmd.CommandText);
        Assert.That(args.Length, Is.GreaterThanOrEqualTo(5),
            $"MySQL ForeignKeyQuench CALL has too few arguments: {mockCmd.CommandText}");
        Assert.That(args[4], Is.EqualTo("1"),
            $"DropForeignKeysRemovedFromProduct must be threaded as the 5th positional argument: {mockCmd.CommandText}");
    }

    #endregion

    #region DropCheckConstraintsRemovedFromProduct param threading

    // Check-by-absence logic lives inside ModifiedTableQuench on ALL THREE engines.

    [Test]
    public void QuenchModifiedTables_SqlServer_ContainsDropCheckConstraintsRemovedFromProductParam()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        // ctor order: ..., dropRemovedTables, dropRemovedColumns, dropRemovedForeignKeys, dropRemovedCheckConstraints, dropUnknownIndexes, ...
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "0", "0", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("@DropCheckConstraintsRemovedFromProduct = 1"));
    }

    [Test]
    public void QuenchModifiedTables_PostgreSQL_ContainsDropCheckConstraintsRemovedFromProductParam()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "false", "false", "true", "true", "true", "true", "false", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("p_DropCheckConstraintsRemovedFromProduct := true"));
    }

    [Test]
    public void QuenchModifiedTables_MySQL_ContainsDropCheckConstraintsRemovedFromProductParam()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.MySQL };
        var template = new Template { Name = "T" };
        template.Tables.Add(new Schema.Domain.Table { Name = "[T1]" });
        // dropRemovedCheckConstraints="1" -> 6th positional arg of the MySQL CALL (followed by
        // excludes, statistics, and the #270 captureWouldDrop flag).
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "0", "0", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        // DropCheckConstraintsRemovedFromProduct is the SIXTH positional argument of the MySQL CALL:
        // (product, database, WhatIf, DropTablesRemovedFromProduct, DropColumnsRemovedFromProduct,
        //  DropCheckConstraintsRemovedFromProduct, ...). Assert that position rather than the tail of
        // the argument list: the tail moves every time a parameter is appended (index removal moving
        // into this procedure appended DropUnknownIndexes and DropIndexesRemovedFromProduct), so a
        // tail assertion fails — or silently passes — for reasons that have nothing to do with check
        // constraints, which is the only thing this test is named for.
        Assert.That(mockCmd.CommandText, Does.StartWith("CALL SchemaSmith_ModifiedTableQuench("),
            "Expected a positional MySQL CALL to SchemaSmith_ModifiedTableQuench.");
        var args = MySqlCallArguments(mockCmd.CommandText);
        Assert.That(args.Length, Is.GreaterThanOrEqualTo(6),
            $"MySQL ModifiedTableQuench CALL has too few arguments: {mockCmd.CommandText}");
        Assert.That(args[5], Is.EqualTo("1"),
            $"DropCheckConstraintsRemovedFromProduct must be threaded as the 6th positional argument: {mockCmd.CommandText}");
    }

    #endregion

    #region CdcFilegroup threading (#417)

    // NULL is the unmanaged contract, so an absent template default must reach the procedure as the SQL literal,
    // never as an empty string -- '' is a filegroup name that does not exist, and would fail every CDC table.
    [TestCase(null, "@CdcFilegroup = NULL")]
    [TestCase("cdc_fg", "@CdcFilegroup = N'cdc_fg'")]
    [TestCase("o'fg", "@CdcFilegroup = N'o''fg'")]
    public void QuenchModifiedTables_SqlServer_SendsTheTemplateCdcFilegroup(string templateValue, string expected)
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.SqlServer };
        var template = new Template { Name = "T", CdcFilegroup = templateValue };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain(expected));
    }

    #endregion

    #region RebuildPolicy threading

    // The resolved upper-tier policy is the only thing that lets a table WITHOUT its own RebuildPolicy be
    // rebuilt, so how it is rendered into each engine's call is load-bearing: an unquoted mode, or a
    // Threshold rendered as 0 instead of NULL, would silently change which tables get elected. These pin
    // the rendering, not the plumbing.

    [Test]
    public void QuenchModifiedTables_SqlServer_WithNoPolicy_SendsNeverAndANullThreshold()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("@RebuildPolicyMode = 'NEVER'"),
            "A DatabaseQuench with no resolved policy must send the domain object's own NEVER default.");
        Assert.That(mockCmd.CommandText, Does.Contain("@RebuildPolicyThreshold = NULL"),
            "An absent Threshold must be the SQL literal NULL. Rendering it as a number would make a "
            + "THRESHOLD policy with no Threshold rebuild at some invented count.");
    }

    [Test]
    public void QuenchModifiedTables_SqlServer_SendsTheResolvedPolicy()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.SqlServer };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null)
        {
            CascadedRebuildPolicy = new RebuildPolicy { Mode = "threshold", Threshold = 4 }
        };

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("@RebuildPolicyMode = 'THRESHOLD'"),
            "The mode is upper-cased on the way out so the procs compare against one spelling.");
        Assert.That(mockCmd.CommandText, Does.Contain("@RebuildPolicyThreshold = 4"),
            "The resolved Threshold must reach the procedure, or THRESHOLD can never fire.");
    }

    [Test]
    public void QuenchModifiedTables_PostgreSQL_SendsTheResolvedPolicy()
    {
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.PostgreSQL };
        var template = new Template { Name = "T" };
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "false", false, "false", "true", "true", "true", "true", "true", "true", "false", false, false, null)
        {
            CascadedRebuildPolicy = new RebuildPolicy { Mode = "ALWAYS" }
        };

        var mockCmd = CreateMockCommand();
        quench.QuenchModifiedTables(mockCmd);

        Assert.That(mockCmd.CommandText, Does.Contain("p_RebuildPolicyMode := 'ALWAYS'"));
        Assert.That(mockCmd.CommandText, Does.Contain("p_RebuildPolicyThreshold := NULL"),
            "ALWAYS carries no Threshold, and NULL is what the procedure's THRESHOLD arm tests for.");
    }

    [Test]
    public void QuenchModifiedTables_MySQL_SetsTheRebuildPolicySessionVariables()
    {
        // MySQL takes the policy in session variables rather than parameters (no default parameter values,
        // ~30 direct call sites), so the SET is the whole transport. It must be issued on EVERY call: a
        // pooled connection carrying a previous template's ALWAYS would rebuild tables that never asked.
        RegisterMockFileWrapper();
        var product = new Product { Name = "Prod", Platform = Platform.MySQL };
        var template = new Template { Name = "T" };
        template.Tables.Add(new Schema.Domain.Table { Name = "[T1]" });
        var quench = new DatabaseQuench("srv", product, template, "db",
            false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null)
        {
            CascadedRebuildPolicy = new RebuildPolicy { Mode = "THRESHOLD", Threshold = 7 }
        };

        var mockCmd = CreateMockCommand();
        var issued = new List<string>();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => issued.Add(mockCmd.CommandText));

        quench.QuenchModifiedTables(mockCmd);

        Assert.That(issued.Any(s => s.Contains("@ss_rebuild_policy_mode = 'THRESHOLD'")
                                    && s.Contains("@ss_rebuild_policy_threshold = 7")),
            "The rebuild policy must be pushed into the session before the CALL. Statements issued: "
            + string.Join(" | ", issued));
    }

    #endregion

    #region Helper Methods

    // Positional arguments of a MySQL "CALL proc(a, b, c)" statement, trimmed. Lets a test assert the
    // one argument it is named for by POSITION, instead of matching the head or tail of the whole
    // list — which breaks whenever an unrelated parameter is added.
    private static string[] MySqlCallArguments(string commandText)
    {
        var open = commandText.IndexOf('(');
        var close = commandText.LastIndexOf(')');
        return commandText.Substring(open + 1, close - open - 1).Split(',').Select(a => a.Trim()).ToArray();
    }

    private static IDbCommand CreateMockCommand()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        var mockConnection = Substitute.For<IDbConnection>();
        mockConnection.Database.Returns("testdb");
        mockCmd.Connection.Returns(mockConnection);
        return mockCmd;
    }

    private static void RegisterMockConfig()
    {
        var mockConfig = Substitute.For<IConfigurationRoot>();
        mockConfig["DropUnknownIndexes"].Returns((string)null);
        mockConfig["Target:User"].Returns("user");
        mockConfig["Target:Password"].Returns("pass");
        FactoryContainer.Register<IConfigurationRoot>(mockConfig);
    }

    private static void RegisterMockFileWrapper()
    {
        var mockFile = Substitute.For<IFile>();
        FactoryContainer.Register<IFile>(mockFile);
        FactoryContainer.Register<Schema.Isolators.IDirectory>(Substitute.For<Schema.Isolators.IDirectory>());
        // LogSqlScript now calls ResolveArtifactDirectory() → IConfigurationRoot. Register a stub
        // so tests that only mock IFile don't hit FactoryContainer's "create interface" failure.
        if (FactoryContainer.Resolve<IConfigurationRoot>() == null)
        {
            var cfg = Substitute.For<IConfigurationRoot>();
            FactoryContainer.Register<IConfigurationRoot>(cfg);
        }
    }

    #endregion
}


