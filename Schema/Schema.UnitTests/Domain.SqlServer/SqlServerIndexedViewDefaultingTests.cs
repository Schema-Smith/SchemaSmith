// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Newtonsoft.Json;
using Schema.Domain;
using Schema.Domain.SqlServer;

namespace Schema.UnitTests.Domain.SqlServer
{
    /// <summary>
    /// Dedicated defaulting coverage for <see cref="SqlServerIndexedView"/>. Mirrors
    /// <see cref="SqlServerTableDefaultingTests"/>. The behavior was previously covered indirectly
    /// via <c>SchemaDefaultResolverTests</c>; this file co-locates the indexed-view-specific cases
    /// with the rest of the SQL Server domain tests so a regression to indexed-view defaulting
    /// surfaces in the same place a regression to table defaulting would.
    /// </summary>
    [TestFixture]
    public class SqlServerIndexedViewDefaultingTests
    {
        [Test]
        public void BareConstructor_Schema_IsNull()
        {
            var view = new SqlServerIndexedView();
            Assert.That(view.Schema, Is.Null);
        }

        [Test]
        public void RegularTemplate_OmittedSchemaInJson_ResolvesToDbo()
        {
            var json = """{"Name":"vw_OrderSummary","Definition":"SELECT 1","Indexes":[]}""";
            var view = JsonConvert.DeserializeObject<SqlServerIndexedView>(json);

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: false, Platform.SqlServer);

            Assert.That(view.Schema, Is.EqualTo("dbo"));
        }

        [Test]
        public void RegularTemplate_EmptySchema_ResolvesToDbo()
        {
            var view = new SqlServerIndexedView { Name = "vw_OrderSummary", Schema = "" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: false, Platform.SqlServer);

            Assert.That(view.Schema, Is.EqualTo("dbo"));
        }

        [Test]
        public void RegularTemplate_ExplicitLiteralSchema_Preserved()
        {
            var view = new SqlServerIndexedView { Name = "vw_OrderSummary", Schema = "Sales" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: false, Platform.SqlServer);

            Assert.That(view.Schema, Is.EqualTo("Sales"));
        }

        [Test]
        public void SchemaTemplate_OmittedSchemaInJson_ResolvesToSchemaNameToken()
        {
            var json = """{"Name":"vw_OrderSummary","Definition":"SELECT 1","Indexes":[]}""";
            var view = JsonConvert.DeserializeObject<SqlServerIndexedView>(json);

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.SqlServer);

            Assert.That(view.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_EmptySchema_ResolvesToSchemaNameToken()
        {
            var view = new SqlServerIndexedView { Name = "vw_OrderSummary", Schema = "" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.SqlServer);

            Assert.That(view.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_LiteralSchemaNameTokenAccepted()
        {
            var view = new SqlServerIndexedView { Name = "vw_OrderSummary", Schema = "{{SchemaName}}" };

            SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.SqlServer);

            Assert.That(view.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_HardLiteralSchema_Rejected_WithIndexedViewContext()
        {
            var view = new SqlServerIndexedView { Name = "vw_OrderSummary", Schema = "dbo" };

            var ex = Assert.Throws<PackageException>(() =>
                SchemaDefaultResolver.Resolve(view, isSchemaTemplate: true, Platform.SqlServer));

            Assert.That(ex.Message, Does.Contain("vw_OrderSummary"));
            Assert.That(ex.Message, Does.Contain("schema template").IgnoreCase);
            Assert.That(ex.Message, Does.Contain("{{SchemaName}}"));
            // The owner-kind label distinguishes indexed views from tables / materialized views in
            // the error message so a user staring at the wrong JSON file knows which kind to look for.
            Assert.That(ex.Message, Does.Contain("indexed view"));
        }

        [Test]
        public void Resolve_NullView_DoesNotThrow()
        {
            // Defensive: indexed-view lists in template construction may briefly carry nulls.
            Assert.DoesNotThrow(() =>
                SchemaDefaultResolver.Resolve((SqlServerIndexedView)null, isSchemaTemplate: false, Platform.SqlServer));
            Assert.DoesNotThrow(() =>
                SchemaDefaultResolver.Resolve((SqlServerIndexedView)null, isSchemaTemplate: true, Platform.SqlServer));
        }
    }
}
