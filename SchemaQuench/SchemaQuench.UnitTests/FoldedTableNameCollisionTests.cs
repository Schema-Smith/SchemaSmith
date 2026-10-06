// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.Domain.MySQL;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class FoldedTableNameCollisionTests
{
    private static Template TemplateWith(params (string Name, string Gate)[] tables)
    {
        var template = new Template { Name = "Main" };
        foreach (var (name, gate) in tables)
            template.Tables.Add(new MySqlTable { Name = name, ShouldApplyExpression = gate });
        return template;
    }

    [Test]
    public void TwoUngatedTablesDifferingOnlyInCase_Collide()
    {
        var collisions = FoldedTableNameCollisions.Find(TemplateWith(("Orders", null), ("orders", null), ("Customers", null)));

        Assert.That(collisions, Is.EqualTo(new[] { "Orders, orders" }));
    }

    [Test]
    public void TablesWithDistinctNames_DoNotCollide()
    {
        Assert.That(FoldedTableNameCollisions.Find(TemplateWith(("Orders", null), ("Customers", null))), Is.Empty);
    }

    [Test]
    public void AFullyGatedVariantSet_IsNotACollision()
    {
        // Variants of one table are gated so that one applies per database; they are not two tables.
        var collisions = FoldedTableNameCollisions.Find(TemplateWith(("Orders", "1 = 1"), ("orders", "1 = 0")));

        Assert.That(collisions, Is.Empty);
    }
}
