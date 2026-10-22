// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Newtonsoft.Json;
using Schema.Domain;
using Schema.Domain.PostgreSQL;

namespace Schema.UnitTests.Domain.PostgreSQL
{
    /// <summary>
    /// Dedicated defaulting coverage for <see cref="PostgreSqlMaterializedView"/>. Mirrors
    /// <see cref="PostgreSqlTableDefaultingTests"/>. The behavior was previously covered indirectly
    /// via <c>SchemaDefaultResolverTests</c>; this file co-locates the materialized-view-specific
    /// cases with the rest of the PostgreSQL domain tests so a regression to materialized-view
    /// defaulting surfaces in the same place a regression to table defaulting would.
    /// </summary>
    [TestFixture]
    public class PostgreSqlMaterializedViewDefaultingTests
    {
        [Test]
        public void BareConstructor_Schema_IsNull()
        {
            var view = new PostgreSqlMaterializedView();
            Assert.That(view.Schema, Is.Null);
        }

        [Test]
        public void RegularTemplate_OmittedSchemaInJson_ResolvesToPublic()
        {
            var json = """{"Name":"mv_order_summary","Definition":"SELECT 1","Indexes":[]}""";
            var view = JsonConvert.DeserializeObject<PostgreSqlMaterializedView>(json);

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: false, Platform.PostgreSQL);

            Assert.That(view.Schema, Is.EqualTo("public"));
        }

        [Test]
        public void RegularTemplate_EmptySchema_ResolvesToPublic()
        {
            var view = new PostgreSqlMaterializedView { Name = "mv_order_summary", Schema = "" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: false, Platform.PostgreSQL);

            Assert.That(view.Schema, Is.EqualTo("public"));
        }

        [Test]
        public void RegularTemplate_ExplicitLiteralSchema_Preserved()
        {
            var view = new PostgreSqlMaterializedView { Name = "mv_order_summary", Schema = "sales" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: false, Platform.PostgreSQL);

            Assert.That(view.Schema, Is.EqualTo("sales"));
        }

        [Test]
        public void SchemaTemplate_OmittedSchemaInJson_ResolvesToSchemaNameToken()
        {
            var json = """{"Name":"mv_order_summary","Definition":"SELECT 1","Indexes":[]}""";
            var view = JsonConvert.DeserializeObject<PostgreSqlMaterializedView>(json);

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.PostgreSQL);

            Assert.That(view.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_EmptySchema_ResolvesToSchemaNameToken()
        {
            var view = new PostgreSqlMaterializedView { Name = "mv_order_summary", Schema = "" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.PostgreSQL);

            Assert.That(view.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_LiteralSchemaNameTokenAccepted()
        {
            var view = new PostgreSqlMaterializedView { Name = "mv_order_summary", Schema = "{{SchemaName}}" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.PostgreSQL);

            Assert.That(view.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_HardLiteralSchema_Rejected_WithMaterializedViewContext()
        {
            var view = new PostgreSqlMaterializedView { Name = "mv_order_summary", Schema = "public" };

            var ex = Assert.Throws<PackageException>(() =>
                SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.PostgreSQL));

            Assert.That(ex.Message, Does.Contain("mv_order_summary"));
            Assert.That(ex.Message, Does.Contain("schema template").IgnoreCase);
            Assert.That(ex.Message, Does.Contain("{{SchemaName}}"));
            // The owner-kind label distinguishes materialized views from tables / indexed views in
            // the error message so a user staring at the wrong JSON file knows which kind to look for.
            Assert.That(ex.Message, Does.Contain("materialized view"));
        }

        [Test]
        public void Resolve_NullView_DoesNotThrow()
        {
            // Defensive: materialized-view lists in template construction may briefly carry nulls.
            Assert.DoesNotThrow(() =>
                SchemaDefaultResolver.Resolve((PostgreSqlMaterializedView)null, isSchemaTemplate: false, Platform.PostgreSQL));
            Assert.DoesNotThrow(() =>
                SchemaDefaultResolver.Resolve((PostgreSqlMaterializedView)null, isSchemaTemplate: true, Platform.PostgreSQL));
        }
    }
}
