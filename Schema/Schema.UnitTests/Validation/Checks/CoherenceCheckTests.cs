// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

/// <summary>
/// Slice 2.2: cross-object coherence (FK/index). Structural reference checks only — no type
/// comparisons, no DeleteAction/UpdateAction checks (out of scope; those belong to JSON-schema
/// lint). Covers FK local-column, related-table resolution (incl. schema defaulting),
/// related-column, cardinality, and index-column existence. No ambiguity check: FK targets always
/// resolve to a concrete schema post schema-resolution (SchemaDefaultResolver.
/// ResolveRelatedTableSchema runs inside Template.Load), so an unqualified reference is never
/// actually ambiguous — it either resolves-and-exists (fine) or resolves-and-missing (SS-FK-002).
/// </summary>
[TestFixture]
public class CoherenceCheckTests
{
    private static Product Product() => new()
    {
        Name = "Acme",
        Platform = Platform.SqlServer,
        TemplateOrder = new System.Collections.Generic.List<string>()
    };

    private static ValidationContext Context(params Template[] templates) =>
        new(Product(), templates, "pkg");

    private static Template TemplateWithTables(string templateName, params SqlServerTable[] tables)
    {
        var template = new Template { Name = templateName };
        foreach (var table in tables) template.Tables.Add(table);
        return template;
    }

    private static SqlServerTable Customer(string schema = "dbo") => new()
    {
        Name = "Customer",
        Schema = schema,
        Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } }
    };

    private static Finding[] RunOn(SqlServerTable table) =>
        new CoherenceCheck().Run(Context(TemplateWithTables("T", table))).ToArray();

    private static Finding[] RunOnMy(Schema.Domain.MySQL.MySqlTable table)
    {
        var template = new Template { Name = "T" };
        template.Tables.Add(table);
        var product = new Product { Name = "Acme", Platform = Platform.MySQL, TemplateOrder = new System.Collections.Generic.List<string>() };
        return new CoherenceCheck().Run(new ValidationContext(product, new[] { template }, "pkg")).ToArray();
    }

    private static Finding[] RunOnMaria(Schema.Domain.MariaDb.MariaDbTable table)
    {
        var template = new Template { Name = "T" };
        template.Tables.Add(table);
        var product = new Product { Name = "Acme", Platform = Platform.MariaDb, TemplateOrder = new System.Collections.Generic.List<string>() };
        return new CoherenceCheck().Run(new ValidationContext(product, new[] { template }, "pkg")).ToArray();
    }

    private static Finding[] RunOnPgTable(Schema.Domain.PostgreSQL.PostgreSqlTable table)
    {
        var template = new Template { Name = "T" };
        template.Tables.Add(table);
        var product = new Product { Name = "Acme", Platform = Platform.PostgreSQL, TemplateOrder = new System.Collections.Generic.List<string>() };
        return new CoherenceCheck().Run(new ValidationContext(product, new[] { template }, "pkg")).ToArray();
    }

    // ---- PostgreSQL names compare exactly ----
    //
    // SchemaSmith deploys every PostgreSQL name quoted, so a key to `Customer` where the table is `customer` fails the
    // deploy with 42P01. The other engines compare names without case.

    private static Finding[] RunPgOrderAndCustomer(string relatedTable, string relatedColumns, string localColumns = "customer_id",
        string keyColumns = "id")
    {
        var order = new Schema.Domain.PostgreSQL.PostgreSqlTable
        {
            Name = "order",
            Schema = "public",
            Columns = { new Schema.Domain.PostgreSQL.PostgreSqlColumn { Name = "customer_id", DataType = "integer" } },
            Indexes = { new Schema.Domain.PostgreSQL.PostgreSqlIndex { Name = "ix_order_customer", IndexColumns = localColumns } },
            ForeignKeys =
            {
                new Schema.Domain.PostgreSQL.PostgreSqlForeignKey
                {
                    Name = "fk_order_customer", Columns = localColumns, RelatedTableSchema = "public", RelatedTable = relatedTable,
                    RelatedColumns = relatedColumns
                }
            }
        };
        var customer = new Schema.Domain.PostgreSQL.PostgreSqlTable
        {
            Name = "customer",
            Schema = "public",
            Columns = { new Schema.Domain.PostgreSQL.PostgreSqlColumn { Name = "id", DataType = "integer" } },
            Indexes = { new Schema.Domain.PostgreSQL.PostgreSqlIndex { Name = "pk_customer", IndexColumns = keyColumns, PrimaryKey = true } }
        };
        var template = new Template { Name = "T" };
        template.Tables.Add(order);
        template.Tables.Add(customer);
        var product = new Product { Name = "Acme", Platform = Platform.PostgreSQL, TemplateOrder = new System.Collections.Generic.List<string>() };
        return new CoherenceCheck().Run(new ValidationContext(product, new[] { template }, "pkg")).ToArray();
    }

    [Test]
    public void PostgreSqlKey_ToTheSameSpelling_IsClean()
    {
        Assert.That(RunPgOrderAndCustomer("customer", "id"), Is.Empty);
        Assert.That(RunPgOrderAndCustomer("\"customer\"", "\"id\""), Is.Empty, "a quoted name is the same name");
    }

    [Test]
    public void PostgreSqlKey_ToADifferentCase_DoesNotResolve()
    {
        var findings = RunPgOrderAndCustomer("Customer", "id");
        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-FK-002" }),
            string.Join("; ", findings.Select(f => f.Code + " " + f.Message)));
    }

    [Test]
    public void PostgreSqlKey_ToARelatedColumnOfADifferentCase_IsError()
    {
        Assert.That(RunPgOrderAndCustomer("customer", "Id").Select(f => f.Code), Is.EqualTo(new[] { "SS-FK-004" }));
    }

    [Test]
    public void PostgreSqlKey_AndIndex_OnALocalColumnOfADifferentCase_AreErrors()
    {
        Assert.That(RunPgOrderAndCustomer("customer", "id", localColumns: "Customer_Id").Select(f => f.Code).OrderBy(c => c).ToArray(),
            Is.EqualTo(new[] { "SS-FK-001", "SS-IDX-001" }));
    }

    [Test]
    public void SqlServerKey_ToADifferentCase_Resolves()
    {
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
            ForeignKeys = { new SqlServerForeignKey { Name = "FK_Order_Customer", Columns = "customerid", RelatedTable = "CUSTOMER", RelatedTableSchema = "DBO", RelatedColumns = "ID" } }
        };
        Assert.That(new CoherenceCheck().Run(Context(TemplateWithTables("Main", order, Customer()))), Is.Empty);
    }

    // ---- A foreign key resolves within its own template (SS-FK-010) ----
    //
    // Each template deploys to the databases its DatabaseIdentificationScript names, so a table declared only in
    // another template is on this one's target only when both scripts name the same databases.

    private static Finding[] RunOrderAndCustomerInTwoTemplates(string orderDatabases, string customerDatabases)
    {
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
            ForeignKeys = { new SqlServerForeignKey { Name = "FK_Order_Customer", Columns = "CustomerId", RelatedTable = "Customer", RelatedTableSchema = "dbo", RelatedColumns = "Id" } }
        };
        var sales = TemplateWithTables("Sales", order);
        sales.DatabaseIdentificationScript = orderDatabases;
        var crm = TemplateWithTables("Crm", Customer());
        crm.DatabaseIdentificationScript = customerDatabases;
        return new CoherenceCheck().Run(Context(crm, sales)).ToArray();
    }

    [Test]
    public void ForeignKey_ToATableInATemplateOnOtherDatabases_WarnsNamingThatTemplate()
    {
        var findings = RunOrderAndCustomerInTwoTemplates("SELECT 'Sales'", "SELECT 'Crm'");
        Assert.That(findings.Select(f => (f.Code, f.Severity)), Is.EqualTo(new[] { ("SS-FK-010", Severity.Warning) }),
            string.Join("; ", findings.Select(f => f.Code + " " + f.Message)));
        Assert.That(findings[0].Message, Does.Contain("template 'Crm'").And.Contain("'Sales'"));
        Assert.That(findings[0].Location, Is.EqualTo("Template 'Sales' / Table 'Order' / FK 'FK_Order_Customer'"));
    }

    [Test]
    public void ForeignKey_ToATableInATemplateOnTheSameDatabases_Resolves()
    {
        Assert.That(RunOrderAndCustomerInTwoTemplates("SELECT 'Shop'", " SELECT 'Shop' "), Is.Empty);
    }

    // ---- Temporal keys (SS-IDX-002 / SS-IDX-003) ----

    private static Schema.Domain.MariaDb.MariaDbTable MariaRates(string keyColumns, bool withoutOverlaps, bool unique = true, bool withPeriod = true)
    {
        var table = new Schema.Domain.MariaDb.MariaDbTable
        {
            Name = "`rates`",
            Columns =
            {
                new Schema.Domain.MariaDb.MariaDbColumn { Name = "`id`", DataType = "INT" },
                new Schema.Domain.MariaDb.MariaDbColumn { Name = "`s`", DataType = "DATE" },
                new Schema.Domain.MariaDb.MariaDbColumn { Name = "`e`", DataType = "DATE" }
            },
            Indexes = { new Schema.Domain.MariaDb.MariaDbIndex { Name = "`uk_rates`", IndexColumns = keyColumns, Unique = unique, WithoutOverlaps = withoutOverlaps } }
        };
        if (withPeriod) table.Periods.Add(new Schema.Domain.MariaDb.TablePeriod { Name = "`p`", StartColumn = "`s`", EndColumn = "`e`" });
        return table;
    }

    [Test]
    public void MariaDbTemporalKey_PeriodNamedLast_IsNotAMissingColumn()
    {
        Assert.That(RunOnMaria(MariaRates("`id`,`p`", withoutOverlaps: true)).Where(f => f.Code.StartsWith("SS-IDX")), Is.Empty);
    }

    [Test]
    public void MariaDbKey_NamingAPeriod_WithoutTheFlag_IsAMissingColumn()
    {
        Assert.That(RunOnMaria(MariaRates("`id`,`p`", withoutOverlaps: false)).Select(f => f.Code), Is.EqualTo(new[] { "SS-IDX-001" }));
    }

    [Test]
    public void MariaDbTemporalKey_WhoseLastPartIsNotAPeriod_IsError()
    {
        var findings = RunOnMaria(MariaRates("`id`,`s`", withoutOverlaps: true));
        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-IDX-003" }));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
    }

    [Test]
    public void MariaDbTemporalKey_OnATableWithNoPeriods_SaysSo()
    {
        var findings = RunOnMaria(MariaRates("`id`,`p`", withoutOverlaps: true, withPeriod: false));
        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-IDX-003", "SS-IDX-001" }));
        Assert.That(findings[0].Message, Does.Contain("declares none"));
    }

    [Test]
    public void MariaDbTemporalKey_ThatIsNotUnique_IsError()
    {
        Assert.That(RunOnMaria(MariaRates("`id`,`p`", withoutOverlaps: true, unique: false)).Select(f => f.Code), Is.EqualTo(new[] { "SS-IDX-002" }));
    }

    // ---- ON DELETE SET NULL (columns) (SS-FK-007) ----

    private static Schema.Domain.PostgreSQL.PostgreSqlTable PgChild(string setNullColumns, string deleteAction = "SET NULL") => new()
    {
        Name = "child",
        Schema = "public",
        Columns =
        {
            new Schema.Domain.PostgreSQL.PostgreSqlColumn { Name = "id", DataType = "integer" },
            new Schema.Domain.PostgreSQL.PostgreSqlColumn { Name = "parent_id", DataType = "integer" },
            new Schema.Domain.PostgreSQL.PostgreSqlColumn { Name = "parent_code", DataType = "integer" }
        },
        ForeignKeys =
        {
            new Schema.Domain.PostgreSQL.PostgreSqlForeignKey
            {
                Name = "fk_child_parent", Columns = "parent_id,parent_code", RelatedTableSchema = "public", RelatedTable = "parent",
                RelatedColumns = "id,code", DeleteAction = deleteAction, SetNullColumns = setNullColumns
            }
        }
    };

    [Test]
    public void SetNullColumns_OverASubsetOfTheKey_WithSetNull_IsClean()
    {
        Assert.That(RunOnPgTable(PgChild("parent_id")).Where(f => f.Code == "SS-FK-007"), Is.Empty);
    }

    [Test]
    public void SetNullColumns_WithoutSetNull_IsError()
    {
        var findings = RunOnPgTable(PgChild("parent_id", deleteAction: "CASCADE")).Where(f => f.Code == "SS-FK-007").ToList();
        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Message, Does.Contain("CASCADE"));
    }

    [Test]
    public void SetNullColumns_NamingAColumnOutsideTheKey_IsError()
    {
        var findings = RunOnPgTable(PgChild("parent_id,id")).Where(f => f.Code == "SS-FK-007").ToList();
        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Message, Does.Contain("'id'"));
    }

    [Test]
    public void PostgreSqlTemporalKey_OnAUniqueIndexThatIsNotAConstraint_IsError()
    {
        var table = new Schema.Domain.PostgreSQL.PostgreSqlTable
        {
            Name = "rates",
            Schema = "public",
            Columns =
            {
                new Schema.Domain.PostgreSQL.PostgreSqlColumn { Name = "id", DataType = "integer" },
                new Schema.Domain.PostgreSQL.PostgreSqlColumn { Name = "valid", DataType = "daterange" }
            },
            Indexes = { new Schema.Domain.PostgreSQL.PostgreSqlIndex { Name = "uq_rates", IndexColumns = "id,valid", Unique = true, WithoutOverlaps = true } }
        };
        Assert.That(RunOnPgTable(table).Select(f => f.Code), Is.EqualTo(new[] { "SS-IDX-002" }));

        table.Indexes[0].UniqueConstraint = true;
        Assert.That(RunOnPgTable(table), Is.Empty);
    }

    // ---- Spatial indexes (SS-SPX-001 .. SS-SPX-004) ----
    //
    // A spatial index is on one geometry or geography column, with a tessellation of the same family; a geometry index
    // needs a bounding box and a geography one cannot have one; grid densities belong to the GRID schemes only. The engine
    // refuses every other shape, so the package is stopped first.

    private static SqlServerTable Places(params SpatialIndex[] indexes)
    {
        var table = new SqlServerTable
        {
            Name = "Places", Schema = "dbo",
            Columns =
            {
                new SqlServerColumn { Name = "Id", DataType = "int" },
                new SqlServerColumn { Name = "Shape", DataType = "geometry" },
                new SqlServerColumn { Name = "Location", DataType = "GEOGRAPHY" },
                new SqlServerColumn { Name = "Label", DataType = "nvarchar(50)" }
            }
        };
        table.SpatialIndexes.AddRange(indexes);
        return table;
    }

    private static readonly SpatialBoundingBox Box = new() { XMin = 0, YMin = 0, XMax = 100, YMax = 100 };

    private static string[] SpatialCodes(SqlServerTable table) => RunOn(table).Where(f => f.Code.StartsWith("SS-SPX")).Select(f => f.Code).ToArray();

    [Test]
    public void SpatialIndexes_OfEveryWellFormedShape_AreClean()
    {
        Assert.That(SpatialCodes(Places(
            new SpatialIndex { Name = "SX_Shape", Column = "[Shape]", Tessellation = "GEOMETRY_GRID", BoundingBox = Box, Grids = new SpatialGrids { Level1 = "LOW" } },
            new SpatialIndex { Name = "SX_Shape_Auto", Column = "Shape", Tessellation = "GEOMETRY_AUTO_GRID", BoundingBox = Box },
            new SpatialIndex { Name = "SX_Location", Column = "Location" },
            new SpatialIndex { Name = "SX_Location_Grid", Column = "Location", Tessellation = "GEOGRAPHY_GRID", CellsPerObject = 32 })), Is.Empty);
    }

    [Test]
    public void ASpatialIndexOnAMissingOrNonSpatialColumn_IsAnError()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpatialCodes(Places(new SpatialIndex { Name = "SX_Gone", Column = "Gone" })), Is.EqualTo(new[] { "SS-SPX-001" }));
            Assert.That(SpatialCodes(Places(new SpatialIndex { Name = "SX_Label", Column = "Label" })), Is.EqualTo(new[] { "SS-SPX-001" }));
        });
    }

    [Test]
    public void ATessellationOfTheWrongFamily_IsAnError()
    {
        Assert.That(SpatialCodes(Places(new SpatialIndex { Name = "SX_Location", Column = "Location", Tessellation = "GEOMETRY_GRID", BoundingBox = Box })),
            Is.EqualTo(new[] { "SS-SPX-002" }));
    }

    [Test]
    public void TheBoundingBox_IsRequiredOnGeometry_ForbiddenOnGeography_AndMustBeAnArea()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpatialCodes(Places(new SpatialIndex { Name = "SX_Shape", Column = "Shape" })), Is.EqualTo(new[] { "SS-SPX-003" }), "geometry without one");
            Assert.That(SpatialCodes(Places(new SpatialIndex { Name = "SX_Location", Column = "Location", BoundingBox = Box })), Is.EqualTo(new[] { "SS-SPX-003" }), "geography with one");
            Assert.That(SpatialCodes(Places(new SpatialIndex { Name = "SX_Shape", Column = "Shape", BoundingBox = new SpatialBoundingBox { XMin = 5, YMin = 0, XMax = 5, YMax = 10 } })),
                Is.EqualTo(new[] { "SS-SPX-003" }), "an empty box");
        });
    }

    [Test]
    public void GridsOnAnAutoGridTessellation_AreAnError()
    {
        Assert.That(SpatialCodes(Places(new SpatialIndex { Name = "SX_Location", Column = "Location", Tessellation = "GEOGRAPHY_AUTO_GRID", Grids = new SpatialGrids { Level1 = "HIGH" } })),
            Is.EqualTo(new[] { "SS-SPX-004" }));
    }

    // ---- Period columns (SS-TMP-001 .. SS-TMP-004) ----
    //
    // A temporal table's period columns are declared as a pair: one RowStart and one RowEnd, both DATETIME2, on a table
    // that is system-versioned. Anything else cannot be created, so the package is stopped before a deploy is.

    private static SqlServerTable Temporal(bool isTemporal, params SqlServerColumn[] periodColumns)
    {
        var table = new SqlServerTable { Name = "Booking", Schema = "dbo", IsTemporal = isTemporal, Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } } };
        foreach (var c in periodColumns) table.Columns.Add(c);
        return table;
    }

    private static SqlServerColumn Period(string name, string role, string dataType = "DATETIME2(2)") =>
        new() { Name = name, DataType = dataType, GeneratedAlways = role, Hidden = true };

    [Test]
    public void PeriodColumns_DeclaredAsAPairOnATemporalTable_AreClean()
    {
        Assert.That(RunOn(Temporal(true, Period("SysStart", "RowStart"), Period("SysEnd", "RowEnd"))).Where(f => f.Code.StartsWith("SS-TMP")), Is.Empty);
    }

    [Test]
    public void APeriodColumnWithoutItsPartner_IsAnError()
    {
        var findings = RunOn(Temporal(true, Period("SysStart", "RowStart"))).Where(f => f.Code.StartsWith("SS-TMP")).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-TMP-001" }));
            Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
            Assert.That(findings[0].Message, Does.Contain("RowEnd"));
        });
    }

    [Test]
    public void ARoleDeclaredTwice_IsAnError()
    {
        var findings = RunOn(Temporal(true, Period("SysStart", "RowStart"), Period("Other", "RowStart"), Period("SysEnd", "RowEnd")));
        Assert.That(findings.Where(f => f.Code.StartsWith("SS-TMP")).Select(f => f.Code), Is.EqualTo(new[] { "SS-TMP-002" }));
    }

    [Test]
    public void PeriodColumnsOnATableThatIsNotTemporal_AreAnError()
    {
        var findings = RunOn(Temporal(false, Period("SysStart", "RowStart"), Period("SysEnd", "RowEnd")));
        Assert.That(findings.Where(f => f.Code.StartsWith("SS-TMP")).Select(f => f.Code), Is.EqualTo(new[] { "SS-TMP-003" }));
    }

    [Test]
    public void APeriodColumnThatIsNotDatetime2_IsAnError()
    {
        var findings = RunOn(Temporal(true, Period("SysStart", "RowStart", "DATETIME"), Period("SysEnd", "RowEnd")));
        Assert.Multiple(() =>
        {
            Assert.That(findings.Where(f => f.Code.StartsWith("SS-TMP")).Select(f => f.Code), Is.EqualTo(new[] { "SS-TMP-004" }));
            Assert.That(findings.Single(f => f.Code == "SS-TMP-004").Message, Does.Contain("SysStart"));
        });
    }

    // ---- Ledger generated columns and view (SS-LDG-001 .. SS-LDG-005) ----
    //
    // A ledger table's generated columns and view exist only on a ledger table; an append-only one has no End columns;
    // each role is one BIGINT column. Every other shape fails CREATE TABLE.

    private static SqlServerTable LedgerTable(string ledger, params SqlServerColumn[] columns)
    {
        var table = new SqlServerTable { Name = "Audit", Schema = "dbo", Ledger = ledger, Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } } };
        foreach (var c in columns) table.Columns.Add(c);
        return table;
    }

    private static SqlServerColumn LedgerColumn(string name, string role, string dataType = "BIGINT") => new() { Name = name, DataType = dataType, GeneratedAlways = role };

    private static string[] LedgerCodes(SqlServerTable table) => RunOn(table).Where(f => f.Code.StartsWith("SS-LDG") || f.Code.StartsWith("SS-TMP")).Select(f => f.Code).ToArray();

    [Test]
    public void DeclaredLedgerColumnsAndView_OnALedgerTable_AreClean_AndAreNotReadAsPeriodColumns()
    {
        var table = LedgerTable("Updatable", LedgerColumn("TxStart", "TransactionIdStart"), LedgerColumn("TxEnd", "TransactionIdEnd"),
                                LedgerColumn("SeqStart", "SequenceNumberStart"), LedgerColumn("SeqEnd", "sequencenumberend"));
        table.LedgerView = new SqlServerLedgerView { Name = "AuditView" };
        Assert.That(LedgerCodes(table), Is.Empty, "no SS-TMP finding either: the period pair rule is for RowStart and RowEnd");
    }

    [TestCase(null)]
    [TestCase("Off")]
    public void ALedgerColumnOnATableThatIsNotALedger_IsAnError(string ledger)
    {
        Assert.That(LedgerCodes(LedgerTable(ledger, LedgerColumn("TxStart", "TransactionIdStart"))), Is.EqualTo(new[] { "SS-LDG-001" }));
    }

    [Test]
    public void AnEndColumnOnAnAppendOnlyTable_IsAnError()
    {
        Assert.That(LedgerCodes(LedgerTable("AppendOnly", LedgerColumn("TxStart", "TransactionIdStart"), LedgerColumn("TxEnd", "TransactionIdEnd"))),
            Is.EqualTo(new[] { "SS-LDG-002" }));
    }

    [Test]
    public void ALedgerRoleDeclaredTwice_IsAnError()
    {
        Assert.That(LedgerCodes(LedgerTable("Updatable", LedgerColumn("A", "TransactionIdStart"), LedgerColumn("B", "transactionidstart"))),
            Is.EqualTo(new[] { "SS-LDG-003" }));
    }

    [Test]
    public void ALedgerViewOnATableThatIsNotALedger_IsAnError()
    {
        var table = LedgerTable(null);
        table.LedgerView = new SqlServerLedgerView { Name = "AuditView" };
        Assert.That(LedgerCodes(table), Is.EqualTo(new[] { "SS-LDG-004" }));
    }

    [Test]
    public void ALedgerColumnThatIsNotBigint_IsAnError()
    {
        var findings = RunOn(LedgerTable("Updatable", LedgerColumn("TxStart", "TransactionIdStart", "INT")));
        Assert.Multiple(() =>
        {
            Assert.That(findings.Where(f => f.Code.StartsWith("SS-LDG")).Select(f => f.Code), Is.EqualTo(new[] { "SS-LDG-005" }));
            Assert.That(findings.Single(f => f.Code == "SS-LDG-005").Message, Does.Contain("TxStart").And.Contain("BIGINT"));
        });
    }

    // ---- MariaDB explicit system-time columns (SS-TMP-001 .. SS-TMP-004) ----

    private static Schema.Domain.MariaDb.MariaDbTable Versioned(bool versioned, params Schema.Domain.MariaDb.MariaDbColumn[] columns)
    {
        var table = new Schema.Domain.MariaDb.MariaDbTable { Name = "Booking", IsSystemVersioned = versioned, Columns = { new Schema.Domain.MariaDb.MariaDbColumn { Name = "Id", DataType = "int" } } };
        foreach (var c in columns) table.Columns.Add(c);
        return table;
    }

    private static Schema.Domain.MariaDb.MariaDbColumn RowTime(string name, string role, string dataType = "TIMESTAMP(6)") => new() { Name = name, DataType = dataType, GeneratedAlways = role };

    private static string[] MariaTmpCodes(Schema.Domain.MariaDb.MariaDbTable table) => RunOnMaria(table).Where(f => f.Code.StartsWith("SS-TMP")).Select(f => f.Code).ToArray();

    [TestCase("TIMESTAMP(6)")]
    [TestCase("bigint unsigned")]
    [TestCase("BIGINT(20) UNSIGNED")]
    public void AnExplicitSystemTimePair_OnAVersionedMariaDbTable_IsClean(string dataType)
    {
        Assert.That(MariaTmpCodes(Versioned(true, RowTime("rs", "RowStart", dataType), RowTime("re", "rowend", dataType))), Is.Empty);
    }

    [Test]
    public void AnIncompleteRepeatedOrUnversionedOrMistypedMariaDbPair_IsAnError()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MariaTmpCodes(Versioned(true, RowTime("rs", "RowStart"))), Is.EqualTo(new[] { "SS-TMP-001" }));
            Assert.That(MariaTmpCodes(Versioned(true, RowTime("a", "RowStart"), RowTime("b", "RowStart"), RowTime("re", "RowEnd"))), Is.EqualTo(new[] { "SS-TMP-002" }));
            Assert.That(MariaTmpCodes(Versioned(false, RowTime("rs", "RowStart"), RowTime("re", "RowEnd"))), Is.EqualTo(new[] { "SS-TMP-003" }));
            Assert.That(MariaTmpCodes(Versioned(true, RowTime("rs", "RowStart", "DATETIME(6)"), RowTime("re", "RowEnd"))), Is.EqualTo(new[] { "SS-TMP-004" }));
        });
    }

    // ---- NullsNotDistinct on a primary key (SS-IDX-004) ----
    //
    // A primary key's columns cannot be NULL, so the clause has nothing to decide and the engine never records it on
    // one; the deploy leaves it off the key rather than re-creating the key on every run, and the package is told.

    [Test]
    public void PostgreSqlNullsNotDistinct_OnAPrimaryKey_IsAWarning()
    {
        var table = new Schema.Domain.PostgreSQL.PostgreSqlTable
        {
            Name = "t",
            Schema = "public",
            Columns = { new Schema.Domain.PostgreSQL.PostgreSqlColumn { Name = "id", DataType = "integer" } },
            Indexes = { new Schema.Domain.PostgreSQL.PostgreSqlIndex { Name = "pk_t", IndexColumns = "id", PrimaryKey = true, NullsNotDistinct = true } }
        };
        var findings = RunOnPgTable(table);
        Assert.Multiple(() =>
        {
            Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-IDX-004" }));
            Assert.That(findings[0].Severity, Is.EqualTo(Severity.Warning));
            Assert.That(findings[0].Message, Does.Contain("pk_t").And.Contain("primary key"));
        });

        table.Indexes[0].PrimaryKey = false;
        table.Indexes[0].UniqueConstraint = true;
        Assert.That(RunOnPgTable(table), Is.Empty, "on a unique constraint the clause is meaningful");
    }

    // ---- Product MinimumVersion the engine cannot resolve (SS-VER-001) ----
    //
    // A declared MinimumVersion the deploy cannot parse aborts the run before any object is touched --
    // correct fail-closed behaviour, but --Validate had nothing to say about it, so the author's first
    // signal was a refused deployment. MinimumVersion carries no [SchemaProperty], so no pattern reaches
    // the generated .json-schema and JsonSchemaCheck cannot see it either.
    //
    // The check calls the SAME VersionHelper.ParseDeclaredVersion the deploy calls, deliberately: any other
    // formulation lets the linter and the deploy disagree about what resolves, which is the bug one level up.

    private static Finding[] RunOnProduct(string minimumVersion, Platform platform)
    {
        var product = new Product
        {
            Name = "Acme",
            Platform = platform,
            MinimumVersion = minimumVersion,
            TemplateOrder = new System.Collections.Generic.List<string>()
        };
        return new CoherenceCheck().Run(
            new ValidationContext(product, new[] { new Template { Name = "T" } }, "pkg")).ToArray();
    }

    // SQL Server is the only platform where a WELL-FORMED value can fail to resolve: a year >= 2000 is
    // looked up in a closed table of release years, so an in-between year is unresolvable while looking
    // entirely plausible to whoever typed it.
    [TestCase("2013")]
    [TestCase("2018")]
    [TestCase("2020")]
    [TestCase("2023")]
    [TestCase("2024")]
    public void UnresolvableSqlServerYear_IsReported(string declared)
    {
        var findings = RunOnProduct(declared, Platform.SqlServer);
        Assert.That(findings.Select(f => f.Code), Does.Contain("SS-VER-001"),
            $"MinimumVersion '{declared}' is not a SQL Server release year, so ParseDeclaredVersion returns "
            + "null and the deploy aborts. --Validate must say so first.");
        Assert.That(findings.Single(f => f.Code == "SS-VER-001").Message, Does.Contain(declared),
            "the finding must name the offending value -- a message that does not is half a finding");
    }

    [TestCase("2008")]
    [TestCase("2016")]
    [TestCase("2022")]
    [TestCase("2025")]
    [TestCase("13")]
    public void ResolvableSqlServerVersion_IsNotReported(string declared) =>
        Assert.That(RunOnProduct(declared, Platform.SqlServer).Select(f => f.Code),
            Does.Not.Contain("SS-VER-001"), $"'{declared}' resolves, so there is nothing to report");

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void AbsentMinimumVersion_IsNotReported(string declared) =>
        Assert.That(RunOnProduct(declared, Platform.SqlServer).Select(f => f.Code),
            Does.Not.Contain("SS-VER-001"),
            "MinimumVersion is optional -- absent is not invalid, and the deploy returns early on it too");

    // The MySQL family and PostgreSQL parse arithmetically, so they resolve any well-formed value and can
    // only fail on a non-numeric one. Asserted so the check is known to behave per platform rather than
    // assumed to.
    [TestCase("8.0", Platform.MySQL)]
    [TestCase("10.6", Platform.MariaDb)]
    [TestCase("12", Platform.PostgreSQL)]
    public void ResolvableOnTheArithmeticPlatforms_IsNotReported(string declared, Platform platform) =>
        Assert.That(RunOnProduct(declared, platform).Select(f => f.Code), Does.Not.Contain("SS-VER-001"));

    [TestCase("eight", Platform.MySQL)]
    [TestCase("latest", Platform.PostgreSQL)]
    [TestCase("ten-six", Platform.MariaDb)]
    public void NonNumericOnTheArithmeticPlatforms_IsReported(string declared, Platform platform) =>
        Assert.That(RunOnProduct(declared, platform).Select(f => f.Code), Does.Contain("SS-VER-001"));


    // ---- Modeled folder objects declared BOTH ways (SS-ENUM-001 / SS-SEQ-001 / SS-DOM-001) ----
    //
    // Enum Types/, Sequences/ and Domain Types/ are additive by design: each holds declared .json and
    // scripted .sql side by side, and using one or the other is correct and common. Declaring the SAME
    // object both ways is the defect, and it used to validate clean -- the coexistence rule existed only
    // for scheduled events, even though all three of these are already shape-validated.

    private static Template PgTemplateWithFolder(string folderPath, params string[] scriptedFileNames)
    {
        var template = new Template { Name = "T" };
        var folder = new TemplateFolder { FolderPath = folderPath, QuenchSlot = TemplateQuenchSlot.Objects };
        foreach (var name in scriptedFileNames)
            folder.Scripts.Add(new SqlScript { Name = name, FilePath = $"/pkg/T/{folderPath}/{name}.sql" });
        template.ScriptFolders.Add(folder);
        return template;
    }

    private static Finding[] RunOnPg(Template template)
    {
        var product = new Product
        {
            Name = "Acme",
            Platform = Platform.PostgreSQL,
            TemplateOrder = new System.Collections.Generic.List<string>()
        };
        return new CoherenceCheck().Run(new ValidationContext(product, new[] { template }, "pkg")).ToArray();
    }

    [Test]
    public void EnumTypeDeclaredAsJsonAndScripted_IsReported()
    {
        var template = PgTemplateWithFolder("Enum Types", "order_status");
        template.EnumTypes.Add(new PostgreSqlEnumType { Name = "order_status" });

        var findings = RunOnPg(template);

        Assert.Multiple(() =>
        {
            Assert.That(findings.Select(f => f.Code), Has.Member("SS-ENUM-001"));
            Assert.That(findings.Single(f => f.Code == "SS-ENUM-001").Message,
                Does.Contain("order_status").And.Contain("guarded CREATE TYPE"),
                "the message has to say what the engine actually does -- the scripted form silently "
                + "no-ops once the type exists, which is why this is worth reporting at all");
        });
    }

    [Test]
    public void SequenceDeclaredAsJsonAndScripted_IsReported()
    {
        var template = PgTemplateWithFolder("Sequences", "invoice_seq");
        template.Sequences.Add(new PostgreSqlSequence { Name = "invoice_seq" });

        var findings = RunOnPg(template);

        Assert.Multiple(() =>
        {
            Assert.That(findings.Select(f => f.Code), Has.Member("SS-SEQ-001"));
            Assert.That(findings.Single(f => f.Code == "SS-SEQ-001").Message,
                Does.Not.Contain("guarded"),
                "the engine scripts say nothing about a scripted sequence, so this message must claim "
                + "no mechanism -- inventing one would be worse than the warning it replaces");
        });
    }

    [Test]
    public void DomainTypeDeclaredAsJsonAndScripted_IsReported()
    {
        var template = PgTemplateWithFolder("Domain Types", "positive_amount");
        template.DomainTypes.Add(new PostgreSqlDomainType { Name = "positive_amount" });

        Assert.That(RunOnPg(template).Select(f => f.Code), Has.Member("SS-DOM-001"));
    }

    [Test]
    public void ObjectDeclaredOnlyOneWay_IsNotReported()
    {
        // The guard that keeps this check from punishing the normal case. Both halves matter: a package
        // that only scripts, and a package that only declares, are each perfectly valid.
        var scriptedOnly = PgTemplateWithFolder("Enum Types", "order_status");

        var declaredOnly = new Template { Name = "T" };
        declaredOnly.EnumTypes.Add(new PostgreSqlEnumType { Name = "order_status" });

        Assert.Multiple(() =>
        {
            Assert.That(RunOnPg(scriptedOnly).Select(f => f.Code), Has.No.Member("SS-ENUM-001"));
            Assert.That(RunOnPg(declaredOnly).Select(f => f.Code), Has.No.Member("SS-ENUM-001"));
        });
    }

    [Test]
    public void SameNameInADifferentFolder_IsNotReported()
    {
        // The folder is what says which KIND an object is, so a sequence script named like the enum
        // must not trip the enum rule. Without this the three checks would report each other's objects.
        var template = PgTemplateWithFolder("Sequences", "order_status");
        template.EnumTypes.Add(new PostgreSqlEnumType { Name = "order_status" });

        Assert.That(RunOnPg(template).Select(f => f.Code), Has.No.Member("SS-ENUM-001"));
    }

    // The folder is the script folder's own path under the template. Searching the script's full path for
    // "/Enum Types/" counted a directory above the package, and a same-named subfolder of another folder.
    [Test]
    public void AFolderNamedLikeAKindAboveThePackage_DoesNotMakeItsScriptsThatKind()
    {
        var template = new Template { Name = "T" };
        var folder = new TemplateFolder { FolderPath = "Functions", QuenchSlot = TemplateQuenchSlot.Objects };
        folder.Scripts.Add(new SqlScript { Name = "status", FilePath = "/work/Enum Types/pkg/T/Functions/status.sql" });
        template.ScriptFolders.Add(folder);
        template.EnumTypes.Add(new PostgreSqlEnumType { Name = "status" });

        Assert.That(RunOnPg(template).Select(f => f.Code), Has.No.Member("SS-ENUM-001"));
    }

    [Test]
    public void ASubfolderNamedLikeAKindInsideAnotherFolder_IsNotThatKind()
    {
        var template = PgTemplateWithFolder("Functions/Enum Types", "status");
        template.EnumTypes.Add(new PostgreSqlEnumType { Name = "status" });

        Assert.That(RunOnPg(template).Select(f => f.Code), Has.No.Member("SS-ENUM-001"));
    }

    // A schema-qualified file name scripts the same object as the bare one, and the finding names the schema, so
    // the same name in two schemas does not give two identical findings.
    [Test]
    public void ASchemaQualifiedScriptFile_IsMatched_AndTheFindingNamesTheSchema()
    {
        var template = PgTemplateWithFolder("Enum Types", "public.status");
        template.EnumTypes.Add(new PostgreSqlEnumType { Schema = "public", Name = "status" });

        var finding = RunOnPg(template).Single(f => f.Code == "SS-ENUM-001");

        Assert.That(finding.Message, Does.Contain("'public.status'"));
    }

    [Test]
    public void AScriptQualifiedWithAnotherSchema_IsADifferentObject()
    {
        var template = PgTemplateWithFolder("Enum Types", "sales.status");
        template.EnumTypes.Add(new PostgreSqlEnumType { Schema = "public", Name = "status" });

        Assert.That(RunOnPg(template).Select(f => f.Code), Has.No.Member("SS-ENUM-001"));
    }

    [Test]
    public void MemoryOptimizedWithFileGroup_IsError()
    {
        // #18 / SS-XTP-001: a memory-optimized table cannot also declare disk placement.
        var table = new SqlServerTable
        {
            Name = "Hot", Schema = "dbo", MemoryOptimized = true, FileGroup = "SECONDARY",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } }
        };
        var finding = RunOn(table).Single(f => f.Code == "SS-XTP-001");
        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Error));
            Assert.That(finding.Message, Does.Contain("FileGroup"));
        });
    }

    [Test]
    public void MemoryOptimizedWithNoPlacement_IsClean()
    {
        var table = new SqlServerTable
        {
            Name = "Hot", Schema = "dbo", MemoryOptimized = true,
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } }
        };
        Assert.That(RunOn(table).Any(f => f.Code == "SS-XTP-001"), Is.False);
    }

    private static Finding[] RunMyFamily(Platform platform, string relatedColumns, string parentIndexColumns, bool parentIndexUnique,
        string deleteAction = null, bool declareParent = true)
    {
        MySqlTable Make() => platform == Platform.MariaDb ? new MariaDbTable() : new MySqlTable();
        var parent = Make();
        parent.Name = "Parent";
        parent.Columns.Add(new MySqlColumn { Name = "Id", DataType = "int" });
        parent.Columns.Add(new MySqlColumn { Name = "Code", DataType = "varchar(20)" });
        parent.Indexes.Add(new MySqlIndex { Name = "PRIMARY", PrimaryKey = true, Unique = true, IndexColumns = "Id" });
        parent.Indexes.Add(new MySqlIndex { Name = "ix_code", Unique = parentIndexUnique, IndexColumns = parentIndexColumns });
        var child = Make();
        child.Name = "Child";
        child.Columns.Add(new MySqlColumn { Name = "Id", DataType = "int" });
        child.Columns.Add(new MySqlColumn { Name = "ParentCode", DataType = "varchar(20)" });
        child.ForeignKeys.Add(new MySqlForeignKey { Name = "fk_child", Columns = "ParentCode", RelatedTable = "Parent", RelatedColumns = relatedColumns, DeleteAction = deleteAction });
        var template = new Template { Name = "T" };
        if (declareParent) template.Tables.Add(parent);
        template.Tables.Add(child);
        var product = new Product { Name = "Acme", Platform = platform, TemplateOrder = new System.Collections.Generic.List<string>() };
        return new CoherenceCheck().Run(new ValidationContext(product, new[] { template }, "pkg")).ToArray();
    }

    // MY-F1: MySQL 8.4 refuses a foreign key to columns that are not a whole primary or unique key.
    [TestCase("Code", "Code", true, false, TestName = "AWholeUniqueKey_IsClean")]
    [TestCase("`Code`", "Code DESC", true, false, TestName = "AWholeUniqueKey_QuotedAndOrdered_IsClean")]
    [TestCase("Id", "Code", false, false, TestName = "ThePrimaryKey_IsClean")]
    [TestCase("Code", "Code", false, true, TestName = "ANonUniqueIndex_IsAWarning")]
    [TestCase("Code", "Code(10)", true, true, TestName = "APrefixUniqueKey_IsAWarning")]
    public void MySqlForeignKeyTarget_MustBeAWholeKey(string relatedColumns, string indexColumns, bool unique, bool expectWarning)
    {
        var findings = RunMyFamily(Platform.MySQL, relatedColumns, indexColumns, unique).Where(f => f.Code == "SS-FK-008").ToArray();
        Assert.That(findings.Select(f => f.Severity), expectWarning ? Is.EqualTo(new[] { Severity.Warning }) : Is.Empty);
    }

    [Test]
    public void ANonStandardKeyTarget_IsNotFlagged_OnMariaDb_OrWhenTheParentIsNotInThePackage()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RunMyFamily(Platform.MariaDb, "Code", "Code", false).Where(f => f.Code == "SS-FK-008"), Is.Empty);
            Assert.That(RunMyFamily(Platform.MySQL, "Code", "Code", false, declareParent: false).Where(f => f.Code == "SS-FK-008"), Is.Empty);
        });
    }

    // MY-080 / MA-062: SET DEFAULT is stored by the MySQL family and then acts as RESTRICT.
    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void SetDefaultAction_IsAWarning_OnTheMySqlFamily(Platform platform)
    {
        var findings = RunMyFamily(platform, "Id", "Code", true, deleteAction: "set  default").Where(f => f.Code == "SS-FK-009").ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(findings.Select(f => f.Severity), Is.EqualTo(new[] { Severity.Warning }));
            Assert.That(findings.Single().Message, Does.Contain("ON DELETE SET DEFAULT").And.Contain("RESTRICT"));
            Assert.That(RunMyFamily(platform, "Id", "Code", true, deleteAction: "CASCADE").Where(f => f.Code == "SS-FK-009"), Is.Empty);
        });
    }

    private static Finding[] RunMyColumn(Platform platform, string dataType, string generation = null, string check = null)
    {
        MySqlTable table = platform == Platform.MariaDb ? new MariaDbTable() : new MySqlTable();
        table.Name = "T1";
        table.Columns.Add(new MySqlColumn { Name = "c", DataType = dataType, GenerationExpression = generation });
        if (check != null) table.CheckConstraints.Add(new CheckConstraint { Name = "ck", Expression = check });
        var template = new Template { Name = "T" };
        template.Tables.Add(table);
        var product = new Product { Name = "Acme", Platform = platform, TemplateOrder = new System.Collections.Generic.List<string>() };
        return new CoherenceCheck().Run(new ValidationContext(product, new[] { template }, "pkg")).ToArray();
    }

    // MY-#2: MySQL 9.6 removed MD5(), SHA1() and SHA().
    [TestCase("varchar(32)", "md5(`c`)", null, true)]
    [TestCase("varchar(40)", null, "SHA1 (c) <> ''", true)]
    [TestCase("varchar(64)", "sha2(`c`, 256)", null, false)]
    public void ARemovedHashFunctionInAnExpression_IsAWarning_OnMySql(string type, string generation, string check, bool expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(RunMyColumn(Platform.MySQL, type, generation, check).Count(f => f.Code == "SS-COL-004"), Is.EqualTo(expected ? 1 : 0));
            Assert.That(RunMyColumn(Platform.MariaDb, type, generation, check).Count(f => f.Code == "SS-COL-004"), Is.Zero, "MariaDB keeps them");
        });
    }

    [Test]
    public void ARemovedHashFunctionInAPartitionExpression_IsAWarning_OnMySql()
    {
        var table = new MySqlTable
        {
            Name = "T1",
            Partitioning = new MySqlPartitioning { Method = "KEY", Expression = "crc32(md5(`c`))" },
            Columns = { new MySqlColumn { Name = "c", DataType = "varchar(10)" } }
        };
        Assert.That(RunOnMy(table).Where(f => f.Code == "SS-COL-004").Select(f => f.Location), Has.One.EndsWith("/ Partitioning"));
    }

    // MY-076: NVARCHAR creates a utf8mb3 column.
    [TestCase(Platform.MySQL, "NVARCHAR(10)", true)]
    [TestCase(Platform.MariaDb, "national varchar(10)", true)]
    [TestCase(Platform.MySQL, "varchar(10)", false)]
    public void ANationalCharacterType_IsAWarning(Platform platform, string type, bool expected) =>
        Assert.That(RunMyColumn(platform, type).Count(f => f.Code == "SS-COL-005"), Is.EqualTo(expected ? 1 : 0));

    // MY-063: forms MySQL 8.0.17 deprecated.
    [TestCase("float(7,2)", true)]
    [TestCase("decimal(10,2) unsigned", true)]
    [TestCase("int(5) zerofill", true)]
    [TestCase("int unsigned", false)]
    [TestCase("decimal(10,2)", false)]
    [TestCase("float", false)]
    public void ADeprecatedNumericForm_IsAWarning_OnMySql(string type, bool expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(RunMyColumn(Platform.MySQL, type).Count(f => f.Code == "SS-COL-006"), Is.EqualTo(expected ? 1 : 0));
            Assert.That(RunMyColumn(Platform.MariaDb, type).Count(f => f.Code == "SS-COL-006"), Is.Zero);
        });
    }

    [Test]
    public void RangePartitioningWithNoPartitions_IsError()
    {
        // #19 / SS-PART-003: RANGE/LIST need named partitions.
        var table = new Schema.Domain.MySQL.MySqlTable
        {
            Name = "Sales",
            Partitioning = new Schema.Domain.MySQL.MySqlPartitioning { Method = "RANGE", Expression = "year(created)" },
            Columns = { new Schema.Domain.MySQL.MySqlColumn { Name = "Id", DataType = "int" } }
        };
        Assert.That(RunOnMy(table).Single(f => f.Code == "SS-PART-003").Severity, Is.EqualTo(Severity.Error));
    }

    [Test]
    public void HashPartitionWithABoundary_IsError()
    {
        // #19 / SS-PART-004: a HASH/KEY partition has no VALUES boundary.
        var table = new Schema.Domain.MySQL.MySqlTable
        {
            Name = "Spread",
            Partitioning = new Schema.Domain.MySQL.MySqlPartitioning
            {
                Method = "HASH", Expression = "id",
                Partitions = { new Schema.Domain.MySQL.MySqlPartition { Name = "p0", Values = "100" } }
            },
            Columns = { new Schema.Domain.MySQL.MySqlColumn { Name = "Id", DataType = "int" } }
        };
        Assert.That(RunOnMy(table).Single(f => f.Code == "SS-PART-004").Severity, Is.EqualTo(Severity.Error));
    }

    // SS-PART-005: every primary and unique key holds each partitioning column whole, or the engine refuses the table
    // with 1503 (MySQL 8.0.46 and 8.4.11, MariaDB 11.8.9: the same four shapes).
    [TestCase("KEY", "", "`a`(10)", true, TestName = "KeyOverAPrefixedPrimaryKey")]
    [TestCase("KEY", "`b`", "`a`(10),`b`", false, TestName = "KeyOnAWholeKeyColumn")]
    [TestCase("KEY", "`a`", "`a`(10),`b`", true, TestName = "KeyOnAColumnTheKeyHoldsAsAPrefix")]
    [TestCase("RANGE COLUMNS", "`b`", "`a`", true, TestName = "RangeColumnsOnAColumnOutsideTheKey")]
    [TestCase("LINEAR KEY", "a, b", "a,b", false, TestName = "LinearKeyOnTheWholeKey")]
    public void PartitioningAColumnAKeyDoesNotHoldWhole_IsError(string method, string expression, string keyColumns, bool expected)
    {
        var table = new Schema.Domain.MySQL.MySqlTable
        {
            Name = "Parts",
            Partitioning = new Schema.Domain.MySQL.MySqlPartitioning
            {
                Method = method, Expression = expression, PartitionCount = method.EndsWith("KEY") ? 2 : null,
                Partitions = method.EndsWith("KEY") ? [] : [new Schema.Domain.MySQL.MySqlPartition { Name = "p0", Values = "MAXVALUE" }]
            },
            Columns =
            {
                new Schema.Domain.MySQL.MySqlColumn { Name = "a", DataType = "varchar(100)" },
                new Schema.Domain.MySQL.MySqlColumn { Name = "b", DataType = "int" }
            },
            Indexes = { new Schema.Domain.MySQL.MySqlIndex { Name = "PRIMARY", PrimaryKey = true, Unique = true, IndexColumns = keyColumns } }
        };
        var findings = RunOnMy(table).Where(f => f.Code == "SS-PART-005").ToList();
        Assert.That(findings, expected ? Has.Count.GreaterThan(0) : Is.Empty, string.Join("; ", findings.Select(f => f.Message)));
    }

    [Test]
    public void BackfillWithoutDefault_IsWarning()
    {
        var table = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Note", DataType = "int", BackfillExistingRows = true } }
        };

        var finding = RunOn(table).Single(f => f.Code == "SS-COL-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning),
                "the deploy still succeeds -- the setting simply does nothing");
            Assert.That(finding.Message, Does.Contain("Note"));
        });
    }

    [Test]
    public void BackfillWithADefault_IsClean()
    {
        var table = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Note", DataType = "int", Default = "7", BackfillExistingRows = true } }
        };

        Assert.That(RunOn(table).Any(f => f.Code == "SS-COL-001"), Is.False);
    }

    [Test]
    public void DefaultWithoutBackfill_IsClean()
    {
        // The ordinary case by far. Flagging it would make the rule noise on nearly every package.
        var table = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Note", DataType = "int", Default = "7" } }
        };

        Assert.That(RunOn(table).Any(f => f.Code == "SS-COL-001"), Is.False);
    }

    private static SqlServerTable OrderWith(RebuildPolicy policy) => new()
    {
        Name = "Order",
        Schema = "dbo",
        Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } },
        RebuildPolicy = policy
    };

    [Test]
    public void ThresholdModeWithoutAThreshold_IsError()
    {
        var finding = RunOn(OrderWith(new RebuildPolicy { Mode = "THRESHOLD" })).Single(f => f.Code == "SS-TBL-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Error),
                "unlike SS-COL-001 the policy is not merely inert -- it cannot be evaluated at all, so a "
                + "deploy would have to guess between altering and rebuilding");
            Assert.That(finding.Message, Is.Not.Null, "a finding with no message names nothing");
            Assert.That(finding.Message, Does.Contain("Threshold"));
        });
    }

    [Test]
    public void ThresholdModeWithAThresholdOfZero_IsError()
    {
        // Zero is not "no rebuilds" -- it is a threshold no change count can fail to reach, which is
        // ALWAYS spelled ambiguously. Minimum = 1 on the property says the same thing to an editor.
        var finding = RunOn(OrderWith(new RebuildPolicy { Mode = "THRESHOLD", Threshold = 0 }))
            .Single(f => f.Code == "SS-TBL-001");

        Assert.That(finding.Severity, Is.EqualTo(Severity.Error));
    }

    [Test]
    public void ThresholdModeWithAThreshold_IsClean()
    {
        Assert.That(RunOn(OrderWith(new RebuildPolicy { Mode = "THRESHOLD", Threshold = 3 }))
            .Any(f => f.Code == "SS-TBL-001"), Is.False);
    }

    // The deploy trims Mode on every engine, so a padded THRESHOLD is THRESHOLD -- the check must read it the same way.
    [TestCase(" THRESHOLD")]
    [TestCase("threshold ")]
    [TestCase("THRESHOLD\t")]
    [TestCase("\r\nthreshold")]
    public void PaddedThresholdMode_WithoutAThreshold_IsSsTbl001(string mode)
    {
        Assert.That(RunOn(OrderWith(new RebuildPolicy { Mode = mode })).Select(f => f.Code), Does.Contain("SS-TBL-001"));
    }

    [TestCase(" THRESHOLD")]
    [TestCase("threshold ")]
    [TestCase("THRESHOLD\t")]
    [TestCase("\r\nthreshold")]
    public void PaddedThresholdMode_WithAThreshold_IsClean(string mode)
    {
        Assert.That(RunOn(OrderWith(new RebuildPolicy { Mode = mode, Threshold = 50 }))
            .Where(f => f.Code is "SS-TBL-001" or "SS-TBL-002").Select(f => f.Message), Is.Empty);
    }

    // The table's policy reaches the deploy re-serialised from the model, and the deploy's SQL trims spaces only, so a
    // tab left on Mode read as no known mode there -- while the schema pattern accepted it. Trimmed at the model.
    [Test]
    public void AWhitespacePaddedMode_ReachesTheDeployTrimmed()
    {
        var policy = Newtonsoft.Json.JsonConvert.DeserializeObject<RebuildPolicy>("{\"Mode\": \"THRESHOLD\t\", \"Threshold\": 5}")!;
        Assert.That(Newtonsoft.Json.Linq.JObject.FromObject(policy)["Mode"]!.ToString(), Is.EqualTo("THRESHOLD"));
    }

    [Test]
    public void AlwaysAndNeverWithoutAThreshold_AreClean()
    {
        // Threshold is ignored outside THRESHOLD mode, so its absence is the ordinary shape -- flagging
        // it would make the rule noise on every table that sets a policy at all.
        Assert.Multiple(() =>
        {
            Assert.That(RunOn(OrderWith(new RebuildPolicy { Mode = "ALWAYS" })).Any(f => f.Code == "SS-TBL-001"),
                Is.False);
            Assert.That(RunOn(OrderWith(new RebuildPolicy { Mode = "NEVER" })).Any(f => f.Code == "SS-TBL-001"),
                Is.False);
            Assert.That(RunOn(OrderWith(null)).Any(f => f.Code == "SS-TBL-001"), Is.False);
        });
    }


    // ---- the same rule at the PRODUCT and TEMPLATE tiers ----
    //
    // A policy is resolved as a WHOLE object from the nearest level that declares one
    // (ProductQuench.ResolveCascadedPolicy), and ModifiedTableQuench rebuilds only when Mode = THRESHOLD AND a
    // Threshold is present. So a threshold-less THRESHOLD at the product or template tier REPLACES an inherited
    // ALWAYS and then never fires itself -- yet only the table tier was checked.

    private static Finding[] RunWithPolicies(RebuildPolicy productPolicy, RebuildPolicy templatePolicy)
    {
        var template = new Template { Name = "Main", RebuildPolicy = templatePolicy };
        var product = new Product
        {
            Name = "Acme", Platform = Platform.SqlServer, RebuildPolicy = productPolicy,
            TemplateOrder = new System.Collections.Generic.List<string>()
        };
        return new CoherenceCheck().Run(new ValidationContext(product, new[] { template }, "pkg")).ToArray();
    }

    [Test]
    public void ThresholdModeWithoutAThreshold_AtTheProductTier_IsError()
    {
        var finding = RunWithPolicies(new RebuildPolicy { Mode = "THRESHOLD" }, null)
            .Single(f => f.Code == "SS-TBL-001");
        Assert.That(finding.Severity, Is.EqualTo(Severity.Error));
        Assert.That(finding.Location, Does.Contain("Product 'Acme'"), "the finding must say WHICH tier");
    }

    [Test]
    public void ThresholdModeWithoutAThreshold_AtTheTemplateTier_IsError()
    {
        var finding = RunWithPolicies(null, new RebuildPolicy { Mode = "THRESHOLD" })
            .Single(f => f.Code == "SS-TBL-001");
        Assert.That(finding.Location, Does.Contain("Template 'Main'"));
    }

    [Test]
    public void ValidPoliciesAtTheUpperTiers_AreClean() =>
        Assert.That(RunWithPolicies(new RebuildPolicy { Mode = "ALWAYS" },
                new RebuildPolicy { Mode = "THRESHOLD", Threshold = 50 })
            .Any(f => f.Code is "SS-TBL-001" or "SS-TBL-002"), Is.False);

    // ---- a Threshold that is ignored (SS-TBL-002) ----
    //
    // Mode defaults to NEVER, so {"Threshold":50} with no Mode is NEVER -- and because the declared policy
    // replaces any inherited one whole, it silently BLOCKS rebuilds an outer level asked for. Warned rather than
    // errored: with Mode written out explicitly (ALWAYS or NEVER) the Threshold is merely inert.
    [Test]
    public void ThresholdWithoutThresholdMode_IsWarned_AtEveryTier()
    {
        var table = OrderWith(new RebuildPolicy { Threshold = 50 });
        var tableFindings = RunOn(table).Where(f => f.Code == "SS-TBL-002").ToArray();
        var upper = RunWithPolicies(new RebuildPolicy { Threshold = 50 }, new RebuildPolicy { Mode = "ALWAYS", Threshold = 5 })
            .Where(f => f.Code == "SS-TBL-002").ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(tableFindings, Has.Length.EqualTo(1), "table tier");
            Assert.That(tableFindings[0].Severity, Is.EqualTo(Severity.Warning));
            Assert.That(tableFindings[0].Message, Does.Contain("NEVER"),
                "the message must say what the Threshold actually resolved to");
            Assert.That(upper.Select(f => f.Location),
                Is.EquivalentTo(new[] { "Product 'Acme'", "Template 'Main'" }), "product and template tiers");
        });
    }

    [Test]
    public void ThresholdUnderThresholdMode_IsNotWarned() =>
        Assert.That(RunOn(OrderWith(new RebuildPolicy { Mode = "THRESHOLD", Threshold = 5 }))
            .Any(f => f.Code == "SS-TBL-002"), Is.False);

    [Test]
    public void FkLocalColumnMissing_IsError()
    {
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "CustomerId",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "Id"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, Customer()));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-FK-001"));
        Assert.That(findings[0].Category, Is.EqualTo("Coherence"));
        Assert.That(findings[0].Location, Is.EqualTo("Template 'Main' / Table 'Order' / FK 'FK_Order_Customer'"));
    }

    // Two shipped demos (course4-recipe-10, TenantCRM) reported SS-FK-002 against a table sitting in the
    // same template. SchemaDefaultResolver keeps a declared Schema verbatim -- "[dbo]" stays bracketed --
    // but FILLS an omitted RelatedTableSchema with the bare platform default, "dbo". Comparing the raw
    // strings then never matched. Every pre-existing test here wrote identifiers unbracketed on both
    // sides, which is exactly why none of them caught it.
    [Test]
    public void FkResolves_WhenSchemaIsBracketWrappedAndRelatedTableSchemaWasDefaulted()
    {
        var order = new SqlServerTable
        {
            Name = "[Order]",
            Schema = "[dbo]",
            Columns = { new SqlServerColumn { Name = "[CustomerId]", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "[FK_Order_Customer]",
                    Columns = "[CustomerId]",
                    RelatedTable = "[Customer]",
                    RelatedTableSchema = "dbo", // what SchemaDefaultResolver fills in when the JSON omits it
                    RelatedColumns = "[Id]"
                }
            }
        };
        var customer = new SqlServerTable
        {
            Name = "[Customer]",
            Schema = "[dbo]",
            Columns = { new SqlServerColumn { Name = "[Id]", DataType = "int" } }
        };
        var ctx = Context(TemplateWithTables("Main", order, customer));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty,
            "a bracket-wrapped schema must resolve against a defaulted RelatedTableSchema: "
            + string.Join("; ", findings.Select(f => f.Code + " " + f.Message)));
    }

    private static SqlServerTable OrderReferencingMissingTable() => new()
    {
        Name = "Order",
        Schema = "dbo",
        Columns = { new SqlServerColumn { Name = "Id", DataType = "int" }, new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
        ForeignKeys =
        {
            new SqlServerForeignKey
            {
                Name = "FK_Order_Customer",
                Columns = "CustomerId",
                RelatedTable = "NoSuchTable",
                RelatedTableSchema = "dbo",
                RelatedColumns = "Id"
            }
        }
    };

    private static Finding[] RunDanglingFk(bool productDropsTables, bool? templateDropsTables)
    {
        var product = Product();
        product.DropTablesRemovedFromProduct = productDropsTables;
        var template = TemplateWithTables("Main", OrderReferencingMissingTable());
        template.DropTablesRemovedFromProduct = templateDropsTables;
        return new CoherenceCheck().Run(new ValidationContext(product, new[] { template }, "pkg")).ToArray();
    }

    // The deploy creates a foreign key to a table the package does not declare, provided the table exists on the
    // target -- so an unresolved RelatedTable is a lean, not a certainty that the deploy fails.
    [Test]
    public void FkRelatedTableMissing_IsWarning()
    {
        var findings = RunDanglingFk(productDropsTables: true, templateDropsTables: null);

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Code, Is.EqualTo("SS-FK-002"));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Warning));
        Assert.That(findings[0].Category, Is.EqualTo("Coherence"));
    }

    // Never silenced, whatever the drop flags say. A partial deployment (a SchemaShears patch, a bootstrap) expects
    // references outside itself, but DropTablesRemovedFromProduct false is also a common safety setting on a complete
    // product, where silence would hide a misspelled RelatedTable until the FK failed at deploy.
    [TestCase(false, null)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void FkRelatedTableMissing_IsAWarningWhateverTheDropFlagsSay(bool productDropsTables, bool? templateDropsTables)
    {
        var findings = RunDanglingFk(productDropsTables, templateDropsTables);

        Assert.That(findings.Select(f => (f.Code, f.Severity)), Is.EqualTo(new[] { ("SS-FK-002", Severity.Warning) }));
    }

    [Test]
    public void FkRelatedTableGatedVariants_NotAmbiguous()
    {
        // Two GATED variants of the SAME (schema,name) are one logical table — not ambiguous,
        // and the FK should resolve cleanly with no findings.
        var customerA = new SqlServerTable
        {
            Name = "Customer", Schema = "dbo", ShouldApplyExpression = "{{IsPartitioned}}", VariantName = "Partitioned",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } }
        };
        var customerB = new SqlServerTable
        {
            Name = "Customer", Schema = "dbo", ShouldApplyExpression = "{{IsNotPartitioned}}", VariantName = "NotPartitioned",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } }
        };
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" }, new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "CustomerId",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "Id"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, customerA, customerB));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void FkRelatedColumnMissing_IsError()
    {
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" }, new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "CustomerId",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "NoSuchColumn"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, Customer()));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-FK-004"));
        Assert.That(findings[0].Category, Is.EqualTo("Coherence"));
    }

    [Test]
    public void FkColumnCountMismatch_IsError()
    {
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns =
            {
                new SqlServerColumn { Name = "Id", DataType = "int" },
                new SqlServerColumn { Name = "CustomerId", DataType = "int" },
                new SqlServerColumn { Name = "CustomerRegion", DataType = "int" }
            },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "CustomerId, CustomerRegion",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "Id"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, Customer()));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-FK-005"));
        Assert.That(findings[0].Category, Is.EqualTo("Coherence"));
    }

    [Test]
    public void FkLocalColumnDeclaredBare_ReferencedBacktickWrapped_NoFinding()
    {
        // Local column declared bare ("CustomerId"), FK Columns backtick-wrapped
        // ("`CustomerId`") -- same column, different spelling (MySQL-style quoting on the FK side).
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" }, new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "`CustomerId`",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "Id"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, Customer()));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void FkLocalColumnDeclaredBracketWrapped_ReferencedBare_NoFinding()
    {
        // Reverse direction, non-backtick wrapper: local column declared bracket-wrapped
        // ("[CustomerId]"), FK Columns bare ("CustomerId").
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" }, new SqlServerColumn { Name = "[CustomerId]", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "CustomerId",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "Id"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, Customer()));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void FkRelatedColumnMismatchedWrapping_NoFinding()
    {
        // Related column reference bracket-wrapped ("[Id]") against the target table's bare
        // declaration (Customer()'s "Id") -- same column across the FK boundary, different spelling.
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" }, new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "CustomerId",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "[Id]"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, Customer()));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void FkLocalColumnMissingWrapped_StillReportsExactlyOneError()
    {
        // Guard against over-correction: a genuinely missing local column, wrapped or not, must
        // still be flagged exactly once with the correct code.
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "`NoSuchColumn`",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "Id"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, Customer()));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-FK-001"));
        Assert.That(findings[0].Category, Is.EqualTo("Coherence"));
    }

    [Test]
    public void FkRelatedColumnMissingWrapped_StillReportsExactlyOneError()
    {
        // Guard against over-correction on the related-column side: a genuinely missing related
        // column, wrapped or not, must still be flagged exactly once with the correct code.
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" }, new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "CustomerId",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "[NoSuchColumn]"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, Customer()));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-FK-004"));
        Assert.That(findings[0].Category, Is.EqualTo("Coherence"));
    }

    private static SqlServerTable EdgeWithConstraint(string graphType, string fromTable = "[dbo].[Person]", string toTable = "City") => new()
    {
        Name = "Knows",
        Schema = "dbo",
        GraphType = graphType,
        Columns = { new SqlServerColumn { Name = "Since", DataType = "date" } },
        EdgeConstraints = { new EdgeConstraint { Name = "EC_Knows", Clauses = { new EdgeConstraintClause { FromTable = fromTable, ToTable = toTable } } } }
    };

    private static SqlServerTable NodeTable(string name) => new()
    {
        Name = name, Schema = "dbo", GraphType = "Node", Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } }
    };

    [Test]
    public void AnEdgeConstraint_OnAnEdgeTable_ConnectingDeclaredNodes_IsClean()
    {
        var findings = new CoherenceCheck().Run(Context(TemplateWithTables("T", NodeTable("Person"), NodeTable("City"), EdgeWithConstraint("Edge"))));
        Assert.That(findings.Where(f => f.Code.StartsWith("SS-TBL")), Is.Empty);
    }

    [Test]
    public void AnEdgeConstraint_OnANodeTable_IsError()
    {
        var findings = new CoherenceCheck().Run(Context(TemplateWithTables("T", NodeTable("Person"), NodeTable("City"), EdgeWithConstraint("Node")))).ToList();
        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-TBL-003" }));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
    }

    [Test]
    public void AnEdgeConstraint_NamingATableThatIsNotADeclaredNode_IsWarning()
    {
        var findings = new CoherenceCheck().Run(Context(TemplateWithTables("T", NodeTable("Person"), EdgeWithConstraint("Edge", toTable: "Country")))).ToList();
        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-TBL-004" }));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Warning));
        Assert.That(findings[0].Message, Does.Contain("'Country'").And.Contain("dbo.Country"));
    }

    [Test]
    public void GraphPseudoColumns_AreKeyParts_OnTheTableKindThatHasThem()
    {
        var edge = new SqlServerTable
        {
            Name = "Knows",
            Schema = "dbo",
            GraphType = "Edge",
            Columns = { new SqlServerColumn { Name = "Since", DataType = "date" } },
            Indexes = { new SqlServerIndex { Name = "IX_Knows_From", IndexColumns = "$from_id,$to_id" } }
        };
        Assert.That(new CoherenceCheck().Run(Context(TemplateWithTables("T", edge))).Where(f => f.Code == "SS-IDX-001"), Is.Empty);

        edge.GraphType = "Node";
        Assert.That(new CoherenceCheck().Run(Context(TemplateWithTables("T", edge))).Count(f => f.Code == "SS-IDX-001"), Is.EqualTo(2),
            "a node table has $node_id, not $from_id or $to_id");
    }

    [Test]
    public void IndexColumnMissing_IsError()
    {
        var table = new SqlServerTable
        {
            Name = "Customer",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } },
            Indexes = { new SqlServerIndex { Name = "IX_Customer_Name", IndexColumns = "Name DESC" } }
        };
        var ctx = Context(TemplateWithTables("Main", table));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-IDX-001"));
        Assert.That(findings[0].Category, Is.EqualTo("Coherence"));
    }

    [Test]
    public void IndexColumnExpressionKeyPart_IsNotFlagged()
    {
        // Canonical extracted form for a MySQL functional/expression index — a whole key part
        // wrapped in one paren pair. Not a column reference; must be skipped, not flagged.
        var table = new SqlServerTable
        {
            Name = "Customer",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } },
            Indexes = { new SqlServerIndex { Name = "IX_Customer_Label", IndexColumns = "(lower(`Label`))" } }
        };
        var ctx = Context(TemplateWithTables("Main", table));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    // The target was split at the first dot even inside delimiters, so `order.detail` resolved as table `detail` in a
    // schema `order and was reported missing.
    [Test]
    public void AForeignKeyToADelimitedNameContainingADot_Resolves()
    {
        var table = new MySqlTable
        {
            Name = "`order.detail`",
            Columns = { new MySqlColumn { Name = "id", DataType = "int" }, new MySqlColumn { Name = "parent_id", DataType = "int", Nullable = true } },
            Indexes = { new MySqlIndex { Name = "PRIMARY", PrimaryKey = true, Unique = true, IndexColumns = "`id`" } },
            ForeignKeys = { new MySqlForeignKey { Name = "fk_parent", Columns = "parent_id", RelatedTable = "`order.detail`", RelatedColumns = "id" } }
        };

        Assert.That(RunOnMy(table).Where(f => f.Code.StartsWith("SS-FK")).Select(f => f.Message), Is.Empty);
    }

    // SchemaTongs writes a MySQL prefix index as `Email`(20); the length is not part of the column's name, and an
    // extracted package failed its own validation on every prefix index.
    [TestCase("`Email`(20)")]
    [TestCase("Email(20)")]
    [TestCase("`Email` (20) DESC")]
    public void MySqlPrefixKeyPart_NamesItsColumn(string keyPart)
    {
        var table = new MySqlTable
        {
            Name = "customer",
            Columns = { new MySqlColumn { Name = "Email", DataType = "varchar(100)" } },
            Indexes = { new MySqlIndex { Name = "ix_email", IndexColumns = keyPart } }
        };

        Assert.That(RunOnMy(table).Where(f => f.Code == "SS-IDX-001").Select(f => f.Message), Is.Empty);
    }

    [Test]
    public void MySqlPrefixKeyPart_OnAMissingColumn_IsStillFlagged()
    {
        var table = new MySqlTable
        {
            Name = "customer",
            Columns = { new MySqlColumn { Name = "Email", DataType = "varchar(100)" } },
            Indexes = { new MySqlIndex { Name = "ix_mail", IndexColumns = "`Mail`(20)" } }
        };

        Assert.That(RunOnMy(table).Count(f => f.Code == "SS-IDX-001"), Is.EqualTo(1));
    }

    // Only MySQL and MariaDB have prefix key parts; on SQL Server `Email(20)` deploys as an error, so it is not a column.
    [Test]
    public void APrefixKeyPartOnSqlServer_IsFlagged()
    {
        var table = new SqlServerTable
        {
            Name = "Customer",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Email", DataType = "varchar(100)" } },
            Indexes = { new SqlServerIndex { Name = "IX_Email", IndexColumns = "Email(20)" } }
        };

        Assert.That(RunOn(table).Count(f => f.Code == "SS-IDX-001"), Is.EqualTo(1));
    }

    [Test]
    public void IndexColumnPlainColumnPlusExpressionKeyPart_OnlyPlainColumnChecked()
    {
        // Mixed key-part list: the plain column must still be checked against the table (and
        // passes here, since `Id` is present), while the expression key part is skipped.
        var table = new SqlServerTable
        {
            Name = "Customer",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } },
            Indexes = { new SqlServerIndex { Name = "IX_Customer_IdLabel", IndexColumns = "`Id`,(lower(`Label`))" } }
        };
        var ctx = Context(TemplateWithTables("Main", table));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void IndexColumnExpressionKeyPartContainingComma_IsNotShatteredOrFlagged()
    {
        // A naive comma split would shatter this into two fragments and report two spurious
        // errors — the paren-depth-aware split must keep it as one key part and then skip it.
        var table = new SqlServerTable
        {
            Name = "Customer",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } },
            Indexes = { new SqlServerIndex { Name = "IX_Customer_Concat", IndexColumns = "(concat(`Label`,`Label`))" } }
        };
        var ctx = Context(TemplateWithTables("Main", table));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void IndexOnlyTemplate_IndexColumnsNotDeclaredOnTheTable_IsNotAnError()
    {
        // IndexOnlyTableQuenches exists to index a table the package does NOT own -- a vendor product
        // or a replicated copy, created outside the package, whose columns are deliberately never
        // declared. So index key parts naming columns absent from the table file are the feature
        // working, not a defect. Reporting them made `--Validate` exit 2 on a correct package, which
        // fails the user's CI gate: caught by the release sweep on the shipped lab that teaches this
        // (Demos/Learn/course4-recipe-13), failing on all four engines.
        var table = new SqlServerTable
        {
            Name = "vendor_order",
            Schema = "dbo",
            Indexes = { new SqlServerIndex { Name = "IX_vendor_order_status", IndexColumns = "status" } }
        };
        var template = TemplateWithTables("Main", table);
        template.IndexOnlyTableQuenches = true;

        var findings = new CoherenceCheck().Run(Context(template)).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void IndexOnlyTemplate_ForeignKeyLocalColumnNotDeclared_IsNotAnError_ButTheRelatedSideStillIs()
    {
        // The two halves are not symmetric and must not be suppressed together: the LOCAL column list
        // is unowned under this flag, while the RELATED table is a different table the package usually
        // does declare in full. Over-correcting would blind the half that still has an answer.
        var related = Customer();
        var table = new SqlServerTable
        {
            Name = "vendor_order",
            Schema = "dbo",
            ForeignKeys =
            {
                new ForeignKey
                {
                    Name = "FK_vendor_order_Customer",
                    Columns = "customer_ref",
                    RelatedTable = "Customer",
                    RelatedColumns = "NoSuchColumn"
                }
            }
        };
        var template = TemplateWithTables("Main", table, related);
        template.IndexOnlyTableQuenches = true;

        var findings = new CoherenceCheck().Run(Context(template)).ToList();

        Assert.That(findings.Select(f => f.Code), Has.None.EqualTo("SS-FK-001"),
            "the local column is owned outside the package under IndexOnlyTableQuenches");
        Assert.That(findings.Select(f => f.Code), Has.One.EqualTo("SS-FK-004"),
            "the related table is still declared here, so its column list is still authoritative");
    }

    [Test]
    public void IndexColumnMissingPlainColumn_StillReportsExactlyOneError()
    {
        // Guard against over-correction: a genuinely bogus plain-column reference (no parens)
        // must still be flagged, exactly once.
        var table = new SqlServerTable
        {
            Name = "Customer",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } },
            Indexes = { new SqlServerIndex { Name = "IX_Customer_NoSuch", IndexColumns = "`NoSuchColumn`" } }
        };
        var ctx = Context(TemplateWithTables("Main", table));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-IDX-001"));
        Assert.That(findings[0].Category, Is.EqualTo("Coherence"));
    }

    [Test]
    public void IndexColumnDeclaredBare_ReferencedBracketWrapped_NoFinding()
    {
        // Declared bare ("Id"), referenced SQL Server-style bracket-wrapped ("[Id]") — same
        // column, different spelling. Also proves quoting normalization isn't MySQL-only.
        var table = new SqlServerTable
        {
            Name = "Customer",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } },
            Indexes = { new SqlServerIndex { Name = "IX_Customer_Id", IndexColumns = "[Id]" } }
        };
        var ctx = Context(TemplateWithTables("Main", table));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void IndexColumnDeclaredBracketWrapped_ReferencedBare_NoFinding()
    {
        // Reverse direction: declared bracket-wrapped ("[Id]"), referenced bare ("Id").
        var table = new SqlServerTable
        {
            Name = "Customer",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "[Id]", DataType = "int" } },
            Indexes = { new SqlServerIndex { Name = "IX_Customer_Id", IndexColumns = "Id" } }
        };
        var ctx = Context(TemplateWithTables("Main", table));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void ValidFkAcrossTemplates_NoFindings()
    {
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" }, new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "CustomerId",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "Id"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order), TemplateWithTables("Reference", Customer()));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void ReferencedVariantColumn_Exists_NoFinding()
    {
        // The related column exists only as a gated variant on the related table — still counts.
        var customer = new SqlServerTable
        {
            Name = "Customer",
            Schema = "dbo",
            Columns =
            {
                new SqlServerColumn { Name = "Id", DataType = "int", ShouldApplyExpression = "{{UseGuidKeys}}", VariantName = "GuidKey" },
                new SqlServerColumn { Name = "Id", DataType = "bigint", ShouldApplyExpression = "{{UseIntKeys}}", VariantName = "IntKey" }
            }
        };
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" }, new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "CustomerId",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "Id"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, customer));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void CleanPackage_NoFindings()
    {
        // A real, populated package — a table with a valid FK (local + related columns present,
        // cardinality matching) AND a valid index — proving every remaining check is satisfied
        // together, not just vacuously on an FK/index-free table.
        var customer = new SqlServerTable
        {
            Name = "Customer",
            Schema = "dbo",
            Columns =
            {
                new SqlServerColumn { Name = "Id", DataType = "int" },
                new SqlServerColumn { Name = "Name", DataType = "nvarchar" }
            },
            Indexes = { new SqlServerIndex { Name = "IX_Customer_Name", IndexColumns = "Name" } }
        };
        var order = new SqlServerTable
        {
            Name = "Order",
            Schema = "dbo",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" }, new SqlServerColumn { Name = "CustomerId", DataType = "int" } },
            ForeignKeys =
            {
                new SqlServerForeignKey
                {
                    Name = "FK_Order_Customer",
                    Columns = "CustomerId",
                    RelatedTable = "Customer",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "Id"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order, customer));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    // Row-level security and Policies are two halves of one feature, and each half alone fails
    // silently in an opposite direction. These are Warnings rather than Errors because both
    // configurations are legal and deployable -- someone may genuinely manage policies outside the
    // package -- but neither is likely to be what the author meant.

    private static PostgreSqlTable PgTable(bool rls, params string[] policyNames)
    {
        var table = new PostgreSqlTable
        {
            Name = "invoice",
            Schema = "public",
            RowLevelSecurity = rls,
            Columns = { new PostgreSqlColumn { Name = "id", DataType = "integer" } }
        };
        foreach (var name in policyNames)
            table.Policies.Add(new PostgreSqlPolicy { Name = name, UsingExpression = "true" });
        return table;
    }

    private static System.Collections.Generic.List<Finding> RunPg(PostgreSqlTable table)
    {
        var template = new Template { Name = "Main" };
        template.Tables.Add(table);
        var product = new Product
        {
            Name = "Acme",
            Platform = Platform.PostgreSQL,
            TemplateOrder = new System.Collections.Generic.List<string>()
        };
        return new CoherenceCheck().Run(new ValidationContext(product, [template], "pkg")).ToList();
    }

    [Test]
    public void RowLevelSecurityWithNoPolicies_IsReported()
    {
        var finding = RunPg(PgTable(rls: true)).Single(f => f.Code == "SS-RLS-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(finding.Message, Does.Contain("no rows"),
                "the message has to say what actually happens -- a reader who does not already know "
                + "that PostgreSQL denies everything here will read 'no policies' as harmless. "
                + finding.Message);
        });
    }

    [Test]
    public void PoliciesWithoutRowLevelSecurity_IsReported()
    {
        // The opposite failure, and the more dangerous one: the policies are created, so the package
        // LOOKS secured, but PostgreSQL does not enforce any of them until RLS is enabled.
        var finding = RunPg(PgTable(rls: false, "tenant_read")).Single(f => f.Code == "SS-RLS-002");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(finding.Message, Does.Contain("RowLevelSecurity"),
                "and name the property that would enforce them. " + finding.Message);
        });
    }

    [Test]
    public void RowLevelSecurityWithPolicies_IsClean()
    {
        Assert.That(RunPg(PgTable(rls: true, "tenant_read")).Any(f => f.Code.StartsWith("SS-RLS-")),
            Is.False);
    }

    [Test]
    public void ATableWithNeither_IsClean()
    {
        // The negative half: a check that fired on absence would warn about every ordinary table in
        // every PostgreSQL package.
        Assert.That(RunPg(PgTable(rls: false)).Any(f => f.Code.StartsWith("SS-RLS-")), Is.False);
    }

    // REPLICA IDENTITY coherence (#407). The deploy raises on the unhonourable cases, but --Validate is
    // where an author should learn about a typo -- and the unknown/non-unique index cases could only ever
    // surface at deploy time as PostgreSQL complaining about generated DDL.

    private static PostgreSqlTable RiTable(string mode, string indexName, bool indexUnique = true, bool declareIndex = true)
    {
        var table = new PostgreSqlTable
        {
            Name = "invoice",
            Schema = "public",
            ReplicaIdentity = mode,
            ReplicaIdentityIndex = indexName,
            Columns = { new PostgreSqlColumn { Name = "id", DataType = "integer" } }
        };
        if (declareIndex)
            table.Indexes.Add(new PostgreSqlIndex { Name = "uq_invoice", IndexColumns = "id", Unique = indexUnique });
        return table;
    }

    [Test]
    public void ReplicaIdentityIndexMode_WithNoIndexNamed_IsAnError()
    {
        var finding = RunPg(RiTable("INDEX", null)).Single(f => f.Code == "SS-RI-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Error), "this cannot be deployed at all");
            Assert.That(finding.Message, Does.Contain("ReplicaIdentityIndex"), finding.Message);
        });
    }

    [Test]
    public void ReplicaIdentityNamingAnUndeclaredIndex_IsAnError()
    {
        var finding = RunPg(RiTable("INDEX", "uq_typo")).Single(f => f.Code == "SS-RI-002");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Error));
            Assert.That(finding.Message, Does.Contain("uq_typo"),
                "naming the offending index is the whole point -- a reader cannot act on a message that "
                + "only says the package is wrong. " + finding.Message);
        });
    }

    [Test]
    public void ReplicaIdentityNamingANonUniqueIndex_IsAnError()
    {
        var finding = RunPg(RiTable("INDEX", "uq_invoice", indexUnique: false)).Single(f => f.Code == "SS-RI-003");

        Assert.That(finding.Severity, Is.EqualTo(Severity.Error));
    }

    [Test]
    public void ReplicaIdentityIndexNamedWithoutIndexMode_IsAWarning()
    {
        // Legal and deployable -- PostgreSQL just ignores the name -- but almost certainly not intended.
        var finding = RunPg(RiTable("FULL", "uq_invoice")).Single(f => f.Code == "SS-RI-004");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(finding.Message, Does.Contain("ignored"), finding.Message);
        });
    }

    [Test]
    public void AValidReplicaIdentityDeclaration_IsSilent()
    {
        // The negative half: a rule that fires on correct packages is worse than no rule.
        Assert.That(RunPg(RiTable("INDEX", "uq_invoice")).Where(f => f.Code.StartsWith("SS-RI-")), Is.Empty);
    }

    [Test]
    public void ATableWithNoReplicaIdentity_IsSilent()
    {
        Assert.That(RunPg(RiTable(null, null)).Where(f => f.Code.StartsWith("SS-RI-")), Is.Empty);
    }

    // MariaDB per-column WITHOUT SYSTEM VERSIONING (#408). Verified on 11.4: MariaDB ACCEPTS the clause
    // on a non-versioned table and silently discards it, so nothing at deploy time can tell the author
    // the declaration is inert. --Validate is the only place this can surface.

    private static System.Collections.Generic.List<Finding> RunMaria(bool tableVersioned, bool columnExcluded)
    {
        var table = new MariaDbTable
        {
            Name = "invoice",
            IsSystemVersioned = tableVersioned,
            Columns = { new MariaDbColumn { Name = "payload", DataType = "int(11)", WithoutSystemVersioning = columnExcluded } }
        };
        var template = new Template { Name = "Main" };
        template.Tables.Add(table);
        var product = new Product { Name = "Acme", Platform = Platform.MariaDb, TemplateOrder = new System.Collections.Generic.List<string>() };
        return new CoherenceCheck().Run(new ValidationContext(product, [template], "pkg")).ToList();
    }

    [Test]
    public void VersioningExclusionOnANonVersionedTable_IsReported()
    {
        var finding = RunMaria(tableVersioned: false, columnExcluded: true).Single(f => f.Code == "SS-SV-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(finding.Message, Does.Contain("payload"), "name the column " + finding.Message);
            Assert.That(finding.Message, Does.Contain("silently discards"),
                "the message has to say WHY nothing complained -- a reader who does not know MariaDB "
                + "swallows the clause will assume it worked. " + finding.Message);
        });
    }

    [Test]
    public void VersioningExclusionOnAVersionedTable_IsSilent()
    {
        Assert.That(RunMaria(tableVersioned: true, columnExcluded: true).Where(f => f.Code == "SS-SV-001"), Is.Empty,
            "the correct declaration must not be flagged");
    }

    [Test]
    public void ANonVersionedTableWithNoExclusion_IsSilent()
    {
        Assert.That(RunMaria(tableVersioned: false, columnExcluded: false).Where(f => f.Code == "SS-SV-001"), Is.Empty);
    }

    // Compression options that cannot be combined. Both engines REFUSE these and neither error names the
    // option: MySQL 8.0 gives 1031 "Table storage engine ... doesn't have this option", MariaDB 11.4
    // gives errno 140 "Wrong create options". Verified live on both.

    [Test]
    public void MySqlCompressionWithCompressedRowFormat_IsAnError()
    {
        var table = new MySqlTable { Name = "invoice", RowFormat = "COMPRESSED", Compression = "zlib" };
        table.Columns.Add(new MySqlColumn { Name = "id", DataType = "INT" });
        var finding = RunFor(table, Platform.MySQL).Single(f => f.Code == "SS-CO-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Error), "the deploy cannot succeed");
            Assert.That(finding.Message, Does.Contain("1031"),
                "quoting the engine error number is what lets someone match this to what they saw. "
                + finding.Message);
        });
    }

    [Test]
    public void MariaDbPageCompressedWithCompressedRowFormat_IsAnError()
    {
        var table = new MariaDbTable { Name = "invoice", RowFormat = "COMPRESSED", PageCompressed = true };
        table.Columns.Add(new MariaDbColumn { Name = "id", DataType = "INT" });
        var finding = RunFor(table, Platform.MariaDb).Single(f => f.Code == "SS-CO-001");

        Assert.That(finding.Severity, Is.EqualTo(Severity.Error));
    }

    [Test]
    public void PageCompressionLevelWithoutPageCompressed_IsAWarning()
    {
        var table = new MariaDbTable { Name = "invoice", PageCompressed = false, PageCompressionLevel = 6 };
        table.Columns.Add(new MariaDbColumn { Name = "id", DataType = "INT" });
        var finding = RunFor(table, Platform.MariaDb).Single(f => f.Code == "SS-CO-002");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning), "legal, just inert");
            Assert.That(finding.Message, Does.Contain("ignored"), finding.Message);
        });
    }

    [Test]
    public void CompressionOnAnUncompressedRowFormat_IsSilent()
    {
        // The negative half -- the combination that IS valid must not be flagged.
        var table = new MySqlTable { Name = "invoice", RowFormat = "DYNAMIC", Compression = "zlib" };
        table.Columns.Add(new MySqlColumn { Name = "id", DataType = "INT" });

        Assert.That(RunFor(table, Platform.MySQL).Where(f => f.Code.StartsWith("SS-CO-")), Is.Empty);
    }

    // ---- partition placement (#partitioning, K1) ------------------------------

    [Test]
    public void PartitionSchemeWithoutAColumn_IsAnError()
    {
        // The quench refuses this too, but only against a live target -- and it would otherwise reach the
        // engine as ON <scheme> with no column, whose syntax error names neither the table nor the
        // property. Catching it at authoring time is the point of --Validate.
        var table = new SqlServerTable { Name = "invoice", PartitionScheme = "[psOrders]" };
        table.Columns.Add(new SqlServerColumn { Name = "id", DataType = "INT" });

        var finding = RunFor(table, Platform.SqlServer).Single(f => f.Code == "SS-PART-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Error));
            Assert.That(finding.Message, Does.Contain("PartitionColumn"),
                "the message must name the missing half, or the user cannot tell which to add: "
                + finding.Message);
        });
    }

    [Test]
    public void PartitionColumnWithoutAScheme_IsAnError()
    {
        var table = new SqlServerTable { Name = "invoice", PartitionColumn = "[id]" };
        table.Columns.Add(new SqlServerColumn { Name = "id", DataType = "INT" });

        var finding = RunFor(table, Platform.SqlServer).Single(f => f.Code == "SS-PART-001");

        Assert.That(finding.Message, Does.Contain("PartitionScheme"), finding.Message);
    }

    [Test]
    public void PartitionSchemeAndFileGroupTogether_IsAnError()
    {
        var table = new SqlServerTable
        {
            Name = "invoice", FileGroup = "[Archive]", PartitionScheme = "[psOrders]", PartitionColumn = "[id]"
        };
        table.Columns.Add(new SqlServerColumn { Name = "id", DataType = "INT" });

        var finding = RunFor(table, Platform.SqlServer).Single(f => f.Code == "SS-PART-002");

        Assert.That(finding.Severity, Is.EqualTo(Severity.Error), "a table lives on one data space");
    }

    [Test]
    public void AnIndexDeclaringHalfAPartitionPlacement_IsAnError()
    {
        // An index carries its own placement independently of its table's, so it has to be checked in its
        // own right -- a table-only check would miss this entirely.
        var table = new SqlServerTable { Name = "invoice" };
        table.Columns.Add(new SqlServerColumn { Name = "id", DataType = "INT" });
        table.Indexes.Add(new SqlServerIndex { Name = "ix_invoice_id", IndexColumns = "[id]", PartitionScheme = "[psOrders]" });

        var finding = RunFor(table, Platform.SqlServer).Single(f => f.Code == "SS-PART-001");

        Assert.That(finding.Message, Does.Contain("ix_invoice_id"), finding.Message);
    }

    [Test]
    public void AFullyDeclaredPartitionPlacement_IsSilent()
    {
        // The negative half -- a correct declaration must not be flagged.
        var table = new SqlServerTable { Name = "invoice", PartitionScheme = "[psOrders]", PartitionColumn = "[id]" };
        table.Columns.Add(new SqlServerColumn { Name = "id", DataType = "INT" });
        table.Indexes.Add(new SqlServerIndex
        {
            Name = "ix_invoice_id", IndexColumns = "[id]", PartitionScheme = "[psOrders]", PartitionColumn = "[id]"
        });

        Assert.That(RunFor(table, Platform.SqlServer).Where(f => f.Code.StartsWith("SS-PART-")), Is.Empty);
    }

    [Test]
    public void ATableDeclaringNoPartitioningAtAll_IsSilent()
    {
        var table = new SqlServerTable { Name = "invoice", FileGroup = "[Archive]" };
        table.Columns.Add(new SqlServerColumn { Name = "id", DataType = "INT" });

        Assert.That(RunFor(table, Platform.SqlServer).Where(f => f.Code.StartsWith("SS-PART-")), Is.Empty);
    }

    // #417: CdcFilegroup only places a change table, so without EnableCDC it does nothing -- and nothing at deploy
    // says so.
    [Test]
    public void CdcFilegroupWithoutEnableCdc_IsAnInertWarning()
    {
        var table = new SqlServerTable { Schema = "dbo", Name = "Orders", CdcFilegroup = "cdc_fg" };
        table.Columns.Add(new SqlServerColumn { Name = "Id", DataType = "INT" });

        var finding = RunFor(table, Platform.SqlServer).Single(f => f.Code == "SS-CDC-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(finding.Message, Does.Contain("Orders").And.Contain("cdc_fg").And.Contain("EnableCDC"));
        });
    }

    [TestCase(true, "cdc_fg")]
    [TestCase(false, null)]
    [TestCase(true, null)]
    public void CdcFilegroup_IsSilent_WhenItHasSomethingToPlace(bool enableCdc, string filegroup)
    {
        var table = new SqlServerTable { Schema = "dbo", Name = "Orders", EnableCDC = enableCdc, CdcFilegroup = filegroup };
        table.Columns.Add(new SqlServerColumn { Name = "Id", DataType = "INT" });

        Assert.That(RunFor(table, Platform.SqlServer).Where(f => f.Code == "SS-CDC-001"), Is.Empty);
    }

    // #426: the same for CdcSupportsNetChanges -- it shapes a capture instance, so without EnableCDC there is none.
    // Either value is inert: false is not "off", it is a setting with nothing to apply to.
    [TestCase(true)]
    [TestCase(false)]
    public void CdcSupportsNetChangesWithoutEnableCdc_IsAnInertWarning(bool netChanges)
    {
        var table = new SqlServerTable { Schema = "dbo", Name = "Orders", CdcSupportsNetChanges = netChanges };
        table.Columns.Add(new SqlServerColumn { Name = "Id", DataType = "INT" });

        var finding = RunFor(table, Platform.SqlServer).Single(f => f.Code == "SS-CDC-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(finding.Message, Does.Contain("Orders").And.Contain("CdcSupportsNetChanges").And.Contain("EnableCDC"));
        });
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, null)]
    public void CdcSupportsNetChanges_IsSilent_WhenThereIsACaptureInstanceToShape(bool enableCdc, bool? netChanges)
    {
        var table = new SqlServerTable { Schema = "dbo", Name = "Orders", EnableCDC = enableCdc, CdcSupportsNetChanges = netChanges };
        table.Columns.Add(new SqlServerColumn { Name = "Id", DataType = "INT" });

        Assert.That(RunFor(table, Platform.SqlServer).Where(f => f.Code == "SS-CDC-001"), Is.Empty);
    }

    // CdcIndexName: the same inert warning without EnableCDC, and with it an error for anything SQL Server's
    // @index_name refuses, which the deploy refuses by name too.
    [Test]
    public void CdcIndexNameWithoutEnableCdc_IsAnInertWarning()
    {
        var table = CdcIndexTable(enableCdc: null, cdcIndexName: "[UX_Code]");

        var finding = RunFor(table, Platform.SqlServer).Single(f => f.Code == "SS-CDC-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(finding.Message, Does.Contain("Orders").And.Contain("CdcIndexName").And.Contain("EnableCDC"));
        });
    }

    [TestCase("[UX_Missing]", "which is not one of its declared indexes")]
    [TestCase("[IX_Plain]", "which is not unique")]
    [TestCase("[UX_Note]", "which has a nullable key column")]
    public void CdcIndexName_ThatSqlServerWouldRefuse_IsAnError(string cdcIndexName, string problem)
    {
        var table = CdcIndexTable(enableCdc: true, cdcIndexName: cdcIndexName);

        var finding = RunFor(table, Platform.SqlServer).Single(f => f.Code == "SS-CDC-002");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Error));
            Assert.That(finding.Message, Does.Contain("Orders").And.Contain(cdcIndexName).And.Contain(problem));
        });
    }

    [TestCase("[UX_Code]")]
    [TestCase("UX_Code")]
    [TestCase("[ux_code]")]
    [TestCase("[PK_Orders]")]
    public void CdcIndexName_NamingADeclaredUniqueIndexOverNotNullColumns_IsSilent(string cdcIndexName)
    {
        var table = CdcIndexTable(enableCdc: true, cdcIndexName: cdcIndexName);

        Assert.That(RunFor(table, Platform.SqlServer).Where(f => f.Code.StartsWith("SS-CDC-")), Is.Empty);
    }

    [TestCase("[UX_Missing]", "which is not one of its declared indexes")]
    [TestCase("[IX_Plain]", "which is not unique")]
    [TestCase("[UX_Note]", "which has more than one key column")]
    [TestCase("[UX_NoteOnly]", "which has a nullable key column")]
    [TestCase("[UX_Filtered]", "which has a filter")]
    public void FullTextKeyIndex_ThatSqlServerWouldRefuse_IsAnError(string keyIndex, string problem)
    {
        var table = CdcIndexTable(enableCdc: null, cdcIndexName: null);
        table.Indexes.Add(new SqlServerIndex { Name = "[UX_NoteOnly]", Unique = true, IndexColumns = "[Note]" });
        table.Indexes.Add(new SqlServerIndex { Name = "[UX_Filtered]", Unique = true, IndexColumns = "[Code]", FilterExpression = "[Code] > 0" });
        table.FullTextIndex.Add(new Schema.Domain.SqlServer.FullTextIndex { FullTextCatalog = "[cat]", KeyIndex = keyIndex, Columns = "[Code]" });

        var finding = RunFor(table, Platform.SqlServer).Single(f => f.Code == "SS-FT-001");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Error));
            Assert.That(finding.Message, Does.Contain("Orders").And.Contain(keyIndex).And.Contain(problem));
        });
    }

    [TestCase("[UX_Code]")]
    [TestCase("ux_code")]
    [TestCase("[PK_Orders]")]
    public void FullTextKeyIndex_NamingADeclaredUniqueSingleNotNullColumnIndex_IsSilent(string keyIndex)
    {
        var table = CdcIndexTable(enableCdc: null, cdcIndexName: null);
        table.FullTextIndex.Add(new Schema.Domain.SqlServer.FullTextIndex { FullTextCatalog = "[cat]", KeyIndex = keyIndex, Columns = "[Code]" });

        Assert.That(RunFor(table, Platform.SqlServer).Where(f => f.Code == "SS-FT-001"), Is.Empty);
    }

    private static SqlServerTable CdcIndexTable(bool? enableCdc, string cdcIndexName)
    {
        var table = new SqlServerTable { Schema = "dbo", Name = "Orders", EnableCDC = enableCdc, CdcIndexName = cdcIndexName };
        table.Columns.Add(new SqlServerColumn { Name = "[Id]", DataType = "INT" });
        table.Columns.Add(new SqlServerColumn { Name = "[Code]", DataType = "INT" });
        table.Columns.Add(new SqlServerColumn { Name = "[Note]", DataType = "INT", Nullable = true });
        table.Indexes.Add(new SqlServerIndex { Name = "[PK_Orders]", PrimaryKey = true, Unique = true, IndexColumns = "[Id]" });
        table.Indexes.Add(new SqlServerIndex { Name = "[UX_Code]", Unique = true, IndexColumns = "[Code] DESC" });
        table.Indexes.Add(new SqlServerIndex { Name = "[IX_Plain]", IndexColumns = "[Code]" });
        table.Indexes.Add(new SqlServerIndex { Name = "[UX_Note]", Unique = true, IndexColumns = "[Code], [Note]" });
        return table;
    }

    private static System.Collections.Generic.List<Finding> RunFor(Table table, Platform platform)
    {
        var template = new Template { Name = "Main" };
        template.Tables.Add(table);
        var product = new Product { Name = "Acme", Platform = platform, TemplateOrder = new System.Collections.Generic.List<string>() };
        return new CoherenceCheck().Run(new ValidationContext(product, [template], "pkg")).ToList();
    }

    // ---- SS-COL-002: a character set other than the table's, with no collation, gets that set's default ----

    private static MySqlTable CharsetTable(string columnCharset, string columnCollation = null,
                                           string tableCollation = "utf8mb4_unicode_ci", bool maria = false)
    {
        var table = maria ? new MariaDbTable { Name = "invoice", Collation = tableCollation }
                          : new MySqlTable { Name = "invoice", Collation = tableCollation };
        var column = maria ? new MariaDbColumn() : new MySqlColumn();
        column.Name = "code";
        column.DataType = "varchar(20)";
        column.CharacterSet = columnCharset;
        column.Collation = columnCollation;
        table.Columns.Add(column);
        return table;
    }

    [TestCase(false, Platform.MySQL)]
    [TestCase(true, Platform.MariaDb)]
    public void AColumnCharsetOtherThanTheTables_WithNoCollation_IsAWarning(bool maria, Platform platform)
    {
        var finding = RunFor(CharsetTable("latin1", maria: maria), platform).Single(f => f.Code == "SS-COL-002");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(finding.Message, Does.Contain("'code'").And.Contain("latin1"),
                "the author needs the column and the character set to act on it. " + finding.Message);
        });
    }

    [TestCase("utf8mb4", null, "utf8mb4_unicode_ci")]
    [TestCase("UTF8MB4", null, "utf8mb4_unicode_ci")]
    [TestCase("latin1", "latin1_bin", "utf8mb4_unicode_ci")]
    [TestCase("latin1", null, null)]
    [TestCase(null, null, "utf8mb4_unicode_ci")]
    public void ACharsetTheDeployResolves_IsSilent(string charset, string collation, string tableCollation)
    {
        Assert.That(RunFor(CharsetTable(charset, collation, tableCollation), Platform.MySQL)
            .Where(f => f.Code == "SS-COL-002"), Is.Empty);
    }

    // utf8 is utf8mb3 under its older name, and the deploy writes both in the server's own form before comparing.
    [TestCase("utf8", "utf8mb3_general_ci")]
    [TestCase("utf8mb3", "utf8_general_ci")]
    public void AColumnInTheTablesCharsetUnderItsOtherName_IsSilent(string charset, string tableCollation)
    {
        Assert.That(RunFor(CharsetTable(charset, null, tableCollation), Platform.MySQL)
            .Where(f => f.Code == "SS-COL-002"), Is.Empty);
    }

    // MariaDB 11.4's short collation names (uca1400_ai_ci) name no character set: they take the table's.
    [Test]
    public void AShortTableCollation_BelongsToTheTablesCharset()
    {
        var sameSet = CharsetTable("utf8mb4", null, "uca1400_ai_ci", maria: true);
        sameSet.CharacterSet = "utf8mb4";
        var otherSet = CharsetTable("latin1", null, "uca1400_ai_ci", maria: true);
        otherSet.CharacterSet = "utf8mb4";
        var tableSetUnknown = CharsetTable("latin1", null, "uca1400_ai_ci", maria: true);

        Assert.Multiple(() =>
        {
            Assert.That(RunFor(sameSet, Platform.MariaDb).Where(f => f.Code == "SS-COL-002"), Is.Empty, "the table's own set");
            Assert.That(RunFor(otherSet, Platform.MariaDb).Count(f => f.Code == "SS-COL-002"), Is.EqualTo(1), "another set");
            Assert.That(RunFor(tableSetUnknown, Platform.MariaDb).Where(f => f.Code == "SS-COL-002"), Is.Empty,
                "with no table character set the database default decides, which validation cannot see");
        });
    }

    // ---- SS-IDENT-002: a PostgreSQL name longer than 63 bytes is stored truncated ----

    [Test]
    public void APostgreSqlColumnNameOver63Bytes_IsAnError()
    {
        var name = new string('c', 64);
        var finding = RunFor(QTable(columnName: name), Platform.PostgreSQL).Single(f => f.Code == "SS-IDENT-002");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Error), "the second deploy fails");
            Assert.That(finding.Message, Does.Contain(name).And.Contain("64 bytes"), finding.Message);
        });
    }

    // The limit is bytes, not characters: 31 two-byte characters (62 bytes) fit, 32 (64 bytes) do not.
    [TestCase(31, false)]
    [TestCase(32, true)]
    public void ThePostgreSqlLimitIsInBytes(int characters, bool reported)
    {
        var findings = RunFor(QTable(tableName: new string('\u00e9', characters)), Platform.PostgreSQL)
            .Where(f => f.Code == "SS-IDENT-002");
        Assert.That(findings, reported ? Is.Not.Empty : Is.Empty);
    }

    [Test]
    public void ALongSequenceName_IsAnError()
    {
        var template = new Template { Name = "Main" };
        template.Sequences.Add(new PostgreSqlSequence { Name = new string('s', 70) });
        var product = new Product { Name = "Acme", Platform = Platform.PostgreSQL, TemplateOrder = [] };
        var findings = new CoherenceCheck().Run(new ValidationContext(product, [template], "pkg")).ToList();
        Assert.That(findings.Where(f => f.Code == "SS-IDENT-002"), Is.Not.Empty);
    }

    [Test]
    public void ALongNameOnAnotherEngine_IsNotThisCheck()
    {
        var table = new SqlServerTable { Name = new string('t', 100) };
        table.Columns.Add(new SqlServerColumn { Name = "id", DataType = "INT" });
        Assert.That(RunFor(table, Platform.SqlServer).Where(f => f.Code == "SS-IDENT-002"), Is.Empty);
    }

    // ---- SS-IDENT-002's POPULATION: one case per PostgreSQL name kind that reaches DDL ----
    //
    // A case per kind, deliberately, because the population is a hand-written helper and a kind dropped
    // from it fails silently -- the check still passes, on a smaller set. Each name here is 64 bytes, one
    // over PostgreSQL's limit, which truncates it.

    private static PostgreSqlTable QTable(string tableName = "invoice", string columnName = "id",
                                          string tableOldName = null, string columnOldName = null) =>
        new()
        {
            Name = tableName,
            Schema = "public",
            OldName = tableOldName,
            Columns = { new PostgreSqlColumn { Name = columnName, DataType = "integer", OldName = columnOldName } }
        };

    private const string Overlong = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static System.Collections.Generic.IEnumerable<TestCaseData> OverlongTableNameKinds()
    {
        PostgreSqlTable Base() => new()
        {
            Name = "invoice",
            Schema = "public",
            Columns = { new PostgreSqlColumn { Name = "id", DataType = "integer" } }
        };

        PostgreSqlTable With(System.Action<PostgreSqlTable> mutate)
        {
            var t = Base();
            mutate(t);
            return t;
        }

        yield return new TestCaseData(With(t => t.Name = Overlong), "Table").SetName("table name");
        yield return new TestCaseData(With(t => t.Schema = Overlong), "Schema").SetName("schema name");
        yield return new TestCaseData(With(t => t.OldName = Overlong), "Table OldName").SetName("table OldName");
        yield return new TestCaseData(With(t => t.Columns[0].Name = Overlong), "Column").SetName("column name");
        yield return new TestCaseData(With(t => t.Columns[0].OldName = Overlong), "Column OldName").SetName("column OldName");
        yield return new TestCaseData(
            With(t => t.Indexes.Add(new PostgreSqlIndex { Name = Overlong, IndexColumns = "id" })),
            "Index").SetName("index name");
        yield return new TestCaseData(
            With(t => t.ForeignKeys.Add(new PostgreSqlForeignKey
                { Name = Overlong, Columns = "id", RelatedTable = "customer", RelatedColumns = "id" })),
            "Foreign key").SetName("foreign key name");

        yield return new TestCaseData(
            With(t => t.ForeignKeys.Add(new PostgreSqlForeignKey
            {
                Name = "fk_1", Columns = "id", RelatedTable = Overlong, RelatedColumns = "id"
            })),
            "Foreign key RelatedTable").SetName("FK RelatedTable");
        yield return new TestCaseData(
            With(t => t.ForeignKeys.Add(new PostgreSqlForeignKey
            {
                Name = "fk_1", Columns = "id", RelatedTable = "customer", RelatedColumns = "id",
                RelatedTableSchema = Overlong
            })),
            "Foreign key RelatedTableSchema").SetName("FK RelatedTableSchema");
        yield return new TestCaseData(
            With(t => t.CheckConstraints.Add(new PostgreSqlCheckConstraint { Name = Overlong, Expression = "id > 0" })),
            "Check constraint").SetName("check constraint name");
        yield return new TestCaseData(
            With(t => t.ExcludeConstraints.Add(new ExcludeConstraint { Name = Overlong })),
            "Exclude constraint").SetName("exclude constraint name");
        yield return new TestCaseData(
            With(t => t.Statistics.Add(new Schema.Domain.PostgreSQL.Statistic { Name = Overlong, StatisticsColumns = "id" })),
            "Statistics").SetName("statistics name");

        // ReplicaIdentityIndex is a POINTER at an index, and the realistic shape is an overlong pointer
        // while the index it names is short -- which is also the only shape that isolates this kind from
        // the Index kind above.
        yield return new TestCaseData(
            With(t =>
            {
                t.Indexes.Add(new PostgreSqlIndex { Name = "uq_invoice", IndexColumns = "id", Unique = true });
                t.ReplicaIdentity = "INDEX";
                t.ReplicaIdentityIndex = Overlong;
            }),
            "ReplicaIdentityIndex").SetName("ReplicaIdentityIndex pointer");
    }

    [TestCaseSource(nameof(OverlongTableNameKinds))]
    public void EveryTableTierNameKind_Overlong_IsRefused(PostgreSqlTable table, string expectedKind)
    {
        var findings = RunPg(table).Where(f => f.Code == "SS-IDENT-002").ToList();

        Assert.That(findings.Select(f => f.Message).ToList(),
            Has.Some.StartsWith($"{expectedKind} '"),
            $"no SS-IDENT-002 names the '{expectedKind}' kind — the population helper has dropped it");
        Assert.That(findings.All(f => f.Severity == Severity.Error), Is.True, "a truncated name is not a warning");
    }

    private static System.Collections.Generic.IEnumerable<TestCaseData> OverlongModeledObjectKinds()
    {
        Template With(System.Action<Template> mutate)
        {
            var t = new Template { Name = "Main" };
            mutate(t);
            return t;
        }

        yield return new TestCaseData(
            With(t => t.MaterializedViews.Add(new PostgreSqlMaterializedView
                { Name = Overlong, Schema = "public", Definition = "SELECT 1" })),
            "Materialized view").SetName("materialized view name");
        yield return new TestCaseData(
            With(t => t.MaterializedViews.Add(new PostgreSqlMaterializedView
                { Name = "mv_1", Schema = Overlong, Definition = "SELECT 1" })),
            "Materialized view schema").SetName("materialized view schema");
        yield return new TestCaseData(
            With(t => t.MaterializedViews.Add(new PostgreSqlMaterializedView
            {
                Name = "mv_1", Schema = "public", Definition = "SELECT 1",
                Indexes = { new PostgreSqlIndex { Name = Overlong, IndexColumns = "id" } }
            })),
            "Materialized view index").SetName("materialized view index name");
        yield return new TestCaseData(
            With(t => t.EnumTypes.Add(new PostgreSqlEnumType { Name = Overlong, Schema = "public" })),
            "Enum type").SetName("enum type name");
        yield return new TestCaseData(
            With(t => t.EnumTypes.Add(new PostgreSqlEnumType { Name = "status", Schema = Overlong })),
            "Enum type schema").SetName("enum type schema");
        yield return new TestCaseData(
            With(t => t.Sequences.Add(new PostgreSqlSequence { Name = Overlong, Schema = "public" })),
            "Sequence").SetName("sequence name");
        yield return new TestCaseData(
            With(t => t.Sequences.Add(new PostgreSqlSequence { Name = "sq_1", Schema = Overlong })),
            "Sequence schema").SetName("sequence schema");
        yield return new TestCaseData(
            With(t => t.DomainTypes.Add(new PostgreSqlDomainType
                { Name = Overlong, Schema = "public", DataType = "text" })),
            "Domain type").SetName("domain type name");
        yield return new TestCaseData(
            With(t => t.DomainTypes.Add(new PostgreSqlDomainType
                { Name = "email", Schema = Overlong, DataType = "text" })),
            "Domain type schema").SetName("domain type schema");
        yield return new TestCaseData(
            With(t => t.DomainTypes.Add(new PostgreSqlDomainType
            {
                Name = "email", Schema = "public", DataType = "text",
                CheckConstraints = { new PostgreSqlDomainConstraint { Name = Overlong, Expression = "VALUE <> ''" } }
            })),
            "Domain type check constraint").SetName("domain type check constraint name");
    }

    [TestCaseSource(nameof(OverlongModeledObjectKinds))]
    public void EveryModeledObjectNameKind_Overlong_IsRefused(Template template, string expectedKind)
    {
        var product = new Product
        {
            Name = "Acme",
            Platform = Platform.PostgreSQL,
            TemplateOrder = new System.Collections.Generic.List<string>()
        };
        var findings = new CoherenceCheck().Run(new ValidationContext(product, [template], "pkg"))
            .Where(f => f.Code == "SS-IDENT-002").ToList();

        Assert.That(findings.Select(f => f.Message).ToList(),
            Has.Some.StartsWith($"{expectedKind} '"),
            $"no SS-IDENT-002 names the '{expectedKind}' kind — the population helper has dropped it");
        Assert.That(findings.All(f => f.Severity == Severity.Error), Is.True, "a truncated name is not a warning");
    }

    [Test]
    public void AShortNamedModeledObjectPackage_IsNotRefused()
    {
        // The control for the widened half. Every shipped PostgreSQL demo carries ordinary lowercase names
        // for these objects, so a check that fired on them would redden --Validate across the catalogue.
        var template = new Template { Name = "Main" };
        template.MaterializedViews.Add(new PostgreSqlMaterializedView
        {
            Name = "mv_sales", Schema = "public", Definition = "SELECT 1",
            Indexes = { new PostgreSqlIndex { Name = "ix_mv_sales", IndexColumns = "id" } }
        });
        template.EnumTypes.Add(new PostgreSqlEnumType { Name = "status", Schema = "public" });
        template.Sequences.Add(new PostgreSqlSequence { Name = "sq_invoice", Schema = "public" });
        template.DomainTypes.Add(new PostgreSqlDomainType
        {
            Name = "email", Schema = "public", DataType = "text",
            CheckConstraints = { new PostgreSqlDomainConstraint { Name = "ck_email", Expression = "VALUE <> ''" } }
        });

        var product = new Product
        {
            Name = "Acme",
            Platform = Platform.PostgreSQL,
            TemplateOrder = new System.Collections.Generic.List<string>()
        };

        Assert.That(new CoherenceCheck().Run(new ValidationContext(product, [template], "pkg"))
            .Where(f => f.Code == "SS-IDENT-002"), Is.Empty);
    }

    // ---- A foreign-key name reused across tables (SS-FK-006) ----
    //
    // MySQL names foreign keys per database and SQL Server per schema, so a second table reusing a name fails the
    // deploy. MariaDB names them per table from 12.1, so there it fails only on an older server -- a warning, unless
    // MinimumVersion already rules older servers out. PostgreSQL names them per table on every version.

    private static ForeignKey ParentFk(string name) =>
        new() { Name = name, Columns = "ParentId", RelatedTable = "Parent", RelatedColumns = "Id" };

    private static Finding[] RunFkReuse(Platform platform, string minimumVersion, params (string Schema, string Table, string Fk)[] tables)
    {
        var template = new Template { Name = "T" };
        foreach (var (schema, name, fk) in tables)
        {
            Table table = platform switch
            {
                Platform.SqlServer => new SqlServerTable { Name = name, Schema = schema },
                Platform.PostgreSQL => new PostgreSqlTable { Name = name, Schema = schema },
                Platform.MariaDb => new MariaDbTable { Name = name },
                _ => new MySqlTable { Name = name }
            };
            table.ForeignKeys.Add(ParentFk(fk));
            template.Tables.Add(table);
        }
        var product = new Product { Name = "Acme", Platform = platform, MinimumVersion = minimumVersion, TemplateOrder = new System.Collections.Generic.List<string>() };
        return new CoherenceCheck().Run(new ValidationContext(product, new[] { template }, "pkg"))
            .Where(f => f.Code == "SS-FK-006").ToArray();
    }

    [Test]
    public void FkNameReusedAcrossTables_OnMySql_IsAnErrorNamingBothTables()
    {
        var findings = RunFkReuse(Platform.MySQL, null!, ("", "Orders", "fk_parent"), ("", "Invoices", "`FK_Parent`"));

        Assert.That(findings, Has.Length.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
            Assert.That(findings[0].Message, Does.Contain("Orders").And.Contain("Invoices").And.Contain("fk_parent"));
        });
    }

    [TestCase(null, Severity.Warning)]
    [TestCase("10.6", Severity.Warning)]
    [TestCase("12.0", Severity.Warning)]
    public void FkNameReusedAcrossTables_OnMariaDbThatMayBeBelow121_IsAWarning(string minimumVersion, Severity expected)
    {
        var findings = RunFkReuse(Platform.MariaDb, minimumVersion, ("", "Orders", "fk_parent"), ("", "Invoices", "fk_parent"));

        Assert.That(findings.Select(f => f.Severity), Is.EqualTo(new[] { expected }));
        Assert.That(findings[0].Message, Does.Contain("12.1"));
    }

    [TestCase("12.1")]
    [TestCase("13.0")]
    public void FkNameReusedAcrossTables_OnMariaDb121AndLater_IsNotReported(string minimumVersion) =>
        Assert.That(RunFkReuse(Platform.MariaDb, minimumVersion, ("", "Orders", "fk_parent"), ("", "Invoices", "fk_parent")), Is.Empty);

    [Test]
    public void FkNameReusedAcrossTables_OnSqlServer_IsAnErrorInTheSameSchemaOnly()
    {
        Assert.That(RunFkReuse(Platform.SqlServer, null!, ("dbo", "Orders", "FK_Parent"), ("[dbo]", "Invoices", "[FK_Parent]"))
            .Select(f => f.Severity), Is.EqualTo(new[] { Severity.Error }));
        Assert.That(RunFkReuse(Platform.SqlServer, null!, ("dbo", "Orders", "FK_Parent"), ("sales", "Invoices", "FK_Parent")), Is.Empty,
            "constraint names are per schema on SQL Server");
    }

    [Test]
    public void FkNameReusedAcrossTables_OnPostgreSql_IsNotReported() =>
        Assert.That(RunFkReuse(Platform.PostgreSQL, null!, ("public", "Orders", "fk_parent"), ("public", "Invoices", "fk_parent")), Is.Empty);

    [Test]
    public void DistinctFkNames_AreNotReported() =>
        Assert.That(RunFkReuse(Platform.MySQL, null!, ("", "Orders", "fk_orders_parent"), ("", "Invoices", "fk_invoices_parent")), Is.Empty);
}