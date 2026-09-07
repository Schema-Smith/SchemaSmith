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

    [Test]
    public void FkRelatedTableMissing_IsError()
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
                    RelatedTable = "NoSuchTable",
                    RelatedTableSchema = "dbo",
                    RelatedColumns = "Id"
                }
            }
        };
        var ctx = Context(TemplateWithTables("Main", order));

        var findings = new CoherenceCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-FK-002"));
        Assert.That(findings[0].Category, Is.EqualTo("Coherence"));
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

    private static System.Collections.Generic.List<Finding> RunFor(Table table, Platform platform)
    {
        var template = new Template { Name = "Main" };
        template.Tables.Add(table);
        var product = new Product { Name = "Acme", Platform = platform, TemplateOrder = new System.Collections.Generic.List<string>() };
        return new CoherenceCheck().Run(new ValidationContext(product, [template], "pkg")).ToList();
    }



}
