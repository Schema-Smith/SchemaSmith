// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
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
    public class TableLoadTests
    {
        private IFile _mockFile;

        [SetUp]
        public void SetUp()
        {
            FactoryContainer.Clear();
            _mockFile = Substitute.For<IFile>();
            FactoryContainer.Register<IFile>(_mockFile);
        }

        [TearDown]
        public void TearDown()
        {
            FactoryContainer.Clear();
        }

        [Test]
        public void Load_SqlServer_ReturnsSqlServerTable()
        {
            var tableJson = @"{
                ""Schema"": ""[dbo]"",
                ""Name"": ""[TestTable]"",
                ""CompressionType"": ""NONE"",
                ""Columns"": [],
                ""Indexes"": []
            }";
            var filePath = Path.Combine("C:", "tables", "dbo.TestTable.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.SqlServer);

            Assert.That(table, Is.InstanceOf<SqlServerTable>());
            Assert.That(table.Name, Is.EqualTo("[TestTable]"));
        }

        [Test]
        public void Load_PostgreSQL_ReturnsPostgreSqlTable()
        {
            var tableJson = @"{
                ""Schema"": ""public"",
                ""Name"": ""TestTable"",
                ""Columns"": [],
                ""Indexes"": []
            }";
            var filePath = Path.Combine("C:", "tables", "public.TestTable.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.PostgreSQL);

            Assert.That(table, Is.InstanceOf<PostgreSqlTable>());
            Assert.That(table.Name, Is.EqualTo("TestTable"));
        }

        [Test]
        public void Load_MySQL_ReturnsMySqlTable()
        {
            var tableJson = @"{
                ""Name"": ""`TestTable`"",
                ""Engine"": ""InnoDB"",
                ""Columns"": [],
                ""Indexes"": []
            }";
            var filePath = Path.Combine("C:", "tables", "TestTable.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.MySQL);

            Assert.That(table, Is.InstanceOf<MySqlTable>());
            Assert.That(table.Name, Is.EqualTo("`TestTable`"));
        }

        [Test]
        public void Load_SqlServer_PreservesColumns()
        {
            var tableJson = @"{
                ""Name"": ""[TestTable]"",
                ""Columns"": [
                    { ""Name"": ""[Id]"", ""DataType"": ""INT"" },
                    { ""Name"": ""[Name]"", ""DataType"": ""VARCHAR(100)"", ""Nullable"": true }
                ]
            }";
            var filePath = Path.Combine("C:", "tables", "test.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.SqlServer);

            Assert.That(table.Columns, Has.Count.EqualTo(2));
            Assert.That(table.Columns[0].Name, Is.EqualTo("[Id]"));
            Assert.That(table.Columns[1].Nullable, Is.True);
        }

        [Test]
        public void Load_PreservesIndexes()
        {
            var tableJson = @"{
                ""Name"": ""TestTable"",
                ""Columns"": [],
                ""Indexes"": [
                    { ""Name"": ""PK_Test"", ""PrimaryKey"": true, ""IndexColumns"": ""Id"" }
                ]
            }";
            var filePath = Path.Combine("C:", "tables", "test.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.PostgreSQL);

            Assert.That(table.Indexes, Has.Count.EqualTo(1));
            Assert.That(table.Indexes[0].PrimaryKey, Is.True);
        }

        [Test]
        public void Load_PreservesForeignKeys()
        {
            var tableJson = @"{
                ""Name"": ""TestTable"",
                ""Columns"": [],
                ""ForeignKeys"": [
                    { ""Name"": ""FK_Test"", ""Columns"": ""ParentID"", ""RelatedTable"": ""Parent"", ""RelatedColumns"": ""Id"" }
                ]
            }";
            var filePath = Path.Combine("C:", "tables", "test.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.SqlServer);

            Assert.That(table.ForeignKeys, Has.Count.EqualTo(1));
            Assert.That(table.ForeignKeys[0].Name, Is.EqualTo("FK_Test"));
        }

        [Test]
        public void Load_PreservesCheckConstraints()
        {
            var tableJson = @"{
                ""Name"": ""TestTable"",
                ""Columns"": [],
                ""CheckConstraints"": [
                    { ""Name"": ""CK_Test"", ""Expression"": ""Status < 20"" }
                ]
            }";
            var filePath = Path.Combine("C:", "tables", "test.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.MySQL);

            Assert.That(table.CheckConstraints, Has.Count.EqualTo(1));
            Assert.That(table.CheckConstraints[0].Expression, Is.EqualTo("Status < 20"));
        }

        [Test]
        public void Load_PreservesMergeProperties()
        {
            var tableJson = @"{
                ""Name"": ""TestTable"",
                ""Columns"": [],
                ""DataDelivery"": {
                    ""MergeType"": ""Insert/Update"",
                    ""MatchColumns"": ""Id"",
                    ""ContentFile"": ""Tables/test.tabledata""
                }
            }";
            var filePath = Path.Combine("C:", "tables", "test.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.SqlServer);

            Assert.That(table.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
            Assert.That(table.DataDelivery[0].MatchColumns, Is.EqualTo("Id"));
            Assert.That(table.DataDelivery[0].ContentFile, Is.EqualTo("Tables/test.tabledata"));
        }

        [Test]
        public void Load_ThrowsWhenFileNotFound()
        {
            var filePath = Path.Combine("C:", "tables", "nonexistent.json");
            _mockFile.Exists(filePath).Returns(false);

            var ex = Assert.Throws<RunFailedException>(() => Table.Load(filePath, Platform.SqlServer));

            Assert.That(ex.Message, Does.Contain("nonexistent.json"));
        }

        [Test]
        public void Load_ThrowsWithContextOnDeserializationError()
        {
            var filePath = Path.Combine("C:", "tables", "bad.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns("{ invalid json }");

            var ex = Assert.Throws<RunFailedException>(() => Table.Load(filePath, Platform.SqlServer));

            Assert.That(ex.Message, Does.Contain("bad.json"));
        }

        [Test]
        public void Load_SqlServer_PreservesPlatformSpecificProperties()
        {
            var tableJson = @"{
                ""Name"": ""[TestTable]"",
                ""CompressionType"": ""PAGE"",
                ""Columns"": []
            }";
            var filePath = Path.Combine("C:", "tables", "test.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.SqlServer) as SqlServerTable;

            Assert.That(table, Is.Not.Null);
            Assert.That(table.CompressionType, Is.EqualTo("PAGE"));
        }

        [Test]
        public void Load_PostgreSQL_PreservesPlatformSpecificProperties()
        {
            var tableJson = @"{
                ""Name"": ""TestTable"",
                ""Schema"": ""public"",
                ""PersistenceType"": ""Unlogged"",
                ""Columns"": []
            }";
            var filePath = Path.Combine("C:", "tables", "test.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.PostgreSQL) as PostgreSqlTable;

            Assert.That(table, Is.Not.Null);
            Assert.That(table.PersistenceType, Is.EqualTo("Unlogged"));
        }

        [Test]
        public void Load_MySQL_PreservesPlatformSpecificProperties()
        {
            var tableJson = @"{
                ""Name"": ""`TestTable`"",
                ""Engine"": ""InnoDB"",
                ""Columns"": []
            }";
            var filePath = Path.Combine("C:", "tables", "test.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.MySQL) as MySqlTable;

            Assert.That(table, Is.Not.Null);
            Assert.That(table.Engine, Is.EqualTo("InnoDB"));
        }

        // Closes the gap this test used to demonstrate: a table named "PreservesPlatformSpecificProperties"
        // let an unrecognized "FileGroup" property vanish silently (Newtonsoft's default
        // MissingMemberHandling.Ignore) while still passing, because the assertion was on
        // CompressionType, not FileGroup. Deploy now agrees with the editor (.json-schemas'
        // additionalProperties:false) and --Validate, both of which already rejected this.
        [Test]
        public void Load_UnrecognizedProperty_ThrowsNamingPropertyAndFile()
        {
            var tableJson = @"{
                ""Name"": ""[TestTable]"",
                ""NotARealTableProperty"": ""x"",
                ""Columns"": []
            }";
            var filePath = Path.Join("C:", "tables", "dbo.TestTable.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var ex = Assert.Throws<RunFailedException>(() => Table.Load(filePath, Platform.SqlServer));

            Assert.That(ex.Message, Does.Contain("NotARealTableProperty"));
            Assert.That(ex.Message, Does.Contain(filePath));
        }

        [Test]
        public void Load_ExtensionsContent_RoundTripsUntouched()
        {
            var tableJson = @"{
                ""Name"": ""[TestTable]"",
                ""Columns"": [],
                ""Extensions"": {
                    ""CustomFlag"": true,
                    ""Nested"": { ""Deep"": [1, 2, 3] }
                }
            }";
            var filePath = Path.Join("C:", "tables", "dbo.TestTable.json");
            _mockFile.Exists(filePath).Returns(true);
            _mockFile.ReadAllText(filePath).Returns(tableJson);

            var table = Table.Load(filePath, Platform.SqlServer);

            Assert.That(table.Extensions, Is.Not.Null);
            Assert.That(table.GetExtensionProperty("CustomFlag"), Is.EqualTo(true));
            Assert.That(((Newtonsoft.Json.Linq.JObject)table.Extensions)["Nested"]?["Deep"]?.ToString(),
                Does.Contain("1"));
        }
    }
}
