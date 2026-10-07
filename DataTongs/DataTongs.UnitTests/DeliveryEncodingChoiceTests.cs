// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;

namespace DataTongs.UnitTests;

// The Json extraction needs FOR JSON (2016) and STRING_AGG (2017), so on an older SQL Server source it failed
// with a syntax error. With no DeliveryEncoding set, such a source now extracts as Xml; an explicit DeliveryEncoding wins.
// Source:CompatEncoding plays no part: it shapes how scripts are built, never what the package contains.
[TestFixture]
public class DeliveryEncodingChoiceTests
{
    private static TargetVersionInfo SqlServer(int major, int? compat = null)
        => new(Platform.SqlServer, $"{major}.0.1000.0", major, compat);

    [TestCase(10, 100, true)]   // 2008 R2
    [TestCase(13, 130, true)]   // 2016: FOR JSON exists, STRING_AGG does not
    [TestCase(14, 140, false)]  // 2017
    [TestCase(16, 110, false)]  // FOR JSON and STRING_AGG do not depend on the compatibility level
    public void Unset_FollowsTheSource(int major, int compat, bool expectXml)
        => Assert.That(global::DataTongs.DataTongs.ChooseXmlDeliveryEncoding(null, Platform.SqlServer, SqlServer(major, compat)),
            Is.EqualTo(expectXml));

    [Test]
    public void Unset_UndetectableSource_StaysJson()
        => Assert.That(global::DataTongs.DataTongs.ChooseXmlDeliveryEncoding(null, Platform.SqlServer, null), Is.False);

    [TestCase("Xml", 16, true)]
    [TestCase("Json", 16, false)]
    [TestCase("Xml", 10, true)]
    public void ExplicitDeliveryEncoding_Wins(string encoding, int major, bool expectXml)
        => Assert.That(global::DataTongs.DataTongs.ChooseXmlDeliveryEncoding(encoding, Platform.SqlServer, SqlServer(major, 160)),
            Is.EqualTo(expectXml));

    [TestCase(10)]
    [TestCase(13)]
    public void ExplicitJson_OnASourceThatCannotProduceIt_IsRefusedByName(int major)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            global::DataTongs.DataTongs.ChooseXmlDeliveryEncoding("Json", Platform.SqlServer, SqlServer(major, 100)));
        Assert.That(ex!.Message, Does.Contain("DeliveryEncoding").And.Contain("2017"));
    }

    [Test]
    public void ExplicitJson_UndetectableSource_IsTakenAtItsWord()
        => Assert.That(global::DataTongs.DataTongs.ChooseXmlDeliveryEncoding("Json", Platform.SqlServer, null), Is.False);

    [TestCase(Platform.PostgreSQL)]
    [TestCase(Platform.MySQL)]
    public void OtherEngines_StayJson_UnlessAskedForXml(Platform platform)
    {
        Assert.Multiple(() =>
        {
            Assert.That(global::DataTongs.DataTongs.ChooseXmlDeliveryEncoding(null, platform, null), Is.False);
            Assert.That(global::DataTongs.DataTongs.ChooseXmlDeliveryEncoding("Xml", platform, null), Is.True);
        });
    }
}
