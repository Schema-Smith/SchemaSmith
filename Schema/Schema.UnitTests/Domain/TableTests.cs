// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Newtonsoft.Json;
using Schema.Domain;

using Schema.Delivery;
namespace Schema.UnitTests.Domain
{
    [TestFixture]
    public class TableTests
    {
        [Test]
        public void DefaultValues_AreCorrect()
        {
            var table = new Table();

            Assert.That(table.Name, Is.EqualTo(""));
            Assert.That(table.Columns, Is.Not.Null);
            Assert.That(table.Columns, Is.Empty);
            Assert.That(table.Indexes, Is.Not.Null);
            Assert.That(table.Indexes, Is.Empty);
            Assert.That(table.ForeignKeys, Is.Not.Null);
            Assert.That(table.ForeignKeys, Is.Empty);
            Assert.That(table.CheckConstraints, Is.Not.Null);
            Assert.That(table.CheckConstraints, Is.Empty);
            Assert.That(table.ShouldApplyExpression, Is.Null);
            Assert.That(table.DataDelivery, Is.Empty);
            Assert.That(table.OldName, Is.Null);
        }

        [Test]
        public void DataDelivery_MergeType_AcceptsStandardizedValues()
        {
            var table = new Table();

            table.DataDelivery = [new DataDelivery { MergeType = "None" }];
            Assert.That(table.DataDelivery[0].MergeType, Is.EqualTo("None"));

            table.DataDelivery = [new DataDelivery { MergeType = "Insert" }];
            Assert.That(table.DataDelivery[0].MergeType, Is.EqualTo("Insert"));

            table.DataDelivery = [new DataDelivery { MergeType = "Insert/Update" }];
            Assert.That(table.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));

            table.DataDelivery = [new DataDelivery { MergeType = "Insert/Update/Delete" }];
            Assert.That(table.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update/Delete"));
        }

        [Test]
        public void JsonRoundTrip_WithNestedObjects()
        {
            var table = new Table
            {
                Name = "Customer",
                DataDelivery = [new DataDelivery { MergeType = "Insert/Update", MatchColumns = "Id" }],
                Columns = new List<Column>
                {
                    new Column { Name = "Id", DataType = "int", Nullable = false },
                    new Column { Name = "Name", DataType = "varchar(100)", Nullable = true }
                },
                Indexes = new List<Index>
                {
                    new Index { Name = "PK_Customer", PrimaryKey = true, IndexColumns = "Id ASC" }
                },
                ForeignKeys = new List<ForeignKey>
                {
                    new ForeignKey { Name = "FK_Customer_Region", Columns = "RegionId", RelatedTable = "Region", RelatedColumns = "Id" }
                },
                CheckConstraints = new List<CheckConstraint>
                {
                    new CheckConstraint { Name = "CK_Customer_Name", Expression = "LEN(Name) > 0" }
                }
            };

            var json = JsonConvert.SerializeObject(table);
            var deserialized = JsonConvert.DeserializeObject<Table>(json);

            Assert.That(deserialized.Name, Is.EqualTo("Customer"));
            Assert.That(deserialized.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
            Assert.That(deserialized.DataDelivery[0].MatchColumns, Is.EqualTo("Id"));
            Assert.That(deserialized.Columns, Has.Count.EqualTo(2));
            Assert.That(deserialized.Columns[0].Name, Is.EqualTo("Id"));
            Assert.That(deserialized.Columns[1].Nullable, Is.True);
            Assert.That(deserialized.Indexes, Has.Count.EqualTo(1));
            Assert.That(deserialized.Indexes[0].PrimaryKey, Is.True);
            Assert.That(deserialized.ForeignKeys, Has.Count.EqualTo(1));
            Assert.That(deserialized.ForeignKeys[0].RelatedTable, Is.EqualTo("Region"));
            Assert.That(deserialized.CheckConstraints, Has.Count.EqualTo(1));
            Assert.That(deserialized.CheckConstraints[0].Expression, Is.EqualTo("LEN(Name) > 0"));
        }

        [Test]
        public void JsonRoundTrip_PreservesVariantName()
        {
            var table = new Table { Name = "Orders", ShouldApplyExpression = "1=1", VariantName = "EU region" };
            var json = JsonConvert.SerializeObject(table);
            var deserialized = JsonConvert.DeserializeObject<Table>(json);
            Assert.That(deserialized.VariantName, Is.EqualTo("EU region"));
        }

        [Test]
        public void DropColumnsRemovedFromProduct_AbsentInJson_IsNull()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T" }""");
            Assert.That(table.DropColumnsRemovedFromProduct, Is.Null);
        }

        [Test]
        public void DropColumnsRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropColumnsRemovedFromProduct": false }""");
            Assert.That(table.DropColumnsRemovedFromProduct, Is.False);
        }

        [Test]
        public void DropColumnsRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropColumnsRemovedFromProduct": true }""");
            Assert.That(table.DropColumnsRemovedFromProduct, Is.True);
        }

        [Test]
        public void DropForeignKeysRemovedFromProduct_AbsentInJson_IsNull()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T" }""");
            Assert.That(table.DropForeignKeysRemovedFromProduct, Is.Null);
        }

        [Test]
        public void DropForeignKeysRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropForeignKeysRemovedFromProduct": false }""");
            Assert.That(table.DropForeignKeysRemovedFromProduct, Is.False);
        }

        [Test]
        public void DropForeignKeysRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropForeignKeysRemovedFromProduct": true }""");
            Assert.That(table.DropForeignKeysRemovedFromProduct, Is.True);
        }

        [Test]
        public void DropCheckConstraintsRemovedFromProduct_AbsentInJson_IsNull()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T" }""");
            Assert.That(table.DropCheckConstraintsRemovedFromProduct, Is.Null);
        }

        [Test]
        public void DropCheckConstraintsRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropCheckConstraintsRemovedFromProduct": false }""");
            Assert.That(table.DropCheckConstraintsRemovedFromProduct, Is.False);
        }

        [Test]
        public void DropCheckConstraintsRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropCheckConstraintsRemovedFromProduct": true }""");
            Assert.That(table.DropCheckConstraintsRemovedFromProduct, Is.True);
        }

        [Test]
        public void DropExcludeConstraintsRemovedFromProduct_AbsentInJson_IsNull()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T" }""");
            Assert.That(table.DropExcludeConstraintsRemovedFromProduct, Is.Null);
        }
        [Test]
        public void DropExcludeConstraintsRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropExcludeConstraintsRemovedFromProduct": false }""");
            Assert.That(table.DropExcludeConstraintsRemovedFromProduct, Is.False);
        }
        [Test]
        public void DropExcludeConstraintsRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropExcludeConstraintsRemovedFromProduct": true }""");
            Assert.That(table.DropExcludeConstraintsRemovedFromProduct, Is.True);
        }
        [Test]
        public void DropStatisticsRemovedFromProduct_AbsentInJson_IsNull()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T" }""");
            Assert.That(table.DropStatisticsRemovedFromProduct, Is.Null);
        }
        [Test]
        public void DropStatisticsRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropStatisticsRemovedFromProduct": false }""");
            Assert.That(table.DropStatisticsRemovedFromProduct, Is.False);
        }
        [Test]
        public void DropStatisticsRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropStatisticsRemovedFromProduct": true }""");
            Assert.That(table.DropStatisticsRemovedFromProduct, Is.True);
        }

        [Test]
        public void DropIndexesRemovedFromProduct_AbsentInJson_IsNull()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T" }""");
            Assert.That(table.DropIndexesRemovedFromProduct, Is.Null);
        }
        [Test]
        public void DropIndexesRemovedFromProduct_ExplicitFalse_IsFalse()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropIndexesRemovedFromProduct": false }""");
            Assert.That(table.DropIndexesRemovedFromProduct, Is.False);
        }
        [Test]
        public void DropIndexesRemovedFromProduct_ExplicitTrue_IsTrue()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "T", "DropIndexesRemovedFromProduct": true }""");
            Assert.That(table.DropIndexesRemovedFromProduct, Is.True);
        }

        [Test]
        public void PreventDrop_DefaultsToFalse()
        {
            var table = new Table();
            Assert.That(table.PreventDrop, Is.False);
        }

        [Test]
        public void PreventDrop_RoundTripsThroughJson()
        {
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Table>(
                """{ "Name": "[Orders]", "PreventDrop": true }""");
            Assert.That(table!.PreventDrop, Is.True);
        }

        // A token's name and value are free text: a name such as a(b or a value holding $1 must replace as written.
        [TestCase("a(b", "1 = 1")]
        [TestCase("Gate)", "1 = 1")]
        [TestCase("a[b", "1 = 1")]
        [TestCase("Price", "$1 + $0")]
        public void TableTokenReplace_TreatsTheNameAndValueAsText(string name, string value)
        {
            var result = Table.TableTokenReplace("WHERE {{" + name.ToUpperInvariant() + "}} AND {{" + name + "}}", [new(name, value)]);

            Assert.That(result, Is.EqualTo("WHERE " + value + " AND " + value));
        }
    }
}
