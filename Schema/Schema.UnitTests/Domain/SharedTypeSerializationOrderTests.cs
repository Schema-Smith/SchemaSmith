// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Serialization;
using Schema.Domain;
using Index = Schema.Domain.Index;

namespace Schema.UnitTests.Domain
{
    /// <summary>
    /// Pins the resolved serialization order — and therefore the property inventory — of the domain types
    /// that every engine shares. Two hazards, one guard.
    /// <para><b>1. Duplicate <c>JsonProperty(Order)</c> values are load-bearing on an undocumented
    /// tie-break.</b> <c>Table.OldName</c> and <c>DropIndexesRemovedFromProduct</c> both declare 90;
    /// <c>Template.IdentificationDatabase</c> and <c>DropTablesRemovedFromProduct</c> both declare 16, and
    /// a comment there shows that one is deliberate — reusing a number places a property without
    /// renumbering its neighbours. Newtonsoft breaks those ties on <i>declaration</i> order, which is
    /// stable but written down nowhere, so reordering two declarations silently changes what extraction
    /// writes. Rather than renumber (which would rewrite key order in every extracted package for no
    /// user-visible gain), this test makes the resolved order the thing that is asserted. The duplicates
    /// stay; what changes is that their consequence can no longer move unnoticed.</para>
    /// <para><b>2. A property added to a SHARED type reaches every engine.</b> The two schema-isolation
    /// fixtures catch the engine-specific direction, but structurally cannot catch this one — a property
    /// moved up to the shared type is no longer <c>DeclaredOnly</c>. An inventory is what catches it:
    /// adding a property here fails this test, which forces the question "should this be engine-scoped, or
    /// carry a <c>Platforms</c> attribute?" to be answered deliberately rather than discovered later in a
    /// regenerated <c>.json-schema</c> diff.</para>
    /// <para><b>When this test fails, it is asking a question, not reporting a bug.</b> Update the expected
    /// list once you have decided the property genuinely belongs on every engine.</para>
    /// </summary>
    [TestFixture]
    public class SharedTypeSerializationOrderTests
    {
        /// <summary>The order Newtonsoft will actually serialize in — Order first, declaration order to break ties.</summary>
        private static List<string> ResolvedOrder<T>() =>
            ((JsonObjectContract)new DefaultContractResolver().ResolveContract(typeof(T)))
            .Properties.Select(p => p.PropertyName).ToList();

        private static void AssertOrder<T>(params string[] expected)
        {
            var actual = ResolvedOrder<T>();
            Assert.That(actual, Is.EqualTo(expected).AsCollection,
                $"The serialized shape of the shared type {typeof(T).Name} changed.\n"
                + "If you ADDED a property: it now reaches every engine's .json-schema. Decide whether that "
                + "is right, or scope it with a Platforms attribute / move it to the engine-specific type.\n"
                + "If the ORDER moved: two properties share a JsonProperty(Order) value and Newtonsoft broke "
                + "the tie on declaration order. That is load-bearing here.\n"
                + "Actual: " + string.Join(", ", actual));
        }

        // "DataDeliveries" leads because it carries no Order at all -- Newtonsoft sorts unordered
        // properties ahead of ordered ones. "Extensions" trails for the same reason from DynamicBase.
        // Neither is an accident to fix; both are pinned so they cannot drift unnoticed.
        //
        // "$schema" sits second here but FIRST in the emitted JSON: DataDeliveries is [JsonIgnore]d,
        // so it is in this reflected list and in no file. $schema leads every package file it is
        // stamped into, which is the convention editors expect.
        [Test]
        public void Table_SerializedShape_IsPinned() => AssertOrder<Table>(
            "DataDeliveries", "$schema", "Name", "Columns", "Indexes", "ForeignKeys", "CheckConstraints",
            "ShouldApplyExpression", "DataDelivery", "VariantName", "DropColumnsRemovedFromProduct",
            "DropForeignKeysRemovedFromProduct", "DropCheckConstraintsRemovedFromProduct",
            "DropExcludeConstraintsRemovedFromProduct", "DropStatisticsRemovedFromProduct",
            "DropIndexesRemovedFromProduct", "OldName", "PreventDrop", "RebuildPolicy", "Extensions");

        [Test]
        public void Column_SerializedShape_IsPinned() => AssertOrder<Column>(
            "Name", "DataType", "Nullable", "Default", "ShouldApplyExpression", "OldName", "VariantName",
            "Extensions");

        [Test]
        public void Index_SerializedShape_IsPinned() => AssertOrder<Index>(
            "Name", "PrimaryKey", "Unique", "UniqueConstraint", "IndexColumns", "ShouldApplyExpression",
            "VariantName", "Extensions");

        [Test]
        public void TheKnownDuplicateOrders_StillResolveTheWayExtractionExpects()
        {
            // Stated as its own assertion so the reason the lists above are what they are does not have to
            // be inferred. Both pairs share a number; both resolve on declaration order.
            var table = ResolvedOrder<Table>();
            Assert.Multiple(() =>
            {
                Assert.That(table.IndexOf("DropIndexesRemovedFromProduct"), Is.LessThan(table.IndexOf("OldName")),
                    "Table.DropIndexesRemovedFromProduct and Table.OldName both declare Order = 90");
                var template = ResolvedOrder<Template>();
                Assert.That(template.IndexOf("IdentificationDatabase"), Is.LessThan(template.IndexOf("DropTablesRemovedFromProduct")),
                    "Template.IdentificationDatabase and Template.DropTablesRemovedFromProduct both declare Order = 16");
            });
        }
    }
}
