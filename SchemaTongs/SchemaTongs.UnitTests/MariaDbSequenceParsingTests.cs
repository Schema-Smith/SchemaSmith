// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain.MariaDb;
using Schema.Domain.MySQL;

namespace SchemaTongs.UnitTests;

// SHOW CREATE SEQUENCE is the source of a MariaDB sequence's declaration; the file keeps only what differs from the
// engine's defaults, so a plain sequence reads as a name and nothing else.
public class MariaDbSequenceParsingTests
{
    [Test]
    public void EveryOption_IsRead_AndTheEngineDefaultsAreLeftOut()
    {
        var s = SchemaTongs.ParseMariaDbSequence("s1", "CREATE SEQUENCE `s1` start with 10 minvalue 1 maxvalue 1000 increment by 2 cache 50 cycle ENGINE=InnoDB");
        Assert.Multiple(() =>
        {
            Assert.That(s.Name, Is.EqualTo("s1"));
            Assert.That(s.Start, Is.EqualTo(10), "start differs from the lower bound, so it is kept");
            Assert.That(s.MinValue, Is.Null, "minvalue 1 is the engine's default for an ascending sequence");
            Assert.That(s.MaxValue, Is.EqualTo(1000));
            Assert.That(s.Increment, Is.EqualTo(2));
            Assert.That(s.Cache, Is.EqualTo(50));
            Assert.That(s.Cycle, Is.True);
            Assert.That(s.DataType, Is.EqualTo("bigint"), "no AS clause below 11.5");
        });
    }

    [Test]
    public void APlainSequence_KeepsNothingButItsName()
    {
        var s = SchemaTongs.ParseMariaDbSequence("s2", "CREATE SEQUENCE `s2` start with 1 minvalue 1 maxvalue 9223372036854775806 increment by 1 cache 1000 nocycle ENGINE=InnoDB");
        Assert.Multiple(() =>
        {
            Assert.That(s.Start, Is.Null);
            Assert.That(s.MinValue, Is.Null);
            Assert.That(s.MaxValue, Is.Null);
            Assert.That(s.Increment, Is.EqualTo(1));
            Assert.That(s.Cache, Is.EqualTo(1000));
            Assert.That(s.Cycle, Is.False);
        });
    }

    [Test]
    public void ADescendingSequence_HasMirroredDefaults_AndATypeIsRead()
    {
        var s = SchemaTongs.ParseMariaDbSequence("s3", "CREATE SEQUENCE `s3` as int unsigned start with -1 minvalue -9223372036854775807 maxvalue -1 increment by -1 cache 1000 nocycle ENGINE=InnoDB");
        Assert.Multiple(() =>
        {
            Assert.That(s.DataType, Is.EqualTo("int unsigned"));
            Assert.That(s.Increment, Is.EqualTo(-1));
            Assert.That(s.Start, Is.Null, "a descending sequence starts at its upper bound by default");
            Assert.That(s.MinValue, Is.Null);
            Assert.That(s.MaxValue, Is.Null);
        });
    }

    [Test]
    public void ANextvalDefault_LosesItsDatabaseQualifier_AndOnlyThat()
    {
        var table = new MariaDbTable { Name = "t" };
        table.Columns.Add(new MariaDbColumn { Name = "id", Default = "nextval(`shop`.`order_seq`)" });
        table.Columns.Add(new MariaDbColumn { Name = "other", Default = "nextval(`elsewhere`.`s`)" });
        table.Columns.Add(new MariaDbColumn { Name = "v", Default = "'shop'" });
        SchemaTongs.StripSequenceDatabase(table, "shop");
        Assert.Multiple(() =>
        {
            Assert.That(table.Columns[0].Default, Is.EqualTo("nextval(`order_seq`)"));
            Assert.That(table.Columns[1].Default, Is.EqualTo("nextval(`elsewhere`.`s`)"), "a sequence in another database stays qualified");
            Assert.That(table.Columns[2].Default, Is.EqualTo("'shop'"));
        });
    }

    // The engine writes a cache of 0 as "nocache", which the number pattern does not match, so it was read as the default
    // 1000 and deploying the extracted package turned the cache back on.
    [Test]
    public void Nocache_IsACacheOfZero()
    {
        var s = SchemaTongs.ParseMariaDbSequence("s4", "CREATE SEQUENCE `s4` start with 1 minvalue 1 maxvalue 9223372036854775806 increment by 1 nocache nocycle ENGINE=InnoDB");
        Assert.That(s.Cache, Is.Zero);
    }
}
