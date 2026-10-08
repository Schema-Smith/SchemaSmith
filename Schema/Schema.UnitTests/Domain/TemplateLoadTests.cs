// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Domain.PostgreSQL;
using Schema.Domain.MySQL;
using Schema.Isolators;
using Schema.Delivery;
using Schema.Utility;

namespace Schema.UnitTests.Domain
{
    [TestFixture]
    public class TemplateLoadTests
    {
        private IFile _mockFile;
        private IDirectory _mockDirectory;

        [SetUp]
        public void SetUp()
        {
            FactoryContainer.Clear();
            _mockFile = Substitute.For<IFile>();
            _mockDirectory = Substitute.For<IDirectory>();
            FactoryContainer.Register<IFile>(_mockFile);
            FactoryContainer.Register<IDirectory>(_mockDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            FactoryContainer.Clear();
        }

        // The tolerant load must record EVERY component it could not load. It used to record only files that
        // were not valid JSON at all, so the most common authoring mistake -- valid JSON carrying an
        // unrecognised property, exactly what strict deserialization rejects -- was caught, skipped, and
        // never reported: the table just vanished from the template. That made the tolerant path strictly
        // worse than the strict one, which at least fails loudly naming the file.
        [Test]
        public void Load_Tolerant_RecordsAComponentThatIsValidJsonButFailsToLoad()
        {
            var templateJson = @"{ ""Name"": ""Main"", ""DatabaseIdentificationScript"": ""SELECT 1"" }";
            var templatePath = Path.Join("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);

            var tablesPath = Path.Join("C:", "products", "Templates", "Main", "Tables");
            var typoFile = Path.Join(tablesPath, "dbo.TypoProperty.json");
            _mockDirectory.Exists(tablesPath).Returns(true);
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories).Returns(new[] { typoFile });
            _mockFile.Exists(typoFile).Returns(true);
            // Well-formed JSON; 'Colums' is not a property of a table.
            _mockFile.ReadAllText(typoFile)
                .Returns(@"{ ""Name"": ""[T]"", ""Colums"": [] }");

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.SqlServer,
                FilePath = Path.Join("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product, tolerateComponentLoadErrors: true);

            Assert.Multiple(() =>
            {
                Assert.That(template.Tables, Is.Empty, "the component did not load");
                Assert.That(template.ComponentLoadErrors, Has.Exactly(1).Items,
                    "a component that did not load must be reported, not silently dropped");
                Assert.That(template.ComponentLoadErrors[0].FilePath, Is.EqualTo(typoFile));
                Assert.That(template.ComponentLoadErrors[0].IsValidJson, Is.True,
                    "classified as parseable so --Validate can leave it to JsonSchemaCheck's precise SS-JSON-001");
            });
        }

        // SchemaIdentificationScript must be token-replaced exactly like DatabaseIdentificationScript. The reference
        // docs say the identification scripts "can interpolate query-tokens", and three of them did -- this one
        // ran verbatim, so SELECT '{{TenantName}}' reached SchemaDiscovery as the literal text and the deploy
        // refused the discovered name for containing '{'. Asserted as AGREEMENT with the database script rather
        // than against a hand-written literal, so the test pins "same treatment" and not the quoting rule.
        [Test]
        public void Load_TokenReplacesSchemaIdentificationScript_TheSameWayAsDatabaseIdentificationScript()
        {
            var templateJson = @"{
                ""Name"": ""Tenants"",
                ""DatabaseIdentificationScript"": ""SELECT '{{TenantName}}'"",
                ""SchemaIdentificationScript"": ""SELECT '{{TenantName}}'"",
                ""ScriptTokens"": { ""TenantName"": ""acme"" }
            }";
            var templatePath = Path.Join("C:", "products", "Templates", "Tenants", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);
            _mockDirectory.Exists(Path.Join("C:", "products", "Templates", "Tenants", "Tables")).Returns(false);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.SqlServer,
                FilePath = Path.Join("C:", "products", "Product.json")
            };

            var template = Template.Load("Tenants", product);

            Assert.That(template.SchemaIdentificationScript, Does.Not.Contain("{{"),
                "SchemaIdentificationScript still carries the raw token -- discovery will run it verbatim");
            Assert.That(template.SchemaIdentificationScript, Does.Contain("acme"));
            Assert.That(template.SchemaIdentificationScript, Is.EqualTo(template.DatabaseIdentificationScript),
                "the two identification scripts must receive identical token treatment");
        }

        // The replacement must not turn an ABSENT script into an empty one. IsSchemaTemplate reads
        // IsNullOrWhiteSpace so either is safe today, but a null is what "not a schema template" means.
        [Test]
        public void Load_LeavesAnAbsentSchemaIdentificationScriptNull()
        {
            var templateJson = @"{ ""Name"": ""Main"", ""DatabaseIdentificationScript"": ""SELECT 1"" }";
            var templatePath = Path.Join("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);
            _mockDirectory.Exists(Path.Join("C:", "products", "Templates", "Main", "Tables")).Returns(false);

            var template = Template.Load("Main", new Product
            {
                Name = "TestProduct", Platform = Platform.SqlServer, FilePath = Path.Join("C:", "products", "Product.json")
            });

            Assert.That(template.SchemaIdentificationScript, Is.Null);
            Assert.That(template.IsSchemaTemplate, Is.False);
        }


        [Test]
        public void Load_SqlServer_ReturnsTemplateWithCorrectProperties()
        {
            var templateJson = @"{
                ""Name"": ""Main"",
                ""DatabaseIdentificationScript"": ""SELECT 1"",
                ""ScriptTokens"": {}
            }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);
            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(false);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.SqlServer,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template, Is.Not.Null);
            Assert.That(template.Name, Is.EqualTo("Main"));
            Assert.That(template.DatabaseIdentificationScript, Is.EqualTo("SELECT 1"));
            Assert.That(template.Product, Is.SameAs(product));
        }

        [Test]
        public void Load_SetsFilePath()
        {
            var templateJson = @"{ ""Name"": ""Main"" }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);
            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(false);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.SqlServer,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template.FilePath, Is.EqualTo(templatePath));
        }

        [Test]
        public void Load_LoadsTablesFromDirectory()
        {
            var templateJson = @"{ ""Name"": ""Main"" }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);

            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(true);

            var tableFile = Path.Combine(tablesPath, "dbo.TestTable.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { tableFile });

            var tableJson = @"{ ""Name"": ""[TestTable]"", ""Columns"": [] }";
            _mockFile.Exists(tableFile).Returns(true);
            _mockFile.ReadAllText(tableFile).Returns(tableJson);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.SqlServer,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template.Tables, Has.Count.EqualTo(1));
            Assert.That(template.Tables[0], Is.InstanceOf<SqlServerTable>());
            Assert.That(template.Tables[0].Name, Is.EqualTo("[TestTable]"));
        }

        [Test]
        public void Load_PostgreSQL_LoadsTablesAsPostgreSqlTable()
        {
            var templateJson = @"{ ""Name"": ""Main"" }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);

            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(true);

            var tableFile = Path.Combine(tablesPath, "public.TestTable.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { tableFile });

            var tableJson = @"{ ""Name"": ""TestTable"", ""Schema"": ""public"", ""Columns"": [] }";
            _mockFile.Exists(tableFile).Returns(true);
            _mockFile.ReadAllText(tableFile).Returns(tableJson);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.PostgreSQL,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template.Tables, Has.Count.EqualTo(1));
            Assert.That(template.Tables[0], Is.InstanceOf<PostgreSqlTable>());
        }

        [Test]
        public void Load_MySQL_LoadsTablesAsMySqlTable()
        {
            var templateJson = @"{ ""Name"": ""Main"" }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);

            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(true);

            var tableFile = Path.Combine(tablesPath, "TestTable.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { tableFile });

            var tableJson = @"{ ""Name"": ""`TestTable`"", ""Engine"": ""InnoDB"", ""Columns"": [] }";
            _mockFile.Exists(tableFile).Returns(true);
            _mockFile.ReadAllText(tableFile).Returns(tableJson);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.MySQL,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template.Tables, Has.Count.EqualTo(1));
            Assert.That(template.Tables[0], Is.InstanceOf<MySqlTable>());
        }

        [Test]
        public void Load_NoTablesDirectory_ReturnsEmptyTables()
        {
            var templateJson = @"{ ""Name"": ""Main"" }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);

            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(false);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.SqlServer,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template.Tables, Is.Empty);
        }

        [Test]
        public void Load_ThrowsWhenTemplateFileNotFound()
        {
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(false);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.SqlServer,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            Assert.Throws<RunFailedException>(() => Template.Load("Main", product));
        }

        [Test]
        public void Load_SortsTablesByFileName()
        {
            var templateJson = @"{ ""Name"": ""Main"" }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);

            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(true);

            var tableFileB = Path.Combine(tablesPath, "B_Table.json");
            var tableFileA = Path.Combine(tablesPath, "A_Table.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { tableFileB, tableFileA });

            _mockFile.Exists(tableFileA).Returns(true);
            _mockFile.ReadAllText(tableFileA).Returns(@"{ ""Name"": ""A_Table"", ""Columns"": [] }");
            _mockFile.Exists(tableFileB).Returns(true);
            _mockFile.ReadAllText(tableFileB).Returns(@"{ ""Name"": ""B_Table"", ""Columns"": [] }");

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.SqlServer,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template.Tables, Has.Count.EqualTo(2));
            Assert.That(template.Tables[0].Name, Is.EqualTo("A_Table"));
            Assert.That(template.Tables[1].Name, Is.EqualTo("B_Table"));
        }
        [Test]
        public void Load_ScriptFoldersDeserializedFromJson()
        {
            var templateJson = @"{
                ""Name"": ""Main"",
                ""ScriptFolders"": [
                    { ""FolderPath"": ""FullTextCatalogs"", ""QuenchSlot"": ""Objects"" },
                    { ""FolderPath"": ""MigrationScripts/Before"", ""QuenchSlot"": ""Before"" }
                ]
            }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);
            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(false);
            // ScriptFolder.LoadSqlFiles checks directory existence
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.SqlServer,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template.ScriptFolders, Has.Count.EqualTo(2));
            Assert.That(template.ScriptFolders[0].FolderPath, Is.EqualTo("FullTextCatalogs"));
            Assert.That(template.ScriptFolders[0].QuenchSlot, Is.EqualTo(TemplateQuenchSlot.Objects));
            Assert.That(template.ScriptFolders[1].FolderPath, Is.EqualTo("MigrationScripts/Before"));
            Assert.That(template.ScriptFolders[1].QuenchSlot, Is.EqualTo(TemplateQuenchSlot.Before));
        }

        [Test]
        public void ApplyLegacyFolderFallbacks_FallsBackToLegacyFolders_WhenNewFoldersDoNotExist()
        {
            var template = new Template();
            template.ScriptFolders.AddRange(Template.GetDefaultTemplateFolders(Platform.SqlServer));

            var templateDir = Path.Combine("C:", "products", "Templates", "Main");

            // New folder names do NOT exist on disk
            _mockDirectory.Exists(Path.Combine(templateDir, "Before Scripts")).Returns(false);
            _mockDirectory.Exists(Path.Combine(templateDir, "After Scripts")).Returns(false);
            _mockDirectory.Exists(Path.Combine(templateDir, "Table Data")).Returns(false);

            // Legacy folder names DO exist on disk
            _mockDirectory.Exists(Path.Combine(templateDir, "MigrationScripts/Before")).Returns(true);
            _mockDirectory.Exists(Path.Combine(templateDir, "MigrationScripts/After")).Returns(true);
            _mockDirectory.Exists(Path.Combine(templateDir, "TableData")).Returns(true);

            // All other folders exist (to avoid false fallback matches)
            _mockDirectory.Exists(Arg.Is<string>(s =>
                !s.EndsWith("Before Scripts") && !s.EndsWith("After Scripts") &&
                !s.EndsWith("Table Data") && !s.EndsWith("MigrationScripts/Before") &&
                !s.EndsWith("MigrationScripts/After") && !s.EndsWith("TableData"))).Returns(true);

            template.ApplyLegacyFolderFallbacks(templateDir);

            Assert.That(template.ScriptFolders.First(f => f.QuenchSlot == TemplateQuenchSlot.Before).FolderPath,
                Is.EqualTo("MigrationScripts/Before"));
            Assert.That(template.ScriptFolders.First(f => f.QuenchSlot == TemplateQuenchSlot.After).FolderPath,
                Is.EqualTo("MigrationScripts/After"));
            Assert.That(template.ScriptFolders.First(f => f.QuenchSlot == TemplateQuenchSlot.TableData).FolderPath,
                Is.EqualTo("TableData"));
        }

        [Test]
        public void ApplyLegacyFolderFallbacks_KeepsNewFolders_WhenTheyExistOnDisk()
        {
            var template = new Template();
            template.ScriptFolders.AddRange(Template.GetDefaultTemplateFolders(Platform.SqlServer));

            var templateDir = Path.Combine("C:", "products", "Templates", "Main");

            // New folder names exist on disk
            _mockDirectory.Exists(Arg.Any<string>()).Returns(true);

            template.ApplyLegacyFolderFallbacks(templateDir);

            Assert.That(template.ScriptFolders.First(f => f.QuenchSlot == TemplateQuenchSlot.Before).FolderPath,
                Is.EqualTo("Before Scripts"));
            Assert.That(template.ScriptFolders.First(f => f.QuenchSlot == TemplateQuenchSlot.After).FolderPath,
                Is.EqualTo("After Scripts"));
            Assert.That(template.ScriptFolders.First(f => f.QuenchSlot == TemplateQuenchSlot.TableData).FolderPath,
                Is.EqualTo("Table Data"));
        }

        [Test]
        public void Load_PostgreSQL_LoadsMaterializedViews()
        {
            var templateJson = @"{ ""Name"": ""Main"" }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);

            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(false);

            var matViewsPath = Path.Combine("C:", "products", "Templates", "Main", "Materialized Views");
            _mockDirectory.Exists(matViewsPath).Returns(true);

            var mvFile = Path.Combine(matViewsPath, "public.mv_test.json");
            _mockDirectory.GetFiles(matViewsPath, "*.json", SearchOption.AllDirectories)
                .Returns(new[] { mvFile });

            var mvJson = @"{""Name"":""mv_test"",""Schema"":""public"",""Definition"":""SELECT 1""}";
            _mockFile.ReadAllText(mvFile).Returns(mvJson);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.PostgreSQL,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template.MaterializedViews, Has.Count.EqualTo(1));
            Assert.That(template.MaterializedViews[0].Name, Is.EqualTo("mv_test"));
            Assert.That(template.MaterializedViews[0], Is.InstanceOf<PostgreSqlMaterializedView>());
        }

        [Test]
        public void Load_SqlServer_SkipsMaterializedViews()
        {
            var templateJson = @"{ ""Name"": ""Main"" }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);

            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(false);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.SqlServer,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template.MaterializedViews, Is.Empty);
        }

        [Test]
        public void Load_PostgreSQL_NoMaterializedViewsDirectory_ReturnsEmptyList()
        {
            var templateJson = @"{ ""Name"": ""Main"" }";
            var templatePath = Path.Combine("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(templateJson);

            var tablesPath = Path.Combine("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(false);

            var matViewsPath = Path.Combine("C:", "products", "Templates", "Main", "Materialized Views");
            _mockDirectory.Exists(matViewsPath).Returns(false);

            var product = new Product
            {
                Name = "TestProduct",
                Platform = Platform.PostgreSQL,
                FilePath = Path.Combine("C:", "products", "Product.json")
            };

            var template = Template.Load("Main", product);

            Assert.That(template.MaterializedViews, Is.Empty);
        }

        [Test]
        public void ApplyLegacyFolderFallbacks_KeepsNewFolders_WhenNeitherExistsOnDisk()
        {
            var template = new Template();
            template.ScriptFolders.AddRange(Template.GetDefaultTemplateFolders(Platform.SqlServer));

            var templateDir = Path.Combine("C:", "products", "Templates", "Main");

            // Neither new nor legacy folders exist
            _mockDirectory.Exists(Arg.Any<string>()).Returns(false);

            template.ApplyLegacyFolderFallbacks(templateDir);

            // Should keep the new standardized names (no fallback target exists either)
            Assert.That(template.ScriptFolders.First(f => f.QuenchSlot == TemplateQuenchSlot.Before).FolderPath,
                Is.EqualTo("Before Scripts"));
            Assert.That(template.ScriptFolders.First(f => f.QuenchSlot == TemplateQuenchSlot.After).FolderPath,
                Is.EqualTo("After Scripts"));
            Assert.That(template.ScriptFolders.First(f => f.QuenchSlot == TemplateQuenchSlot.TableData).FolderPath,
                Is.EqualTo("Table Data"));
        }
    }
}
