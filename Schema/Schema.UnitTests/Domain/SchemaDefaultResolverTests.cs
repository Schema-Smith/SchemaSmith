// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;

namespace Schema.UnitTests.Domain
{
    [TestFixture]
    public class SchemaDefaultResolverTests
    {
        [Test]
        public void Resolve_NullArgument_DoesNotThrow()
        {
            // Defensive: resolver tolerates a null table/fk passed in (lists in domain may have nulls during construction).
            Assert.DoesNotThrow(() =>
                SchemaDefaultResolver.Resolve((SqlServerTable)null, isSchemaTemplate: false, Platform.SqlServer));
        }

        [Test]
        public void Resolve_SqlServerIndexedView_UnsetSchema_ResolvesToDbo_InRegularTemplate()
        {
            var view = new SqlServerIndexedView { Name = "vw_Test" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: false, Platform.SqlServer);

            Assert.That(view.Schema, Is.EqualTo("dbo"));
        }

        [Test]
        public void Resolve_SqlServerIndexedView_UnsetSchema_ResolvesToSchemaNameToken_InSchemaTemplate()
        {
            var view = new SqlServerIndexedView { Name = "vw_Test" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.SqlServer);

            Assert.That(view.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void Resolve_SqlServerIndexedView_HardLiteralInSchemaTemplate_Rejected()
        {
            var view = new SqlServerIndexedView { Name = "vw_Test", Schema = "dbo" };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.SqlServer));

            Assert.That(ex.Message, Does.Contain("vw_Test"));
        }

        [Test]
        public void Resolve_PostgreSqlMaterializedView_UnsetSchema_ResolvesToPublic_InRegularTemplate()
        {
            var view = new PostgreSqlMaterializedView { Name = "mv_test" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: false, Platform.PostgreSQL);

            Assert.That(view.Schema, Is.EqualTo("public"));
        }

        [Test]
        public void Resolve_PostgreSqlMaterializedView_UnsetSchema_ResolvesToSchemaNameToken_InSchemaTemplate()
        {
            var view = new PostgreSqlMaterializedView { Name = "mv_test" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.PostgreSQL);

            Assert.That(view.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void Resolve_PostgreSqlMaterializedView_HardLiteralInSchemaTemplate_Rejected()
        {
            var view = new PostgreSqlMaterializedView { Name = "mv_test", Schema = "public" };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.PostgreSQL));

            Assert.That(ex.Message, Does.Contain("mv_test"));
        }

        [Test]
        public void Resolve_Template_RegularTemplate_AppliesPlatformDefaultsToAllChildren()
        {
            var template = new Template { Name = "Regular" };
            var table = new SqlServerTable { Name = "Customers" };
            table.ForeignKeys.Add(new SqlServerForeignKey { Name = "FK_Customers_Region", RelatedTable = "Regions" });
            template.Tables.Add(table);
            template.IndexedViews.Add(new SqlServerIndexedView { Name = "vw_Active" });
            template.Product = new Product { Platform = Platform.SqlServer };

            SchemaDefaultResolver.Resolve(template);

            Assert.That(table.Schema, Is.EqualTo("dbo"));
            Assert.That(((SqlServerForeignKey)table.ForeignKeys[0]).RelatedTableSchema, Is.EqualTo("dbo"));
            Assert.That(template.IndexedViews[0].Schema, Is.EqualTo("dbo"));
        }

        [Test]
        public void Resolve_Template_SchemaTemplate_AppliesSchemaNameTokenDefaults()
        {
            var template = new Template
            {
                Name = "TenantBody",
                SchemaIdentificationScript = "SELECT 'tenant_a' AS SchemaName"
            };
            var table = new SqlServerTable { Name = "Customers" };
            table.ForeignKeys.Add(new SqlServerForeignKey { Name = "FK_Customers_Region", RelatedTable = "Regions" });
            // Cross-schema FK kept explicit (preserved by resolver)
            table.ForeignKeys.Add(new SqlServerForeignKey
            {
                Name = "FK_Customers_Country", RelatedTable = "Countries", RelatedTableSchema = "dbo"
            });
            template.Tables.Add(table);
            template.IndexedViews.Add(new SqlServerIndexedView { Name = "vw_Active" });
            template.Product = new Product { Platform = Platform.SqlServer };

            SchemaDefaultResolver.Resolve(template);

            Assert.That(table.Schema, Is.EqualTo("{{SchemaName}}"));
            Assert.That(((SqlServerForeignKey)table.ForeignKeys[0]).RelatedTableSchema, Is.EqualTo("{{SchemaName}}"));
            Assert.That(((SqlServerForeignKey)table.ForeignKeys[1]).RelatedTableSchema, Is.EqualTo("dbo"),
                "Cross-schema FK with explicit literal must be preserved.");
            Assert.That(template.IndexedViews[0].Schema, Is.EqualTo("{{SchemaName}}"));
        }

        // The three declarative PostgreSQL types (2.6.0) resolve exactly like a materialized view. Before
        // this they were never visited at all: EnumTypeQuench/DomainTypeQuench/SequenceQuench each default a
        // null Schema with COALESCE(..., 'public'), so under a SCHEMA TEMPLATE an object authored without an
        // explicit Schema was created in public on every tenant rather than in the tenant's own schema --
        // silently, at exit 0.
        [Test]
        public void Resolve_PostgreSqlEnumType_UnsetSchema_ResolvesToPublic_InRegularTemplate()
        {
            var obj = new PostgreSqlEnumType { Name = "enumtype_test" };

            SchemaDefaultResolver.Resolve(obj, isSchemaTemplate: false, Platform.PostgreSQL);

            Assert.That(obj.Schema, Is.EqualTo("public"));
        }

        [Test]
        public void Resolve_PostgreSqlEnumType_UnsetSchema_ResolvesToSchemaNameToken_InSchemaTemplate()
        {
            var obj = new PostgreSqlEnumType { Name = "enumtype_test" };

            SchemaDefaultResolver.Resolve(obj, isSchemaTemplate: true, Platform.PostgreSQL);

            Assert.That(obj.Schema, Is.EqualTo("{{SchemaName}}"),
                "an unset Schema under a schema template must become the tenant token -- left null it "
                + "reaches the quench, which COALESCEs it to public on every tenant");
        }

        [Test]
        public void Resolve_PostgreSqlEnumType_HardLiteralInSchemaTemplate_Rejected()
        {
            var obj = new PostgreSqlEnumType { Name = "enumtype_test", Schema = "public" };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SchemaDefaultResolver.Resolve(obj, isSchemaTemplate: true, Platform.PostgreSQL));

            Assert.That(ex.Message, Does.Contain("enumtype_test"));
            Assert.That(ex.Message, Does.Contain("enum type"),
                "the message must name the KIND, or a package author with several object types cannot tell "
                + "which declaration to correct");
        }

        // The three declarative PostgreSQL types (2.6.0) resolve exactly like a materialized view. Before
        // this they were never visited at all: EnumTypeQuench/DomainTypeQuench/SequenceQuench each default a
        // null Schema with COALESCE(..., 'public'), so under a SCHEMA TEMPLATE an object authored without an
        // explicit Schema was created in public on every tenant rather than in the tenant's own schema --
        // silently, at exit 0.
        [Test]
        public void Resolve_PostgreSqlDomainType_UnsetSchema_ResolvesToPublic_InRegularTemplate()
        {
            var obj = new PostgreSqlDomainType { Name = "domaintype_test" };

            SchemaDefaultResolver.Resolve(obj, isSchemaTemplate: false, Platform.PostgreSQL);

            Assert.That(obj.Schema, Is.EqualTo("public"));
        }

        [Test]
        public void Resolve_PostgreSqlDomainType_UnsetSchema_ResolvesToSchemaNameToken_InSchemaTemplate()
        {
            var obj = new PostgreSqlDomainType { Name = "domaintype_test" };

            SchemaDefaultResolver.Resolve(obj, isSchemaTemplate: true, Platform.PostgreSQL);

            Assert.That(obj.Schema, Is.EqualTo("{{SchemaName}}"),
                "an unset Schema under a schema template must become the tenant token -- left null it "
                + "reaches the quench, which COALESCEs it to public on every tenant");
        }

        [Test]
        public void Resolve_PostgreSqlDomainType_HardLiteralInSchemaTemplate_Rejected()
        {
            var obj = new PostgreSqlDomainType { Name = "domaintype_test", Schema = "public" };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SchemaDefaultResolver.Resolve(obj, isSchemaTemplate: true, Platform.PostgreSQL));

            Assert.That(ex.Message, Does.Contain("domaintype_test"));
            Assert.That(ex.Message, Does.Contain("domain type"),
                "the message must name the KIND, or a package author with several object types cannot tell "
                + "which declaration to correct");
        }

        // The three declarative PostgreSQL types (2.6.0) resolve exactly like a materialized view. Before
        // this they were never visited at all: EnumTypeQuench/DomainTypeQuench/SequenceQuench each default a
        // null Schema with COALESCE(..., 'public'), so under a SCHEMA TEMPLATE an object authored without an
        // explicit Schema was created in public on every tenant rather than in the tenant's own schema --
        // silently, at exit 0.
        [Test]
        public void Resolve_PostgreSqlSequence_UnsetSchema_ResolvesToPublic_InRegularTemplate()
        {
            var obj = new PostgreSqlSequence { Name = "sequence_test" };

            SchemaDefaultResolver.Resolve(obj, isSchemaTemplate: false, Platform.PostgreSQL);

            Assert.That(obj.Schema, Is.EqualTo("public"));
        }

        [Test]
        public void Resolve_PostgreSqlSequence_UnsetSchema_ResolvesToSchemaNameToken_InSchemaTemplate()
        {
            var obj = new PostgreSqlSequence { Name = "sequence_test" };

            SchemaDefaultResolver.Resolve(obj, isSchemaTemplate: true, Platform.PostgreSQL);

            Assert.That(obj.Schema, Is.EqualTo("{{SchemaName}}"),
                "an unset Schema under a schema template must become the tenant token -- left null it "
                + "reaches the quench, which COALESCEs it to public on every tenant");
        }

        [Test]
        public void Resolve_PostgreSqlSequence_HardLiteralInSchemaTemplate_Rejected()
        {
            var obj = new PostgreSqlSequence { Name = "sequence_test", Schema = "public" };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SchemaDefaultResolver.Resolve(obj, isSchemaTemplate: true, Platform.PostgreSQL));

            Assert.That(ex.Message, Does.Contain("sequence_test"));
            Assert.That(ex.Message, Does.Contain("sequence"),
                "the message must name the KIND, or a package author with several object types cannot tell "
                + "which declaration to correct");
        }
        [Test]
        public void Resolve_Template_PostgreSql_SchemaTemplate_AppliesSchemaNameTokenDefaults()
        {
            var template = new Template
            {
                Name = "tenant_body",
                SchemaIdentificationScript = "SELECT 'tenant_a' AS schema_name"
            };
            var table = new PostgreSqlTable { Name = "customers" };
            table.ForeignKeys.Add(new PostgreSqlForeignKey { Name = "fk_customers_region", RelatedTable = "regions" });
            table.ForeignKeys.Add(new PostgreSqlForeignKey
            {
                Name = "fk_customers_country", RelatedTable = "countries", RelatedTableSchema = "public"
            });
            template.Tables.Add(table);
            template.MaterializedViews.Add(new PostgreSqlMaterializedView { Name = "mv_active" });
            template.EnumTypes.Add(new PostgreSqlEnumType { Name = "order_status" });
            template.DomainTypes.Add(new PostgreSqlDomainType { Name = "email_address" });
            template.Sequences.Add(new PostgreSqlSequence { Name = "order_seq" });
            template.Product = new Product { Platform = Platform.PostgreSQL };

            SchemaDefaultResolver.Resolve(template);

            Assert.That(table.Schema, Is.EqualTo("{{SchemaName}}"));
            Assert.That(((PostgreSqlForeignKey)table.ForeignKeys[0]).RelatedTableSchema, Is.EqualTo("{{SchemaName}}"));
            Assert.That(((PostgreSqlForeignKey)table.ForeignKeys[1]).RelatedTableSchema, Is.EqualTo("public"),
                "Cross-schema FK with explicit literal must be preserved.");
            Assert.That(template.MaterializedViews[0].Schema, Is.EqualTo("{{SchemaName}}"));
            // The three declarative types were never walked, so they stayed null and the quench put
            // them in public on every tenant. This is the assertion the defect fails.
            Assert.Multiple(() =>
            {
                Assert.That(template.EnumTypes[0].Schema, Is.EqualTo("{{SchemaName}}"),
                    "an enum type in a schema template belongs to the tenant, not to public");
                Assert.That(template.DomainTypes[0].Schema, Is.EqualTo("{{SchemaName}}"),
                    "a domain type in a schema template belongs to the tenant, not to public");
                Assert.That(template.Sequences[0].Schema, Is.EqualTo("{{SchemaName}}"),
                    "a sequence in a schema template belongs to the tenant -- a shared sequence would "
                    + "hand every tenant numbers from the same counter");
            });
        }

        [Test]
        public void Resolve_Template_NullTemplate_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => SchemaDefaultResolver.Resolve(null));
        }

        [Test]
        public void Resolve_Template_NoProduct_ThrowsWithProgrammerHint()
        {
            var template = new Template { Name = "Standalone" };
            var ex = Assert.Throws<InvalidOperationException>(() => SchemaDefaultResolver.Resolve(template));
            Assert.That(ex.Message, Does.Contain("Standalone"));
            Assert.That(ex.Message, Does.Contain("Product"));
        }

        [Test]
        public void Resolve_Template_HardLiteralSchemaInside_RewrapsWithTemplateContext()
        {
            var template = new Template
            {
                Name = "TenantBody",
                FilePath = @"C:\app\Templates\TenantBody\Template.json",
                SchemaIdentificationScript = "SELECT 'tenant_a' AS SchemaName",
                Product = new Product { Platform = Platform.SqlServer }
            };
            template.Tables.Add(new SqlServerTable { Name = "Customers", Schema = "dbo" });

            var ex = Assert.Throws<InvalidOperationException>(() => SchemaDefaultResolver.Resolve(template));

            // Outer wrap surfaces template name + file path so the user can find the bad JSON.
            Assert.That(ex.Message, Does.Contain("TenantBody"));
            Assert.That(ex.Message, Does.Contain("Template.json"));
            // Inner exception's detail (table name) is preserved either in the message or InnerException.
            Assert.That(ex.Message, Does.Contain("Customers"));
            Assert.That(ex.InnerException, Is.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public void Resolve_Template_PlatformUnknown_ThrowsWithFilePathHint()
        {
            var template = new Template
            {
                Name = "Bad",
                FilePath = @"C:\some\Templates\Bad\Template.json",
                Product = new Product { Platform = Platform.Unknown }
            };
            var ex = Assert.Throws<InvalidOperationException>(() => SchemaDefaultResolver.Resolve(template));
            Assert.That(ex.Message, Does.Contain("Bad"));
            Assert.That(ex.Message, Does.Contain("Unknown"));
            Assert.That(ex.Message, Does.Contain("Template.json"));
        }

        [Test]
        public void Resolve_Template_DerivesSchemaTemplateFlag_FromSchemaIdentificationScriptPresence()
        {
            var regular = new Template { Name = "Regular" };
            Assert.That(regular.IsSchemaTemplate, Is.False);

            var schemaTemplate = new Template { Name = "Tenant", SchemaIdentificationScript = "SELECT 'x'" };
            Assert.That(schemaTemplate.IsSchemaTemplate, Is.True);

            var emptyScript = new Template { Name = "Empty", SchemaIdentificationScript = "   " };
            Assert.That(emptyScript.IsSchemaTemplate, Is.False, "Whitespace-only script should not flip the flag.");
        }
    }
}
