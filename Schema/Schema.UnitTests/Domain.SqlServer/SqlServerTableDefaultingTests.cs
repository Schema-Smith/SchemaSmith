// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Newtonsoft.Json;
using Schema.Domain;
using Schema.Domain.SqlServer;

namespace Schema.UnitTests.Domain.SqlServer
{
    [TestFixture]
    public class SqlServerTableDefaultingTests
    {
        [Test]
        public void BareConstructor_Schema_IsNull()
        {
            var table = new SqlServerTable();
            Assert.That(table.Schema, Is.Null);
        }

        [Test]
        public void RegularTemplate_OmittedSchemaInJson_ResolvesToDbo()
        {
            var json = """{"Name":"Customers","Columns":[]}""";
            var table = JsonConvert.DeserializeObject<SqlServerTable>(json);

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: false, Platform.SqlServer);

            Assert.That(table.Schema, Is.EqualTo("dbo"));
        }

        [Test]
        public void RegularTemplate_EmptySchema_ResolvesToDbo()
        {
            var table = new SqlServerTable { Name = "Customers", Schema = "" };

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: false, Platform.SqlServer);

            Assert.That(table.Schema, Is.EqualTo("dbo"));
        }

        [Test]
        public void RegularTemplate_ExplicitLiteralSchema_Preserved()
        {
            var table = new SqlServerTable { Name = "Customers", Schema = "Sales" };

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: false, Platform.SqlServer);

            Assert.That(table.Schema, Is.EqualTo("Sales"));
        }

        [Test]
        public void SchemaTemplate_OmittedSchemaInJson_ResolvesToSchemaNameToken()
        {
            var json = """{"Name":"Customers","Columns":[]}""";
            var table = JsonConvert.DeserializeObject<SqlServerTable>(json);

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: true, Platform.SqlServer);

            Assert.That(table.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_EmptySchema_ResolvesToSchemaNameToken()
        {
            var table = new SqlServerTable { Name = "Customers", Schema = "" };

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: true, Platform.SqlServer);

            Assert.That(table.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_LiteralSchemaNameTokenAccepted()
        {
            var table = new SqlServerTable { Name = "Customers", Schema = "{{SchemaName}}" };

            SchemaDefaultResolver.Resolve(table, isSchemaTemplate: true, Platform.SqlServer);

            Assert.That(table.Schema, Is.EqualTo("{{SchemaName}}"));
        }

        [Test]
        public void SchemaTemplate_HardLiteralSchema_Rejected_WithFileContext()
        {
            var table = new SqlServerTable { Name = "Customers", Schema = "dbo" };

            var ex = Assert.Throws<PackageException>(() =>
                SchemaDefaultResolver.Resolve(table, isSchemaTemplate: true, Platform.SqlServer));

            Assert.That(ex.Message, Does.Contain("Customers"));
            Assert.That(ex.Message, Does.Contain("schema template").IgnoreCase);
            Assert.That(ex.Message, Does.Contain("{{SchemaName}}"));
        }
    }
}
