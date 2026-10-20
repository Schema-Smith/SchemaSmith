// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Domain
{
    [TestFixture]
    public class TemplateFolderDefaultTests
    {
        // ── SQL Server ────────────────────────────────────────────────────────────

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_BeforeScripts_HasObjectTypeNone()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "Before Scripts");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.None));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_AfterScripts_HasObjectTypeNone()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "After Scripts");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.None));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_TableData_HasObjectTypeNone()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "Table Data");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.None));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_Views_HasObjectTypeViews()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "Views");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Views));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_Functions_HasObjectTypeFunctions()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "Functions");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Functions));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_Procedures_HasObjectTypeProcedures()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "Procedures");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Procedures));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_Triggers_HasObjectTypeTriggers()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "Triggers");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Triggers));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_Schemas_HasObjectTypeSchemas()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "Schemas");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Schemas));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_DataTypes_HasObjectTypeDataTypes()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "DataTypes");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.DataTypes));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_FullTextCatalogs_HasObjectTypeFullTextCatalogs()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "FullTextCatalogs");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.FullTextCatalogs));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_FullTextStopLists_HasObjectTypeFullTextStopLists()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "FullTextStopLists");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.FullTextStopLists));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_XMLSchemaCollections_HasObjectTypeXMLSchemaCollections()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "XMLSchemaCollections");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.XMLSchemaCollections));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_DDLTriggers_HasObjectTypeDDLTriggers()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "DDLTriggers");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.DDLTriggers));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_Sequences_HasObjectTypeSequences()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "Sequences");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Sequences));
        }

        [Test]
        public void SqlServer_GetDefaultTemplateFolders_Synonyms_HasObjectTypeSynonyms()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var folder = folders.Single(f => f.FolderPath == "Synonyms");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Synonyms));
        }

        // ── PostgreSQL ────────────────────────────────────────────────────────────

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_BeforeScripts_HasObjectTypeNone()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Before Scripts");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.None));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_AfterScripts_HasObjectTypeNone()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "After Scripts");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.None));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_TableData_HasObjectTypeNone()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Table Data");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.None));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_Views_HasObjectTypeViews()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Views");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Views));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_Functions_HasObjectTypeFunctions()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Functions");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Functions));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_Procedures_HasObjectTypeProcedures()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Procedures");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Procedures));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_Triggers_HasObjectTypeTriggers()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Triggers");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Triggers));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_Schemas_HasObjectTypeSchemas()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Schemas");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Schemas));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_DomainTypes_HasObjectTypeDomainTypes()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Domain Types");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.DomainTypes));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_EnumTypes_HasObjectTypeEnumTypes()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Enum Types");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.EnumTypes));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_CompositeTypes_HasObjectTypeCompositeTypes()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Composite Types");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.CompositeTypes));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_TriggerFunctions_HasObjectTypeTriggerFunctions()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Trigger Functions");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.TriggerFunctions));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_WindowFunctions_HasObjectTypeWindowFunctions()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Window Functions");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.WindowFunctions));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_Aggregates_HasObjectTypeAggregates()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Aggregates");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Aggregates));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_Sequences_HasObjectTypeSequences()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Sequences");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Sequences));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_Rules_HasObjectTypeRules()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Rules");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Rules));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_Collations_HasObjectTypeCollations()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Collations");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Collations));
        }

        [Test]
        public void PostgreSQL_GetDefaultTemplateFolders_Publications_HasObjectTypePublications()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL);
            var folder = folders.Single(f => f.FolderPath == "Publications");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Publications));
        }

        [Test]
        public void PostgreSQL_SchemaTemplate_ExcludesPublications_DatabaseScoped()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL, isSchemaTemplate: true);
            Assert.That(folders.Any(f => f.ObjectType == ScriptObjectType.Publications), Is.False,
                "Publications are database-scoped (no schema qualifier on CREATE PUBLICATION) and must not fan out per schema.");
        }

        [Test]
        public void PostgreSQL_SchemaTemplate_IncludesCollations_SchemaScoped()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.PostgreSQL, isSchemaTemplate: true);
            Assert.That(folders.Any(f => f.ObjectType == ScriptObjectType.Collations), Is.True,
                "Collations are schema-scoped and should fan out per schema like Domain/Enum/Composite Types.");
        }

        // ── MySQL ─────────────────────────────────────────────────────────────────

        [Test]
        public void MySQL_GetDefaultTemplateFolders_BeforeScripts_HasObjectTypeNone()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            var folder = folders.Single(f => f.FolderPath == "Before Scripts");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.None));
        }

        [Test]
        public void MySQL_GetDefaultTemplateFolders_AfterScripts_HasObjectTypeNone()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            var folder = folders.Single(f => f.FolderPath == "After Scripts");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.None));
        }

        [Test]
        public void MySQL_GetDefaultTemplateFolders_TableData_HasObjectTypeNone()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            var folder = folders.Single(f => f.FolderPath == "Table Data");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.None));
        }

        [Test]
        public void MySQL_GetDefaultTemplateFolders_Views_HasObjectTypeViews()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            var folder = folders.Single(f => f.FolderPath == "Views");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Views));
        }

        [Test]
        public void MySQL_GetDefaultTemplateFolders_Functions_HasObjectTypeFunctions()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            var folder = folders.Single(f => f.FolderPath == "Functions");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Functions));
        }

        [Test]
        public void MySQL_GetDefaultTemplateFolders_Procedures_HasObjectTypeProcedures()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            var folder = folders.Single(f => f.FolderPath == "Procedures");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Procedures));
        }

        [Test]
        public void MySQL_GetDefaultTemplateFolders_Triggers_HasObjectTypeTriggers()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            var folder = folders.Single(f => f.FolderPath == "Triggers");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Triggers));
        }

        [Test]
        public void MySQL_GetDefaultTemplateFolders_Events_HasObjectTypeEvents()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            var folder = folders.Single(f => f.FolderPath == "Events");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Events));
        }

        [Test]
        public void MySQL_GetDefaultTemplateFolders_Events_HasQuenchSlotObjects()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            var folder = folders.Single(f => f.FolderPath == "Events");
            Assert.That(folder.QuenchSlot, Is.EqualTo(TemplateQuenchSlot.Objects));
        }

        [Test]
        public void MySQL_GetDefaultTemplateFolders_HasNoSequencesFolder()
        {
            // MySQL has no native SEQUENCE object at all — unlike every other cell in this document,
            // this isn't a version gap, so the folder must be absent rather than merely unwired.
            var folders = Template.GetDefaultTemplateFolders(Platform.MySQL);
            Assert.That(folders.Any(f => f.FolderPath == "Sequences"), Is.False);
        }

        [Test]
        public void MariaDb_GetDefaultTemplateFolders_Sequences_HasObjectTypeSequences()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MariaDb);
            var folder = folders.Single(f => f.FolderPath == "Sequences");
            Assert.That(folder.ObjectType, Is.EqualTo(ScriptObjectType.Sequences));
        }

        [Test]
        public void MariaDb_GetDefaultTemplateFolders_Sequences_HasQuenchSlotObjects()
        {
            var folders = Template.GetDefaultTemplateFolders(Platform.MariaDb);
            var folder = folders.Single(f => f.FolderPath == "Sequences");
            Assert.That(folder.QuenchSlot, Is.EqualTo(TemplateQuenchSlot.Objects));
        }

        // ── Parity: GetDefaultTemplateFolders vs AddDefaultScriptFolders ──────────

        [Test]
        public void SqlServer_AddDefaultScriptFolders_MatchesGetDefaultTemplateFolders()
        {
            var template = RepositoryHelper.CreateDefaultTemplate("Test", Platform.SqlServer);
            var expected = Template.GetDefaultTemplateFolders(Platform.SqlServer)
                .Where(f => !RepositoryHelper.FoldersWrittenOnUse.Contains(f.ObjectType)).ToList();

            AssertFolderListsMatch(expected, template.ScriptFolders);
        }

        // 2.7.x refuses a Template.json naming an ObjectType it does not know, so a package with no security policy
        // must not declare their folder: written, it made every new SQL Server package unloadable there.
        [Test]
        public void SqlServer_AWrittenDefaultTemplate_DeclaresNoSecurityPolicyFolder()
        {
            var template = RepositoryHelper.CreateDefaultTemplate("Test", Platform.SqlServer);

            Assert.Multiple(() =>
            {
                Assert.That(template.ScriptFolders.Select(f => f.ObjectType), Has.None.EqualTo(ScriptObjectType.SecurityPolicies));
                Assert.That(Template.GetDefaultTemplateFolders(Platform.SqlServer).Select(f => f.ObjectType), Has.Some.EqualTo(ScriptObjectType.SecurityPolicies),
                    "the deploy's own fallback still knows the folder");
            });
        }

        [Test]
        public void PostgreSQL_AddDefaultScriptFolders_MatchesGetDefaultTemplateFolders()
        {
            var template = RepositoryHelper.CreateDefaultTemplate("Test", Platform.PostgreSQL);
            var expected = Template.GetDefaultTemplateFolders(Platform.PostgreSQL)
                .Where(f => !RepositoryHelper.FoldersWrittenOnUse.Contains(f.ObjectType)).ToList();

            AssertFolderListsMatch(expected, template.ScriptFolders);
        }

        // The five PostgreSQL kinds added in 2.8 are newer than releases still deploying packages, which refuse a
        // Template.json naming a type they do not know, so a package that uses none of them declares none of them.
        [TestCase("Range Types", ScriptObjectType.RangeTypes, TemplateQuenchSlot.Objects)]
        [TestCase("Text Search Configurations", ScriptObjectType.TextSearchConfigurations, TemplateQuenchSlot.Objects)]
        [TestCase("Operators", ScriptObjectType.Operators, TemplateQuenchSlot.Objects)]
        [TestCase("Casts", ScriptObjectType.Casts, TemplateQuenchSlot.Objects)]
        [TestCase("Event Triggers", ScriptObjectType.EventTriggers, TemplateQuenchSlot.AfterTablesObjects)]
        public void PostgreSQL_A2Point8Kind_HasItsFolder_ButAWrittenDefaultTemplateDeclaresItOnlyOnUse(string folderPath, ScriptObjectType type, TemplateQuenchSlot slot)
        {
            var folder = Template.GetDefaultTemplateFolders(Platform.PostgreSQL).Single(f => f.ObjectType == type);
            var written = RepositoryHelper.CreateDefaultTemplate("Test", Platform.PostgreSQL);
            Assert.Multiple(() =>
            {
                Assert.That(folder.FolderPath, Is.EqualTo(folderPath));
                Assert.That(folder.QuenchSlot, Is.EqualTo(slot));
                Assert.That(written.ScriptFolders.Select(f => f.ObjectType), Has.None.EqualTo(type));
                Assert.That(RepositoryHelper.FoldersWrittenOnUse, Does.Contain(type));
            });
        }

        // Event triggers belong to the last folder of their slot, so they do not fire on the package's own views.
        [Test]
        public void PostgreSQL_EventTriggers_DeployAfterEveryOtherObjectFolderOfTheirSlot()
        {
            var afterTables = Template.GetDefaultTemplateFolders(Platform.PostgreSQL).Where(f => f.QuenchSlot == TemplateQuenchSlot.AfterTablesObjects).ToList();
            Assert.That(afterTables[^1].ObjectType, Is.EqualTo(ScriptObjectType.EventTriggers), string.Join(", ", afterTables.Select(f => f.FolderPath)));
        }

        [Test]
        public void PostgreSQL_SchemaTemplate_ExcludesCastsAndEventTriggers_KeepsTheSchemaScopedKinds()
        {
            var types = Template.GetDefaultTemplateFolders(Platform.PostgreSQL, isSchemaTemplate: true).Select(f => f.ObjectType).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(types, Has.None.EqualTo(ScriptObjectType.Casts), "a cast has no schema");
                Assert.That(types, Has.None.EqualTo(ScriptObjectType.EventTriggers), "an event trigger has no schema");
                Assert.That(types, Has.Some.EqualTo(ScriptObjectType.RangeTypes));
                Assert.That(types, Has.Some.EqualTo(ScriptObjectType.TextSearchConfigurations));
                Assert.That(types, Has.Some.EqualTo(ScriptObjectType.Operators));
            });
        }

        [Test]
        public void MySQL_AddDefaultScriptFolders_MatchesGetDefaultTemplateFolders()
        {
            var template = RepositoryHelper.CreateDefaultTemplate("Test", Platform.MySQL);
            var expected = Template.GetDefaultTemplateFolders(Platform.MySQL);

            AssertFolderListsMatch(expected, template.ScriptFolders);
        }

        [Test]
        public void MariaDb_AddDefaultScriptFolders_MatchesGetDefaultTemplateFolders()
        {
            var template = RepositoryHelper.CreateDefaultTemplate("Test", Platform.MariaDb);
            var expected = Template.GetDefaultTemplateFolders(Platform.MariaDb);

            AssertFolderListsMatch(expected, template.ScriptFolders);
        }

        private static void AssertFolderListsMatch(
            List<TemplateFolder> expected,
            List<TemplateFolder> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count),
                $"Folder count mismatch.\nExpected: {string.Join(", ", expected.Select(f => f.FolderPath))}\nActual: {string.Join(", ", actual.Select(f => f.FolderPath))}");

            for (var i = 0; i < expected.Count; i++)
            {
                Assert.That(actual[i].FolderPath, Is.EqualTo(expected[i].FolderPath),
                    $"Folder[{i}] FolderPath mismatch");
                Assert.That(actual[i].QuenchSlot, Is.EqualTo(expected[i].QuenchSlot),
                    $"Folder[{i}] ({expected[i].FolderPath}) QuenchSlot mismatch");
                Assert.That(actual[i].ObjectType, Is.EqualTo(expected[i].ObjectType),
                    $"Folder[{i}] ({expected[i].FolderPath}) ObjectType mismatch");
            }
        }
    }
}
