// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;

namespace Schema.UnitTests.Domain
{
    [TestFixture]
    public class TemplateTests
    {
        [Test]
        public void DefaultValues_AreCorrect()
        {
            var template = new Template();

            Assert.That(template.Name, Is.EqualTo(""));
            Assert.That(template.DatabaseIdentificationScript, Is.Null);
            Assert.That(template.VersionStampScript, Is.Null);
            Assert.That(template.IndexOnlyTableQuenches, Is.False);
            Assert.That(template.ScriptFolders, Is.Not.Null);
            Assert.That(template.ScriptFolders, Is.Empty);
            Assert.That(template.ScriptTokens, Is.Not.Null);
            Assert.That(template.ScriptTokens, Is.Empty);
            Assert.That(template.UpdateFillFactor, Is.True);
            Assert.That(template.BaselineValidationScript, Is.Null);
            Assert.That(template.RequireAtLeastOneTarget, Is.True);
            Assert.That(template.Product, Is.Null);
            Assert.That(template.Tables, Is.Not.Null);
            Assert.That(template.Tables, Is.Empty);
            Assert.That(template.TableSchema, Is.EqualTo(""));
            Assert.That(template.FilePath, Is.Null);
            Assert.That(template.QueryTokens, Is.Not.Null);
            Assert.That(template.NonQueryTokens, Is.Not.Null);
            Assert.That(template.LoggableTokens, Is.Not.Null);
        }

        [Test]
        public void JsonRoundTrip_PreservesSerializedProperties()
        {
            var template = new Template
            {
                Name = "Default",
                DatabaseIdentificationScript = "SELECT DB_NAME()",
                IdentificationDatabase = "ControlDb",
                VersionStampScript = "EXEC UpdateVersion",
                IndexOnlyTableQuenches = true,
                UpdateFillFactor = false,
                BaselineValidationScript = "SELECT 1",
                RequireAtLeastOneTarget = false,
                ScriptTokens = new Dictionary<string, string> { { "DB", "TestDB" } }
            };

            var json = JsonConvert.SerializeObject(template);
            var deserialized = JsonConvert.DeserializeObject<Template>(json);

            Assert.That(deserialized.Name, Is.EqualTo("Default"));
            Assert.That(deserialized.DatabaseIdentificationScript, Is.EqualTo("SELECT DB_NAME()"));
            Assert.That(deserialized.IdentificationDatabase, Is.EqualTo("ControlDb"));
            Assert.That(deserialized.VersionStampScript, Is.EqualTo("EXEC UpdateVersion"));
            Assert.That(deserialized.IndexOnlyTableQuenches, Is.True);
            Assert.That(deserialized.UpdateFillFactor, Is.False);
            Assert.That(deserialized.BaselineValidationScript, Is.EqualTo("SELECT 1"));
            Assert.That(deserialized.RequireAtLeastOneTarget, Is.False);
            Assert.That(deserialized.ScriptTokens["DB"], Is.EqualTo("TestDB"));
        }

        [Test]
        public void JsonIgnored_PropertiesNotSerialized()
        {
            var template = new Template
            {
                Name = "Test",
                FilePath = "/some/path",
                TableSchema = "dbo"
            };
            template.Product = new Product { Name = "TestProduct" };

            var json = JsonConvert.SerializeObject(template);

            Assert.That(json, Does.Not.Contain("FilePath"));
            Assert.That(json, Does.Not.Contain("TableSchema"));
            Assert.That(json, Does.Not.Contain("\"Product\":"));
            Assert.That(json, Does.Not.Contain("\"Tables\":"));
            Assert.That(json, Does.Not.Contain("QueryTokens"));
            Assert.That(json, Does.Not.Contain("NonQueryTokens"));
            Assert.That(json, Does.Not.Contain("LoggableTokens"));
        }

        [Test]
        public void Clone_ReturnsDeepCopy_WithIndependentCollections()
        {
            var product = new Product { Name = "TestProduct" };
            var original = new Template
            {
                Name = "Test",
                FilePath = "/some/path",
                TableSchema = "schema_json",
                DatabaseIdentificationScript = "SELECT DB_NAME()",
                IdentificationDatabase = "ControlDb",
                VersionStampScript = "EXEC UpdateVersion",
                ScriptTokens = new Dictionary<string, string> { { "DB", "TestDB" } },
            };
            original.Product = product;
            original.Tables.Add(new Table { Name = "Users" });
            original.ScriptFolders.Add(new TemplateFolder { FolderPath = "Objects", QuenchSlot = TemplateQuenchSlot.Objects });
            original.QueryTokens = new Dictionary<string, string> { { "QToken", "QValue" } };
            original.NonQueryTokens = new Dictionary<string, string> { { "NQToken", "NQValue" } };
            original.LoggableTokens = new Dictionary<string, string> { { "LToken", "LValue" } };

            var clone = original.Clone();

            // Same values
            Assert.That(clone.Name, Is.EqualTo("Test"));
            Assert.That(clone.FilePath, Is.EqualTo("/some/path"));
            Assert.That(clone.TableSchema, Is.EqualTo("schema_json"));
            Assert.That(clone.DatabaseIdentificationScript, Is.EqualTo("SELECT DB_NAME()"));
            Assert.That(clone.IdentificationDatabase, Is.EqualTo("ControlDb"));
            Assert.That(clone.VersionStampScript, Is.EqualTo("EXEC UpdateVersion"));
            Assert.That(clone.Product, Is.SameAs(product));
            Assert.That(clone.Tables, Has.Count.EqualTo(1));
            Assert.That(clone.ScriptFolders, Has.Count.EqualTo(1));
            Assert.That(clone.QueryTokens, Has.Count.EqualTo(1));
            Assert.That(clone.NonQueryTokens, Has.Count.EqualTo(1));
            Assert.That(clone.LoggableTokens, Has.Count.EqualTo(1));
            Assert.That(clone.ScriptTokens["DB"], Is.EqualTo("TestDB"));

            // Independent references
            Assert.That(clone, Is.Not.SameAs(original));
            Assert.That(clone.Tables, Is.Not.SameAs(original.Tables));
            Assert.That(clone.ScriptFolders, Is.Not.SameAs(original.ScriptFolders));
            Assert.That(clone.QueryTokens, Is.Not.SameAs(original.QueryTokens));
            Assert.That(clone.NonQueryTokens, Is.Not.SameAs(original.NonQueryTokens));
            Assert.That(clone.LoggableTokens, Is.Not.SameAs(original.LoggableTokens));

            // Mutating clone does not affect original
            clone.Tables.Clear();
            Assert.That(original.Tables, Has.Count.EqualTo(1));

            clone.ScriptFolders.Clear();
            Assert.That(original.ScriptFolders, Has.Count.EqualTo(1));

            clone.QueryTokens.Clear();
            Assert.That(original.QueryTokens, Has.Count.EqualTo(1));
        }

        [Test]
        public void MaterializedViews_DefaultsToEmptyList()
        {
            var template = new Template();
            Assert.That(template.MaterializedViews, Is.Not.Null);
            Assert.That(template.MaterializedViews, Is.Empty);
        }

        [Test]
        public void MaterializedViewSchema_DefaultsToEmptyArray()
        {
            var template = new Template();
            Assert.That(template.MaterializedViewSchema, Is.EqualTo("[]"));
        }

        [Test]
        public void Clone_CopiesMaterializedViews()
        {
            var original = new Template { Name = "Test" };
            original.Product = new Product { Name = "P" };
            original.MaterializedViews.Add(new PostgreSqlMaterializedView { Name = "mv_test", Definition = "SELECT 1" });
            original.MaterializedViewSchema = "[{\"Name\":\"mv_test\"}]";

            var clone = original.Clone();

            Assert.That(clone.MaterializedViews, Has.Count.EqualTo(1));
            Assert.That(clone.MaterializedViews[0].Name, Is.EqualTo("mv_test"));
            Assert.That(clone.MaterializedViewSchema, Is.EqualTo("[{\"Name\":\"mv_test\"}]"));
            Assert.That(clone.MaterializedViews, Is.Not.SameAs(original.MaterializedViews));

            // Mutating clone does not affect original
            clone.MaterializedViews.Clear();
            Assert.That(original.MaterializedViews, Has.Count.EqualTo(1));
        }

        [Test]
        public void JsonIgnored_MaterializedViewProperties_NotSerialized()
        {
            var template = new Template { Name = "Test" };
            template.MaterializedViews.Add(new PostgreSqlMaterializedView { Name = "mv" });
            template.MaterializedViewSchema = "[{\"Name\":\"mv\"}]";

            var json = JsonConvert.SerializeObject(template);

            Assert.That(json, Does.Not.Contain("MaterializedViews"));
            Assert.That(json, Does.Not.Contain("MaterializedViewSchema"));
        }

        [Test]
        public void IndexedViews_DefaultsToEmptyList()
        {
            var template = new Template();
            Assert.That(template.IndexedViews, Is.Not.Null);
            Assert.That(template.IndexedViews, Is.Empty);
        }

        [Test]
        public void IndexedViewSchema_DefaultsToEmptyArray()
        {
            var template = new Template();
            Assert.That(template.IndexedViewSchema, Is.EqualTo("[]"));
        }

        #region A2 — model-payload XML twins

        [Test]
        public void TableXml_DerivesFromTableSchema_AsWellFormedIngestXml()
        {
            var template = new Template { TableSchema = "[{\"Name\":\"[Orders]\",\"Note\":\"a'b\"}]" };

            var xml = template.TableXml;

            Assert.Multiple(() =>
            {
                Assert.DoesNotThrow(() => System.Xml.Linq.XElement.Parse(xml), "Twin must be well-formed XML.");
                Assert.That(xml, Does.StartWith("<Tables"), "Rooted at <Tables> with repeated <Table> children.");
                Assert.That(xml, Does.Contain("<Table>"));
                Assert.That(xml, Does.Contain("[Orders]"));
                // The property is RAW (unescaped) exactly like the TableSchema property — single-quote
                // escaping for SQL embedding happens at token-add time, mirroring the JSON sibling. A
                // pre-escaped property would be the #301 double-escape bug.
                Assert.That(xml, Does.Contain("a'b"));
                Assert.That(xml, Does.Not.Contain("a''b"));
            });
        }

        [Test]
        public void ModelPayloadXmlTwins_EmptyModel_ProduceWellFormedEmptyRoots()
        {
            var template = new Template();

            Assert.Multiple(() =>
            {
                Assert.That(template.TableXml, Does.StartWith("<Tables"));
                Assert.That(template.MaterializedViewXml, Does.StartWith("<MaterializedViews"));
                Assert.That(template.IndexedViewXml, Does.StartWith("<IndexedViews"));
                Assert.DoesNotThrow(() => System.Xml.Linq.XElement.Parse(template.TableXml));
                Assert.DoesNotThrow(() => System.Xml.Linq.XElement.Parse(template.MaterializedViewXml));
                Assert.DoesNotThrow(() => System.Xml.Linq.XElement.Parse(template.IndexedViewXml));
            });
        }

        [Test]
        public void ModelPayloadXmlTwins_NotSerialized()
        {
            var template = new Template { Name = "T", TableSchema = "[{\"Name\":\"X\"}]" };

            var json = JsonConvert.SerializeObject(template);

            Assert.That(json, Does.Not.Contain("TableXml"));
            Assert.That(json, Does.Not.Contain("IndexedViewXml"));
            Assert.That(json, Does.Not.Contain("MaterializedViewXml"));
        }

        #endregion

        [Test]
        public void Clone_CopiesIndexedViews()
        {
            var original = new Template { Name = "Test" };
            original.Product = new Product { Name = "P" };
            original.IndexedViews.Add(new SqlServerIndexedView { Name = "[iv_test]", Definition = "SELECT 1" });
            original.IndexedViewSchema = "[{\"Name\":\"[iv_test]\"}]";

            var clone = original.Clone();

            Assert.That(clone.IndexedViews, Has.Count.EqualTo(1));
            Assert.That(clone.IndexedViews[0].Name, Is.EqualTo("[iv_test]"));
            Assert.That(clone.IndexedViewSchema, Is.EqualTo("[{\"Name\":\"[iv_test]\"}]"));
            Assert.That(clone.IndexedViews, Is.Not.SameAs(original.IndexedViews));

            // Mutating clone does not affect original
            clone.IndexedViews.Clear();
            Assert.That(original.IndexedViews, Has.Count.EqualTo(1));
        }

        [Test]
        public void Clone_CopiesIndexedViewSchema()
        {
            var original = new Template { Name = "Test" };
            original.Product = new Product { Name = "P" };
            original.IndexedViewSchema = "[{\"Name\":\"[iv_test]\"}]";

            var clone = original.Clone();

            Assert.That(clone.IndexedViewSchema, Is.EqualTo("[{\"Name\":\"[iv_test]\"}]"));
        }

        [Test]
        public void JsonIgnored_IndexedViewProperties_NotSerialized()
        {
            var template = new Template { Name = "Test" };
            template.IndexedViews.Add(new SqlServerIndexedView { Name = "[iv]" });
            template.IndexedViewSchema = "[{\"Name\":\"[iv]\"}]";

            var json = JsonConvert.SerializeObject(template);

            Assert.That(json, Does.Not.Contain("IndexedViews"));
            Assert.That(json, Does.Not.Contain("IndexedViewSchema"));
        }

        [Test]
        public void GetDefaultTemplateFolders_SqlServer_ReturnsExpectedFolders()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            Assert.That(folders, Has.Count.GreaterThan(0));
            Assert.That(folders.Any(f => f.QuenchSlot == TemplateQuenchSlot.Objects), Is.True);
            Assert.That(folders.Any(f => f.FolderPath == "Schemas"), Is.True);
            Assert.That(folders.Any(f => f.FolderPath == "DDLTriggers"), Is.True);
        }

        [Test]
        public void GetDefaultTemplateFolders_SqlServer_UsesStandardizedFolderNames()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            Assert.That(folders.First(f => f.QuenchSlot == TemplateQuenchSlot.Before).FolderPath, Is.EqualTo("Before Scripts"));
            Assert.That(folders.First(f => f.QuenchSlot == TemplateQuenchSlot.After).FolderPath, Is.EqualTo("After Scripts"));
            Assert.That(folders.First(f => f.QuenchSlot == TemplateQuenchSlot.TableData).FolderPath, Is.EqualTo("Table Data"));
        }

        [Test]
        public void GetDefaultTemplateFolders_SqlServer_RunsPrerequisitesFirst_InTheObjectsPass_AndNotInASchemaTemplate()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            Assert.Multiple(() =>
            {
                Assert.That(folders[0].FolderPath, Is.EqualTo("Prerequisites"), "first of the Objects folders");
                Assert.That(folders[0].QuenchSlot, Is.EqualTo(TemplateQuenchSlot.Objects), "the pass that runs ahead of table creation");
                Assert.That(folders[0].ObjectType, Is.EqualTo(ScriptObjectType.None), "so it runs on every deploy");
                Assert.That(Template.GetDefaultTemplateFolders(Platform.SqlServer, isSchemaTemplate: true).Select(f => f.FolderPath),
                    Has.No.Member("Prerequisites"), "database-scoped, so never fanned out per schema");
                Assert.That(Template.GetDefaultTemplateFolders(Platform.PostgreSQL).Select(f => f.FolderPath), Has.No.Member("Prerequisites"));
                var policies = folders.Single(f => f.ObjectType == ScriptObjectType.SecurityPolicies);
                Assert.That(policies.QuenchSlot, Is.EqualTo(TemplateQuenchSlot.AfterTablesObjects), "after the tables and predicate functions");
                Assert.That(folders.IndexOf(policies), Is.GreaterThan(folders.FindIndex(f => f.ObjectType == ScriptObjectType.SchemaBoundFunctions)));
                Assert.That(Schema.Utility.ScriptFolderTypeInference.InferFromFolderName("SecurityPolicies"), Is.EqualTo(ScriptObjectType.SecurityPolicies));
            });
        }

        [Test]
        public void GetDefaultTemplateFolders_SqlServer_DoesNotContainLegacyFolderNames()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            Assert.That(folders.Any(f => f.FolderPath.Contains("MigrationScripts")), Is.False);
            Assert.That(folders.Any(f => f.FolderPath == "TableData"), Is.False);
        }

        [Test]
        public void GetDefaultTemplateFolders_PostgreSQL_ReturnsExpectedFolders()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            Assert.That(folders, Has.Count.GreaterThan(0));
            Assert.That(folders.Any(f => f.FolderPath == "Domain Types"), Is.True);
            Assert.That(folders.Any(f => f.FolderPath == "Trigger Functions"), Is.True);
            Assert.That(folders.Any(f => f.FolderPath == "Sequences"), Is.True);
        }

        [Test]
        public void GetDefaultTemplateFolders_MySQL_ReturnsExpectedFolders()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            Assert.That(folders, Has.Count.GreaterThan(0));
            Assert.That(folders.Any(f => f.FolderPath == "Events"), Is.True);
            Assert.That(folders.Any(f => f.FolderPath.Contains("Schemas")), Is.False);
        }

        [Test]
        public void Clone_DeepClonesQueryTokens_ForPerDatabaseResolution()
        {
            var original = new Template { Name = "Test" };
            original.Product = new Product { Name = "P" };
            original.QueryTokens = new Dictionary<string, string>
            {
                { "<*Query*>ServerVersion", "SELECT @@VERSION" },
                { "<*Query*>DbId", "SELECT DB_ID()" }
            };

            var clone = original.Clone();

            // Modify clone's query tokens
            clone.QueryTokens["<*Query*>ServerVersion"] = "RESOLVED_VALUE";

            // Original should be unaffected
            Assert.That(original.QueryTokens["<*Query*>ServerVersion"], Is.EqualTo("SELECT @@VERSION"));
        }

        // I9: Clone() must copy all slice-3 fields (SchemaIdentificationScript,
        // CreateSchemaIfMissing, AllowParallel, ContinueOnSchemaFailure,
        // ContinueOnDatabaseFailure) and recompute the per-token scope map.
        [Test]
        public void Clone_CopiesSlice3SchemaTemplateFields()
        {
            var original = new Template
            {
                Name = "TenantBody",
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                CreateSchemaIfMissing = true,
                AllowParallel = false,
                ContinueOnSchemaFailure = false,
                ContinueOnDatabaseFailure = false
            };
            original.Product = new Product { Name = "P", Platform = Platform.SqlServer };

            var clone = original.Clone();

            Assert.Multiple(() =>
            {
                Assert.That(clone.SchemaIdentificationScript, Is.EqualTo("SELECT 'tenant_a'"));
                Assert.That(clone.CreateSchemaIfMissing, Is.True);
                Assert.That(clone.AllowParallel, Is.False);
                Assert.That(clone.ContinueOnSchemaFailure, Is.False);
                Assert.That(clone.ContinueOnDatabaseFailure, Is.False);
                Assert.That(clone.IsSchemaTemplate, Is.True);
            });
        }

        [Test]
        public void Clone_RegularTemplate_PreservesDefaultsForSlice3Fields()
        {
            var original = new Template { Name = "Core" };
            original.Product = new Product { Name = "P", Platform = Platform.SqlServer };

            var clone = original.Clone();

            Assert.Multiple(() =>
            {
                Assert.That(clone.SchemaIdentificationScript, Is.Null.Or.Empty);
                Assert.That(clone.CreateSchemaIfMissing, Is.False);
                Assert.That(clone.AllowParallel, Is.True);
                Assert.That(clone.ContinueOnSchemaFailure, Is.True);
                Assert.That(clone.ContinueOnDatabaseFailure, Is.True);
                Assert.That(clone.IsSchemaTemplate, Is.False);
            });
        }

        [Test]
        public void Clone_RebuildsTokenScopes_ForSchemaTemplate()
        {
            // Pre-resolved iteration-scoped token on the original. The clone must rebuild the
            // scope map from the cloned dictionaries, so IsIterationScoped reports correctly
            // even though _tokenScopes was never directly copied.
            var original = new Template
            {
                Name = "TenantBody",
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["TenantId"] = "<*Query*>SELECT TenantId FROM {{SchemaName}}.Config" }
            };
            original.Product = new Product { Name = "P", Platform = Platform.SqlServer };
            original.ResolveTokenScopes();

            var clone = original.Clone();

            Assert.That(clone.IsIterationScoped("TenantId"), Is.True);
        }

        // ---- Per-slot folder accessors (#260, folder-gate support) ----

        [Test]
        public void BeforeFolders_ReturnsOnlyBeforeSlotFolders()
        {
            var template = new Template();
            template.ScriptFolders.Add(new TemplateFolder { FolderPath = "B", QuenchSlot = TemplateQuenchSlot.Before });
            template.ScriptFolders.Add(new TemplateFolder { FolderPath = "A", QuenchSlot = TemplateQuenchSlot.After });

            Assert.That(template.BeforeFolders.Select(f => f.FolderPath), Is.EqualTo(new[] { "B" }));
        }

        [Test]
        public void AfterTablesObjectFolders_IncludesObjectsAndAfterTablesObjects()
        {
            var template = new Template();
            template.ScriptFolders.Add(new TemplateFolder { FolderPath = "O", QuenchSlot = TemplateQuenchSlot.Objects });
            template.ScriptFolders.Add(new TemplateFolder { FolderPath = "ATO", QuenchSlot = TemplateQuenchSlot.AfterTablesObjects });
            template.ScriptFolders.Add(new TemplateFolder { FolderPath = "B", QuenchSlot = TemplateQuenchSlot.Before });

            Assert.That(template.AfterTablesObjectFolders.Select(f => f.FolderPath), Is.EquivalentTo(new[] { "O", "ATO" }));
        }

        [Test]
        public void BeforeScripts_DelegatesToBeforeFolders()
        {
            var template = new Template();
            var folder = new TemplateFolder { FolderPath = "B", QuenchSlot = TemplateQuenchSlot.Before };
            folder.Scripts.Add(new SqlScript { Name = "s1" });
            template.ScriptFolders.Add(folder);

            Assert.That(template.BeforeScripts.Select(s => s.Name), Is.EqualTo(new[] { "s1" }));
        }

        [Test]
        public void DropTablesRemovedFromProduct_AbsentInJson_IsNull()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T" }""");
            Assert.That(template.DropTablesRemovedFromProduct, Is.Null);
        }

        [Test]
        public void DropTablesRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropTablesRemovedFromProduct": false }""");
            Assert.That(template.DropTablesRemovedFromProduct, Is.False);
        }

        [Test]
        public void DropTablesRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropTablesRemovedFromProduct": true }""");
            Assert.That(template.DropTablesRemovedFromProduct, Is.True);
        }

        [Test]
        public void DropUnknownIndexes_AbsentInJson_IsNull()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T" }""");
            Assert.That(template.DropUnknownIndexes, Is.Null);
        }

        [Test]
        public void DropUnknownIndexes_ExplicitFalse_IsFalse()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropUnknownIndexes": false }""");
            Assert.That(template.DropUnknownIndexes, Is.False);
        }

        [Test]
        public void DropUnknownIndexes_ExplicitTrue_IsTrue()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropUnknownIndexes": true }""");
            Assert.That(template.DropUnknownIndexes, Is.True);
        }

        [Test]
        public void DropColumnsRemovedFromProduct_AbsentInJson_IsNull()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T" }""");
            Assert.That(template.DropColumnsRemovedFromProduct, Is.Null);
        }

        [Test]
        public void DropColumnsRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropColumnsRemovedFromProduct": false }""");
            Assert.That(template.DropColumnsRemovedFromProduct, Is.False);
        }

        [Test]
        public void DropColumnsRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropColumnsRemovedFromProduct": true }""");
            Assert.That(template.DropColumnsRemovedFromProduct, Is.True);
        }

        [Test]
        public void DropForeignKeysRemovedFromProduct_AbsentInJson_IsNull()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T" }""");
            Assert.That(template.DropForeignKeysRemovedFromProduct, Is.Null);
        }

        [Test]
        public void DropForeignKeysRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropForeignKeysRemovedFromProduct": false }""");
            Assert.That(template.DropForeignKeysRemovedFromProduct, Is.False);
        }

        [Test]
        public void DropForeignKeysRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropForeignKeysRemovedFromProduct": true }""");
            Assert.That(template.DropForeignKeysRemovedFromProduct, Is.True);
        }

        [Test]
        public void DropCheckConstraintsRemovedFromProduct_AbsentInJson_IsNull()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T" }""");
            Assert.That(template.DropCheckConstraintsRemovedFromProduct, Is.Null);
        }

        [Test]
        public void DropCheckConstraintsRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropCheckConstraintsRemovedFromProduct": false }""");
            Assert.That(template.DropCheckConstraintsRemovedFromProduct, Is.False);
        }

        [Test]
        public void DropCheckConstraintsRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropCheckConstraintsRemovedFromProduct": true }""");
            Assert.That(template.DropCheckConstraintsRemovedFromProduct, Is.True);
        }

        [Test]
        public void DropExcludeConstraintsRemovedFromProduct_AbsentInJson_IsNull()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T" }""");
            Assert.That(template.DropExcludeConstraintsRemovedFromProduct, Is.Null);
        }
        [Test]
        public void DropExcludeConstraintsRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropExcludeConstraintsRemovedFromProduct": false }""");
            Assert.That(template.DropExcludeConstraintsRemovedFromProduct, Is.False);
        }
        [Test]
        public void DropExcludeConstraintsRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropExcludeConstraintsRemovedFromProduct": true }""");
            Assert.That(template.DropExcludeConstraintsRemovedFromProduct, Is.True);
        }
        [Test]
        public void DropStatisticsRemovedFromProduct_AbsentInJson_IsNull()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T" }""");
            Assert.That(template.DropStatisticsRemovedFromProduct, Is.Null);
        }
        [Test]
        public void DropStatisticsRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropStatisticsRemovedFromProduct": false }""");
            Assert.That(template.DropStatisticsRemovedFromProduct, Is.False);
        }
        [Test]
        public void DropStatisticsRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropStatisticsRemovedFromProduct": true }""");
            Assert.That(template.DropStatisticsRemovedFromProduct, Is.True);
        }

        [Test]
        public void DropIndexesRemovedFromProduct_AbsentInJson_IsNull()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T" }""");
            Assert.That(template.DropIndexesRemovedFromProduct, Is.Null);
        }
        [Test]
        public void DropIndexesRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropIndexesRemovedFromProduct": false }""");
            Assert.That(template.DropIndexesRemovedFromProduct, Is.False);
        }
        [Test]
        public void DropIndexesRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var template = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(
                """{ "Name": "T", "DropIndexesRemovedFromProduct": true }""");
            Assert.That(template.DropIndexesRemovedFromProduct, Is.True);
        }
    }
}

