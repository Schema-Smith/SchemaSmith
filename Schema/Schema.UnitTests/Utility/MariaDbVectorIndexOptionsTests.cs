// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

/// <summary>A MariaDB VECTOR index's M and DISTANCE are read from SHOW CREATE TABLE, the only place 11.7-13.0 report them.</summary>
public class MariaDbVectorIndexOptionsTests
{
    // Copied from MariaDB 11.8.9's SHOW CREATE TABLE: options it was declared with, after a COMMENT.
    private const string Declared = """
        CREATE TABLE `t` (
          `id` int(11) NOT NULL,
          `v` vector(3) NOT NULL,
          PRIMARY KEY (`id`),
          VECTOR KEY `vx` (`v`) COMMENT 'M=99 (not an option)' `M`='8' `DISTANCE`='cosine'
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_uca1400_ai_ci
        """;

    // An index declared without options: MariaDB writes none, and option-like text in its comment is not one.
    private const string Defaults = """
        CREATE TABLE `t2` (
          `id` int(11) NOT NULL,
          `v` vector(3) NOT NULL,
          PRIMARY KEY (`id`),
          VECTOR KEY `v` (`v`) COMMENT 'tuned: M=99 DISTANCE=cosine'
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_uca1400_ai_ci
        """;

    [Test]
    public void ReadsTheOptionsAVectorIndexWasDeclaredWith_NotTextInItsComment()
    {
        var options = MariaDbVectorIndexOptions.Read(Declared);
        Assert.That(options, Is.EquivalentTo(new Dictionary<string, (int? M, string Distance)> { ["vx"] = (8, "cosine") }));
    }

    [Test]
    public void AnIndexDeclaredWithoutOptions_ReadsAsNone_WhateverItsCommentSays()
    {
        Assert.That(MariaDbVectorIndexOptions.Read(Defaults)["v"], Is.EqualTo(((int?)null, (string)null)));
    }

    [Test]
    public void ApplySetsTheOptionsOnTheExtractedIndex_ByItsBacktickedName()
    {
        var vector = new MariaDbIndex { Name = "`vx`", IndexType = "VECTOR", IndexColumns = "`v`" };
        var other = new MariaDbIndex { Name = "`other`", IndexColumns = "`id`" };
        MariaDbVectorIndexOptions.Apply(new List<Index> { vector, other }, Declared);
        Assert.Multiple(() =>
        {
            Assert.That(vector.M, Is.EqualTo(8));
            Assert.That(vector.Distance, Is.EqualTo("cosine"));
            Assert.That(other.M, Is.Null);
        });
    }
    // #561: a changed M or DISTANCE on a deployed VECTOR index is a rebuild. Undeclared means the server's default, on
    // both sides: an option the live index was not written with is the default, and so is one the JSON leaves out.
    [TestCase(8, "cosine", false)]
    [TestCase(12, "cosine", true)]
    [TestCase(8, "euclidean", true)]
    [TestCase(8, "COSINE", false)]
    [TestCase(null, "cosine", true)]
    [TestCase(8, null, true)]
    public void AVectorIndexWhoseOptionsChanged_NeedsARebuild(int? m, string distance, bool expected)
    {
        var declared = new MariaDbIndex { Name = "`vx`", IndexType = "VECTOR", IndexColumns = "`v`", M = m, Distance = distance };
        Assert.That(MariaDbVectorIndexOptions.NeedsRebuild(declared, Declared, 6, "euclidean"), Is.EqualTo(expected));
    }

    [TestCase(null, null, false)]
    [TestCase(6, "euclidean", false)]
    [TestCase(16, null, true)]
    [TestCase(null, "cosine", true)]
    public void AnIndexLiveWithTheServersDefaults_ComparesItsDeclaredOptionsToThem(int? m, string distance, bool expected)
    {
        var declared = new MariaDbIndex { Name = "`v`", IndexType = "VECTOR", IndexColumns = "`v`", M = m, Distance = distance };
        Assert.That(MariaDbVectorIndexOptions.NeedsRebuild(declared, Defaults, 6, "euclidean"), Is.EqualTo(expected));
    }

    [Test]
    public void AnIndexThatIsNotLiveOrNotAVectorIndex_IsNotARebuild()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MariaDbVectorIndexOptions.NeedsRebuild(new MariaDbIndex { Name = "`missing`", IndexType = "VECTOR", M = 12 }, Declared, 6, "euclidean"), Is.False);
            Assert.That(MariaDbVectorIndexOptions.NeedsRebuild(new MariaDbIndex { Name = "`vx`", IndexType = "BTREE", M = 12 }, Declared, 6, "euclidean"), Is.False);
        });
    }
}
