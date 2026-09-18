// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using log4net;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Domain
{
    /// <summary>
    /// Load-time validation tests for the schema-template surface area (design §3.3).
    /// Covers rules 1-5: MySQL guard, database-scoped folder rejection, filename-prefix check,
    /// table Schema literal rejection (rule 4 bubbles up from SchemaDefaultResolver — slice 1),
    /// CreateSchemaIfMissing presence check, and regular-template warnings for schema-only fields.
    /// Uses the mocked IFile / IDirectory pattern already established in TemplateLoadTests.
    /// </summary>
    [TestFixture]
    public class TemplateLoadValidationTests
    {
        private IFile _mockFile;
        private IDirectory _mockDirectory;
        private ILog _mockProgressLog;

        [SetUp]
        public void SetUp()
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            _mockFile = Substitute.For<IFile>();
            _mockDirectory = Substitute.For<IDirectory>();
            FactoryContainer.Register<IFile>(_mockFile);
            FactoryContainer.Register<IDirectory>(_mockDirectory);

            _mockProgressLog = Substitute.For<ILog>();
            LogFactory.Register("ProgressLog", _mockProgressLog);
        }

        [TearDown]
        public void TearDown()
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
        }

        private static Product MakeProduct(Platform platform) => new()
        {
            Name = "TestProduct",
            Platform = platform,
            FilePath = Path.Combine("C:", "products", "Product.json")
        };

        private static (string TemplateFile, string TablesPath) Paths() =>
            (Path.Combine("C:", "products", "Templates", "Main", "Template.json"),
             Path.Combine("C:", "products", "Templates", "Main", "Tables"));

        // -------------------------------------------------------------------
        // Rule 1: MySQL never reaches load with non-empty SchemaIdentificationScript.
        // Slice 1's MigrateMySqlSchemaIdentificationScriptAlias clears it; this test
        // asserts the alias migration completes cleanly and the template is treated
        // as a regular MySQL template post-load (IsSchemaTemplate == false).
        // -------------------------------------------------------------------
        [Test]
        public void SchemaTemplate_OnMySql_IsMigratedAway_NotTreatedAsSchemaTemplate()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""Main"",
                ""DatabaseIdentificationScript"": null,
                ""SchemaIdentificationScript"": ""SELECT 'whatever'""
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);

            var template = Template.Load("Main", MakeProduct(Platform.MySQL));

            // Alias migration moves the value into DatabaseIdentificationScript and clears the alias.
            Assert.That(template.IsSchemaTemplate, Is.False,
                "MySQL templates must not be treated as schema templates post-load — the alias migration clears the field.");
            Assert.That(template.DatabaseIdentificationScript, Is.EqualTo("SELECT 'whatever'"));
            Assert.That(template.SchemaIdentificationScript, Is.Null.Or.Empty);
        }

        [TestCase(Platform.MySQL)]
        [TestCase(Platform.MariaDb)]
        public void SchemaIdentificationScriptAlias_OnMySqlFamily_IsRecordedAsADeprecationNotice(Platform platform)
        {
            var (templateFile, tablesPath) = Paths();
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(@"{ ""Name"": ""Main"", ""SchemaIdentificationScript"": ""SELECT 'db'"" }");
            _mockDirectory.Exists(tablesPath).Returns(false);

            var template = Template.Load("Main", MakeProduct(platform));

            Assert.That(template.DeprecationNotices, Has.Count.EqualTo(1));
            Assert.That(template.DeprecationNotices[0].Code, Is.EqualTo("SS-DEP-001"));
            Assert.That(template.DeprecationNotices[0].Location, Is.EqualTo(templateFile));
            Assert.That(template.DeprecationNotices[0].Message, Does.Contain("DatabaseIdentificationScript"));
        }

        [TestCase(Platform.SqlServer)]
        [TestCase(Platform.PostgreSQL)]
        public void SchemaIdentificationScript_OnSchemaTemplateEngines_IsNotADeprecation(Platform platform)
        {
            var (templateFile, tablesPath) = Paths();
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(@"{ ""Name"": ""Main"", ""DatabaseIdentificationScript"": ""SELECT 'db'"", ""SchemaIdentificationScript"": ""SELECT 'tenant'"" }");
            _mockDirectory.Exists(tablesPath).Returns(false);

            var template = Template.Load("Main", MakeProduct(platform));

            Assert.That(template.DeprecationNotices, Is.Empty);
        }

        // -------------------------------------------------------------------
        // Rule 2: Database-scoped ObjectType rejection for schema templates.
        // -------------------------------------------------------------------
        [Test]
        public void SchemaTemplate_DDLTriggersScriptFolder_Rejected()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""TenantBody"",
                ""SchemaIdentificationScript"": ""SELECT 'tenant_a'"",
                ""ScriptFolders"": [
                    { ""FolderPath"": ""DDLTriggers"", ""QuenchSlot"": ""AfterTablesObjects"", ""ObjectType"": ""DDLTriggers"" }
                ]
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Template.Load("Main", MakeProduct(Platform.SqlServer)));
            Assert.That(ex!.Message, Does.Contain("DDLTriggers"));
            Assert.That(ex.Message, Does.Contain("non-schema-template").IgnoreCase
                .Or.Contain("database-scoped").IgnoreCase);
        }

        [Test]
        public void SchemaTemplate_SchemasScriptFolder_Rejected()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""TenantBody"",
                ""SchemaIdentificationScript"": ""SELECT 'tenant_a'"",
                ""ScriptFolders"": [
                    { ""FolderPath"": ""Schemas"", ""QuenchSlot"": ""Objects"", ""ObjectType"": ""Schemas"" }
                ]
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Template.Load("Main", MakeProduct(Platform.SqlServer)));
            Assert.That(ex!.Message, Does.Contain("Schemas"));
        }

        [Test]
        public void SchemaTemplate_FullTextCatalogsScriptFolder_Rejected()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""TenantBody"",
                ""SchemaIdentificationScript"": ""SELECT 'tenant_a'"",
                ""ScriptFolders"": [
                    { ""FolderPath"": ""FullTextCatalogs"", ""QuenchSlot"": ""Objects"", ""ObjectType"": ""FullTextCatalogs"" }
                ]
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            Assert.Throws<InvalidOperationException>(() =>
                Template.Load("Main", MakeProduct(Platform.SqlServer)));
        }

        [Test]
        public void SchemaTemplate_FullTextStopListsScriptFolder_Rejected()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""TenantBody"",
                ""SchemaIdentificationScript"": ""SELECT 'tenant_a'"",
                ""ScriptFolders"": [
                    { ""FolderPath"": ""FullTextStopLists"", ""QuenchSlot"": ""Objects"", ""ObjectType"": ""FullTextStopLists"" }
                ]
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            Assert.Throws<InvalidOperationException>(() =>
                Template.Load("Main", MakeProduct(Platform.SqlServer)));
        }

        [Test]
        public void RegularTemplate_DDLTriggersScriptFolder_Allowed()
        {
            // Regular templates may declare DDLTriggers without issue.
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""Main"",
                ""ScriptFolders"": [
                    { ""FolderPath"": ""DDLTriggers"", ""QuenchSlot"": ""AfterTablesObjects"", ""ObjectType"": ""DDLTriggers"" }
                ]
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            var template = Template.Load("Main", MakeProduct(Platform.SqlServer));

            Assert.That(template.ScriptFolders, Has.Count.EqualTo(1));
            Assert.That(template.ScriptFolders[0].ObjectType, Is.EqualTo(ScriptObjectType.DDLTriggers));
        }

        // -------------------------------------------------------------------
        // Rule 3: Filename prefix check — schema templates reject "Schema.Name.json".
        // -------------------------------------------------------------------
        [Test]
        public void SchemaTemplate_TableFilenameWithSchemaPrefix_Rejected()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""TenantBody"",
                ""SchemaIdentificationScript"": ""SELECT 'tenant_a'""
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(true);

            var tableFile = Path.Combine(tablesPath, "dbo.Customers.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { tableFile });
            _mockFile.Exists(tableFile).Returns(true);
            _mockFile.ReadAllText(tableFile).Returns(@"{ ""Name"": ""Customers"", ""Columns"": [] }");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Template.Load("Main", MakeProduct(Platform.SqlServer)));
            Assert.That(ex!.Message, Does.Contain("dbo.Customers.json"));
            Assert.That(ex.Message, Does.Contain("schema").IgnoreCase);
        }

        [Test]
        public void SchemaTemplate_TableFilenameWithoutSchemaPrefix_Allowed()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""TenantBody"",
                ""SchemaIdentificationScript"": ""SELECT 'tenant_a'""
            }";
            // Catch-all FIRST, specific paths SECOND — NSubstitute's last-matching-call-wins
            // means specific Returns() values stick only when configured after the wildcard default.
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(true);

            var tableFile = Path.Combine(tablesPath, "Customers.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { tableFile });
            _mockFile.Exists(tableFile).Returns(true);
            _mockFile.ReadAllText(tableFile).Returns(@"{ ""Name"": ""Customers"", ""Columns"": [] }");

            var template = Template.Load("Main", MakeProduct(Platform.SqlServer));
            Assert.That(template.Tables, Has.Count.EqualTo(1));
        }

        [Test]
        public void RegularTemplate_TableFilenameWithSchemaPrefix_Allowed()
        {
            // Regular templates use schema-qualified filenames — the prefix check applies only to schema templates.
            var (templateFile, tablesPath) = Paths();
            var json = @"{ ""Name"": ""Main"" }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(true);

            var tableFile = Path.Combine(tablesPath, "dbo.Customers.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { tableFile });
            _mockFile.Exists(tableFile).Returns(true);
            _mockFile.ReadAllText(tableFile).Returns(@"{ ""Name"": ""Customers"", ""Columns"": [] }");

            var template = Template.Load("Main", MakeProduct(Platform.SqlServer));
            Assert.That(template.Tables, Has.Count.EqualTo(1));
        }

        [Test]
        public void SchemaTemplate_MaterializedViewFilenameWithSchemaPrefix_Rejected()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""TenantBody"",
                ""SchemaIdentificationScript"": ""SELECT 'tenant_a'""
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);

            var mvPath = Path.Combine("C:", "products", "Templates", "Main", "Materialized Views");
            _mockDirectory.Exists(mvPath).Returns(true);
            var mvFile = Path.Combine(mvPath, "public.mv_test.json");
            _mockDirectory.GetFiles(mvPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { mvFile });
            _mockFile.ReadAllText(mvFile).Returns(@"{ ""Name"": ""mv_test"", ""Definition"": ""SELECT 1"" }");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Template.Load("Main", MakeProduct(Platform.PostgreSQL)));
            Assert.That(ex!.Message, Does.Contain("public.mv_test.json"));
        }

        [Test]
        public void SchemaTemplate_IndexedViewFilenameWithSchemaPrefix_Rejected()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""TenantBody"",
                ""SchemaIdentificationScript"": ""SELECT 'tenant_a'""
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);

            var ivPath = Path.Combine("C:", "products", "Templates", "Main", "Indexed Views");
            _mockDirectory.Exists(ivPath).Returns(true);
            var ivFile = Path.Combine(ivPath, "dbo.vw_test.json");
            _mockDirectory.GetFiles(ivPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { ivFile });
            _mockFile.ReadAllText(ivFile).Returns(@"{ ""Name"": ""vw_test"", ""Definition"": ""SELECT 1"" }");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Template.Load("Main", MakeProduct(Platform.SqlServer)));
            Assert.That(ex!.Message, Does.Contain("dbo.vw_test.json"));
        }

        // -------------------------------------------------------------------
        // Rule 4: Table Schema literal rejection (slice 1's SchemaDefaultResolver) —
        // assert it bubbles up with file context wrap.
        // -------------------------------------------------------------------
        [Test]
        public void SchemaTemplate_TableJsonWithLiteralSchemaDbo_BubblesUpWithFileContext()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""TenantBody"",
                ""SchemaIdentificationScript"": ""SELECT 'tenant_a'""
            }";
            // Catch-all FIRST, specific paths SECOND.
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(true);

            var tableFile = Path.Combine(tablesPath, "Customers.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { tableFile });
            _mockFile.Exists(tableFile).Returns(true);
            _mockFile.ReadAllText(tableFile).Returns(@"{ ""Name"": ""Customers"", ""Schema"": ""dbo"", ""Columns"": [] }");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Template.Load("Main", MakeProduct(Platform.SqlServer)));

            // SchemaDefaultResolver.Resolve(Template) wraps with template name + file path.
            Assert.That(ex!.Message, Does.Contain("TenantBody"));
            Assert.That(ex.Message, Does.Contain("Template.json"));
            Assert.That(ex.Message, Does.Contain("Customers"));
        }

        // -------------------------------------------------------------------
        // Rule 5: CreateSchemaIfMissing requires SchemaIdentificationScript.
        // -------------------------------------------------------------------
        [Test]
        public void CreateSchemaIfMissingTrue_WithoutSchemaIdentificationScript_Rejected()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""Main"",
                ""CreateSchemaIfMissing"": true
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Template.Load("Main", MakeProduct(Platform.SqlServer)));
            Assert.That(ex!.Message, Does.Contain("CreateSchemaIfMissing"));
            Assert.That(ex.Message, Does.Contain("SchemaIdentificationScript"));
        }

        // -------------------------------------------------------------------
        // Schema-only fields set non-default on a regular template → warn but load.
        // -------------------------------------------------------------------
        [Test]
        public void RegularTemplate_AllowParallelFalse_WarnsButLoads()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""Main"",
                ""AllowParallel"": false
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            var template = Template.Load("Main", MakeProduct(Platform.SqlServer));

            Assert.That(template, Is.Not.Null);
            Assert.That(template.AllowParallel, Is.False);
            _mockProgressLog.Received().Warn(Arg.Is<string>(s => s.Contains("AllowParallel")));
        }

        [Test]
        public void RegularTemplate_ContinueOnSchemaFailureFalse_WarnsButLoads()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""Main"",
                ""ContinueOnSchemaFailure"": false
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            var template = Template.Load("Main", MakeProduct(Platform.SqlServer));

            Assert.That(template, Is.Not.Null);
            _mockProgressLog.Received().Warn(Arg.Is<string>(s => s.Contains("ContinueOnSchemaFailure")));
        }

        [Test]
        public void SchemaTemplate_SchemaOnlyFieldsNonDefault_NoWarnings()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""Main"",
                ""SchemaIdentificationScript"": ""SELECT schema_name()"",
                ""AllowParallel"": false,
                ""ContinueOnSchemaFailure"": false
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            Template.Load("Main", MakeProduct(Platform.SqlServer));

            _mockProgressLog.DidNotReceive().Warn(Arg.Any<string>());
        }

        [Test]
        public void RegularTemplate_ContinueOnDatabaseFailureFalse_NoWarning()
        {
            // ContinueOnDatabaseFailure applies universally — it's DB-level, not schema-template-specific.
            // Setting it non-default on a regular template is fine; no warning.
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""Main"",
                ""ContinueOnDatabaseFailure"": false
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            var template = Template.Load("Main", MakeProduct(Platform.SqlServer));

            Assert.That(template, Is.Not.Null);
            Assert.That(template.ContinueOnDatabaseFailure, Is.False);
            _mockProgressLog.DidNotReceive().Warn(
                Arg.Is<string>(s => s.Contains("ContinueOnDatabaseFailure")));
        }

        // -------------------------------------------------------------------
        // GetDefaultTemplateFolders overload omits database-scoped folders for schema templates.
        // -------------------------------------------------------------------
        [Test]
        public void GetDefaultTemplateFolders_SqlServer_SchemaTemplate_OmitsDatabaseScopedFolders()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer, isSchemaTemplate: true);

            Assert.That(folders, Has.None.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.Schemas));
            Assert.That(folders, Has.None.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.DDLTriggers));
            Assert.That(folders, Has.None.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.FullTextCatalogs));
            Assert.That(folders, Has.None.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.FullTextStopLists));
            // Non-database-scoped types remain.
            Assert.That(folders, Has.Some.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.Views));
            Assert.That(folders, Has.Some.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.Procedures));
        }

        [Test]
        public void GetDefaultTemplateFolders_PostgreSQL_SchemaTemplate_OmitsSchemasFolder()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL, isSchemaTemplate: true);

            Assert.That(folders, Has.None.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.Schemas));
            // Non-database-scoped types remain.
            Assert.That(folders, Has.Some.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.Views));
        }

        [Test]
        public void GetDefaultTemplateFolders_SqlServer_RegularTemplate_StillIncludesDatabaseScopedFolders()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer, isSchemaTemplate: false);

            Assert.That(folders, Has.Some.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.Schemas));
            Assert.That(folders, Has.Some.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.DDLTriggers));
        }

        [Test]
        public void GetDefaultTemplateFolders_SingleArgOverload_PreservedAsRegularTemplateBehavior()
        {
            // Existing callers (SchemaTongs, FolderMappingConfig, tests) use the single-arg form;
            // it must keep returning the regular-template set.
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);

            Assert.That(folders, Has.Some.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.Schemas));
            Assert.That(folders, Has.Some.Matches<TemplateFolder>(f => f.ObjectType == ScriptObjectType.DDLTriggers));
        }

        // -------------------------------------------------------------------
        // SchemaTemplate with no ScriptFolders gets the schema-template default set automatically.
        // -------------------------------------------------------------------
        [Test]
        public void SchemaTemplate_EmptyScriptFolders_PopulatedFromSchemaTemplateDefaults()
        {
            var (templateFile, tablesPath) = Paths();
            var json = @"{
                ""Name"": ""TenantBody"",
                ""SchemaIdentificationScript"": ""SELECT 'tenant_a'""
            }";
            _mockFile.Exists(templateFile).Returns(true);
            _mockFile.ReadAllText(templateFile).Returns(json);
            _mockDirectory.Exists(tablesPath).Returns(false);
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            var template = Template.Load("Main", MakeProduct(Platform.SqlServer));

            // Auto-populated default set must NOT include database-scoped folder types.
            Assert.That(template.ScriptFolders, Has.None.Matches<TemplateFolder>(
                f => f.ObjectType == ScriptObjectType.Schemas));
            Assert.That(template.ScriptFolders, Has.None.Matches<TemplateFolder>(
                f => f.ObjectType == ScriptObjectType.DDLTriggers));
            Assert.That(template.ScriptFolders, Has.None.Matches<TemplateFolder>(
                f => f.ObjectType == ScriptObjectType.FullTextCatalogs));
            Assert.That(template.ScriptFolders, Has.None.Matches<TemplateFolder>(
                f => f.ObjectType == ScriptObjectType.FullTextStopLists));
        }
    }
}
