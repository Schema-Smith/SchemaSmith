// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using System.Linq;
using log4net;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Domain;

/// <summary>
/// TRANSITIONAL (MySQL column-level CheckExpression retirement).
/// <para>MySQL and MariaDB cannot round-trip a column-level check — the catalog has no link from a
/// check constraint back to a column — so authoring moved to table-level `CheckConstraints` and the
/// column property is kept as a deprecated alias that migrates at load. Deleting it outright would
/// have been silent data loss: the deployed CK_&lt;table&gt;_&lt;column&gt; constraint would become
/// an orphan and be dropped by the by-absence cleanup on the next quench, and a plain deploy never
/// runs the package validator that would flag the now-unknown key.</para>
/// </summary>
[TestFixture]
public class MySqlColumnCheckExpressionAliasTests
{
    private IFile _mockFile;
    private IDirectory _mockDirectory;
    private ILog _mockProgressLog;

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
        _mockFile = Substitute.For<IFile>();
        _mockDirectory = Substitute.For<IDirectory>();
        FactoryContainer.Register<IFile>(_mockFile);
        FactoryContainer.Register<IDirectory>(_mockDirectory);
        _mockProgressLog = Substitute.For<ILog>();
        LogFactory.Register("ProgressLog", _mockProgressLog);
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    private static string TemplateFile => Path.Join("C:", "products", "Templates", "Main", "Template.json");
    private static string TablesPath => Path.Join("C:", "products", "Templates", "Main", "Tables");
    private static string TableFile => Path.Join(TablesPath, "Orders.json");

    private Template LoadWith(Platform platform, string tableJson)
    {
        _mockFile.Exists(TemplateFile).Returns(true);
        _mockFile.ReadAllText(TemplateFile).Returns(@"{ ""Name"": ""Main"", ""DatabaseIdentificationScript"": ""SELECT 1"" }");
        _mockDirectory.Exists(TablesPath).Returns(true);
        _mockDirectory.GetFiles(TablesPath, "*.json", SearchOption.AllDirectories).Returns([TableFile]);
        _mockFile.Exists(TableFile).Returns(true);
        _mockFile.ReadAllText(TableFile).Returns(tableJson);

        return Template.Load("Main", new Product
        {
            Name = "TestProduct",
            Platform = platform,
            FilePath = Path.Join("C:", "products", "Product.json")
        });
    }

    private const string MySqlTableWithColumnCheck = @"{
        ""Name"": ""`Orders`"",
        ""Columns"": [
            { ""Name"": ""`Id`"", ""DataType"": ""INT"", ""Nullable"": false },
            { ""Name"": ""`Status`"", ""DataType"": ""INT"", ""Nullable"": true, ""CheckExpression"": ""`Status` >= 0"" }
        ]
    }";

    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void ColumnCheckExpression_IsMigratedToTableLevel_AndColumnCleared(Platform platform)
    {
        var template = LoadWith(platform, MySqlTableWithColumnCheck);

        var table = template.Tables.Single();
        var migrated = table.CheckConstraints.SingleOrDefault(c => c.Name == "CK_Orders_Status");

        Assert.That(migrated, Is.Not.Null, "the column-level check must arrive as a table-level constraint");
        Assert.That(migrated.Expression, Is.EqualTo("`Status` >= 0"));
        Assert.That(table.Columns.OfType<MySqlColumn>().Single(c => c.Name == "`Status`").CheckExpression,
            Is.Null.Or.Empty, "the alias must be cleared so only one code path applies the check");
    }

    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void ColumnCheckExpression_LogsDeprecationWarning(Platform platform)
    {
        LoadWith(platform, MySqlTableWithColumnCheck);

        _mockProgressLog.Received().Warn(Arg.Is<string>(s =>
            s.Contains("CheckExpression") && s.Contains("CheckConstraints") && s.Contains("Status")));
    }

    // A log line is invisible to --Validate, which reported a package using the alias as a clean PASS.
    // Recording the migration on the template is what lets the linter report it.
    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void ColumnCheckExpression_IsRecordedAsADeprecationNotice(Platform platform)
    {
        var template = LoadWith(platform, MySqlTableWithColumnCheck);

        var notice = template.DeprecationNotices.Single();
        Assert.That(notice.Code, Is.EqualTo("SS-DEP-002"));
        Assert.That(notice.Location, Is.EqualTo("Template 'Main' / Table '`Orders`'"));
        Assert.That(notice.Message, Does.Contain("Status").And.Contain("CheckConstraints").And.Contain("CK_Orders_Status"));
    }

    [Test]
    public void TableLevelCheckConstraints_RecordNoDeprecationNotice()
    {
        var template = LoadWith(Platform.MySQL, @"{
            ""Name"": ""`Orders`"",
            ""Columns"": [ { ""Name"": ""`Status`"", ""DataType"": ""INT"", ""Nullable"": true } ],
            ""CheckConstraints"": [ { ""Name"": ""CK_Orders_Status"", ""Expression"": ""`Status` >= 0"" } ]
        }");

        Assert.That(template.DeprecationNotices, Is.Empty);
    }

    [Test]
    public void ExplicitTableLevelConstraintOfSameName_Wins_AndIsNotDuplicated()
    {
        // The author has already migrated this one; the leftover alias must not overwrite it or
        // add a second constraint with the same name.
        var json = @"{
            ""Name"": ""`Orders`"",
            ""Columns"": [
                { ""Name"": ""`Status`"", ""DataType"": ""INT"", ""Nullable"": true, ""CheckExpression"": ""`Status` >= 0"" }
            ],
            ""CheckConstraints"": [
                { ""Name"": ""CK_Orders_Status"", ""Expression"": ""`Status` > 100"" }
            ]
        }";

        var table = LoadWith(Platform.MySQL, json).Tables.Single();

        Assert.That(table.CheckConstraints.Count(c => c.Name == "CK_Orders_Status"), Is.EqualTo(1));
        Assert.That(table.CheckConstraints.Single(c => c.Name == "CK_Orders_Status").Expression,
            Is.EqualTo("`Status` > 100"), "the explicitly authored table-level constraint wins");
    }

    [Test]
    public void PostgreSql_ColumnCheckExpression_IsNotMigrated()
    {
        // PostgreSQL keeps column-level authoring — pg_constraint.conkey attributes a single-column
        // check back to its column, so it round-trips.
        var json = @"{
            ""Schema"": ""\""public\"""",
            ""Name"": ""\""Orders\"""",
            ""Columns"": [
                { ""Name"": ""\""Status\"""", ""DataType"": ""INT"", ""Nullable"": true, ""CheckExpression"": ""\""Status\"" >= 0"" }
            ]
        }";

        var table = LoadWith(Platform.PostgreSQL, json).Tables.Single();

        Assert.That(table.CheckConstraints, Is.Empty, "PostgreSQL must not migrate column checks to table level");
        Assert.That(table.Columns.OfType<PostgreSqlColumn>().Single().CheckExpression, Is.Not.Null.And.Not.Empty);
        _mockProgressLog.DidNotReceive().Warn(Arg.Is<string>(s => s.Contains("CheckExpression")));
    }

    [Test]
    public void MySqlTableWithoutColumnCheck_IsUntouched_AndDoesNotWarn()
    {
        var json = @"{
            ""Name"": ""`Orders`"",
            ""Columns"": [ { ""Name"": ""`Id`"", ""DataType"": ""INT"", ""Nullable"": false } ]
        }";

        var table = LoadWith(Platform.MySQL, json).Tables.Single();

        Assert.That(table.CheckConstraints, Is.Empty);
        _mockProgressLog.DidNotReceive().Warn(Arg.Is<string>(s => s.Contains("CheckExpression")));
    }
}
