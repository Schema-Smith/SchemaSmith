// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Newtonsoft.Json;
using Schema.Domain;
using Schema.Domain.PostgreSQL;

namespace Schema.UnitTests.Domain.PostgreSQL
{
    [TestFixture]
    public class PostgreSqlTableDefaultingTests
    {
        [Test]
        public void BareConstructor_Schema_IsNull()
        {
            var table = new PostgreSqlTable();
            Assert.That(table.Schema, Is.Null);
        }

        [Test]
        public void RegularTemplate_OmittedSchemaInJson_ResolvesToPublic()
        {
            var json = """{"Name":"customers","Columns":[]}""";
            var table = JsonConvert.DeserializeObject<PostgreSqlTable>(json);

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: false, Platform.PostgreSQL);

            Assert.That(table.Schema, Is.EqualTo("public"));
        }

        [Test]
        public void RegularTemplate_EmptySchema_ResolvesToPublic()
        {
            var table = new PostgreSqlTable { Name = "customers", Schema = "" };

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: false, Platform.PostgreSQL);

            Assert.That(table.Schema, Is.EqualTo("public"));
        }

        [Test]
        public void RegularTemplate_ExplicitLiteralSchema_Preserved()
        {
            var table = new PostgreSqlTable { Name = "customers", Schema = "sales" };

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: false, Platform.PostgreSQL);

            Assert.That(table.Schema, Is.EqualTo("sales"));
        }

        [Test]
        public void SchemaTemplate_OmittedSchemaInJson_ResolvesToSchemaNameToken()
        {
            var json = """{"Name":"customers","Columns":[]}""";
            var table = JsonConvert.DeserializeObject<PostgreSqlTable>(json);

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: true, Platform.PostgreSQL);

            Assert.That(table.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_EmptySchema_ResolvesToSchemaNameToken()
        {
            var table = new PostgreSqlTable { Name = "customers", Schema = "" };

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: true, Platform.PostgreSQL);

            Assert.That(table.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_LiteralSchemaNameTokenAccepted()
        {
            var table = new PostgreSqlTable { Name = "customers", Schema = "{{SchemaName}}" };

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: true, Platform.PostgreSQL);

            Assert.That(table.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_HardLiteralSchema_Rejected_WithFileContext()
        {
            var table = new PostgreSqlTable { Name = "customers", Schema = "public" };

            var ex = Assert.Throws<PackageException>(() =>
                SchemaDefaultResolver.Resolve(table, isSchemaTemplate: true, Platform.PostgreSQL));

            Assert.That(ex.Message, Does.Contain("customers"));
            Assert.That(ex.Message, Does.Contain("schema template").IgnoreCase);
            Assert.That(ex.Message, Does.Contain("{{SchemaName}}"));
        }
    }
}
