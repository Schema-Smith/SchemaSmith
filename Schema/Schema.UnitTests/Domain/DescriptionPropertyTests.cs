// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using Newtonsoft.Json;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace Schema.UnitTests.Domain;

// One spelling, Description, on every engine and every object that can carry one. The file keeps the three
// states apart -- omitted (leave the database's description alone), "" (remove it), text (set it) -- so
// null must stay out of the file and "" must stay in it.
[TestFixture]
public class DescriptionPropertyTests
{
    private static readonly Type[] DescribedTypes =
    [
        typeof(Table), typeof(Column), typeof(Schema.Domain.Index),
        typeof(SqlServerTable), typeof(SqlServerColumn), typeof(SqlServerIndex), typeof(SqlServerIndexedView),
        typeof(PostgreSqlTable), typeof(PostgreSqlColumn), typeof(PostgreSqlIndex),
        typeof(PostgreSqlMaterializedView), typeof(PostgreSqlSequence), typeof(PostgreSqlEnumType), typeof(PostgreSqlDomainType),
        typeof(MySqlTable), typeof(MySqlColumn), typeof(MySqlIndex), typeof(Schema.Domain.MySQL.FullTextIndex), typeof(MySqlEvent),
        typeof(MariaDbTable), typeof(MariaDbColumn)
    ];

    [Test]
    public void EveryDescribableShape_HasAStringDescription()
    {
        var missing = DescribedTypes.Where(t => t.GetProperty("Description")?.PropertyType != typeof(string)).Select(t => t.Name).ToList();
        Assert.That(missing, Is.Empty, "every one of these carries a description on its engine");
    }

    [Test]
    public void TheDescriptionFollowsTheName_AndPrecedesTheColumns()
    {
        var table = new SqlServerTable { Name = "[Orders]", Description = "all the orders" };
        table.Columns.Add(new SqlServerColumn { Name = "[Id]", DataType = "INT", Description = "the key" });
        table.Indexes.Add(new SqlServerIndex { Name = "[PK]", IndexColumns = "[Id]", PrimaryKey = true, Description = "the key index" });

        var json = JsonHelper.Serialize(table);

        Assert.Multiple(() =>
        {
            Assert.That(json.IndexOf("\"Name\"", StringComparison.Ordinal), Is.LessThan(json.IndexOf("\"Description\"", StringComparison.Ordinal)), json);
            Assert.That(json.IndexOf("\"Description\"", StringComparison.Ordinal), Is.LessThan(json.IndexOf("\"Columns\"", StringComparison.Ordinal)), json);
            Assert.That(json.IndexOf("\"DataType\"", StringComparison.Ordinal), Is.LessThan(json.IndexOf("\"the key\"", StringComparison.Ordinal)), json);
            Assert.That(json.IndexOf("\"IndexColumns\"", StringComparison.Ordinal), Is.LessThan(json.IndexOf("\"the key index\"", StringComparison.Ordinal)), json);
        });
    }

    [Test]
    public void AnOmittedDescription_StaysOutOfTheFile_AndAnEmptyOne_StaysIn()
    {
        var omitted = JsonHelper.Serialize(new PostgreSqlTable { Name = "t" });
        var cleared = JsonHelper.Serialize(new PostgreSqlTable { Name = "t", Description = "" });

        Assert.Multiple(() =>
        {
            Assert.That(omitted, Does.Not.Contain("Description"), "omitted means the deploy leaves the database's description alone");
            Assert.That(cleared, Does.Contain("\"Description\":\"\"").Or.Contain("\"Description\": \"\""), "an empty string is the request to remove it");
        });
    }

    [Test]
    public void MySqlFamily_ReadsBothSpellings_AndWritesOnlyTheOneItWasGiven()
    {
        var table = PlatformDeserializer.DeserializeTable(
            """{ "Name": "`t`", "Comment": "old", "Columns": [ { "Name": "`c`", "DataType": "INT", "Description": "new" } ] }""",
            Platform.MariaDb) as MariaDbTable;

        Assert.That(table, Is.Not.Null);
        var column = (MySqlColumn)table!.Columns[0];
        Assert.Multiple(() =>
        {
            Assert.That(table.Comment, Is.EqualTo("old"), "the deprecated alias still loads");
            Assert.That(table.Description, Is.Null);
            Assert.That(column.Description, Is.EqualTo("new"));
            Assert.That(column.Comment, Is.Null);
        });

        var json = JsonHelper.Serialize(table);
        Assert.That(json, Does.Contain("\"Comment\":\"old\"").Or.Contain("\"Comment\": \"old\""));
        Assert.That(json.Split("\"Description\"").Length - 1, Is.EqualTo(1), "only the column declares a Description");
    }

    [Test]
    public void AnEventReadsBothSpellings()
    {
        var ev = JsonConvert.DeserializeObject<MySqlEvent>("""{ "Name": "`e`", "Definition": "DO 1", "Comment": "old" }""");
        Assert.That(ev!.Comment, Is.EqualTo("old"));
        Assert.That(ev.Description, Is.Null);
    }
}
