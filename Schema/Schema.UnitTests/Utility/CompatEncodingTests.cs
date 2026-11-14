// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Utility;

namespace Schema.UnitTests.Utility
{
    [TestFixture]
    public class CompatEncodingTests
    {
        [TestCase("modern", 100, 10, IngestEncoding.Json)]   // override wins over an old target
        [TestCase("legacy", 160, 16, IngestEncoding.Xml)]    // override wins over a modern target
        [TestCase("auto", 120, 15, IngestEncoding.Xml)]      // compat < 130 -> xml on a modern binary
        [TestCase("auto", 130, 15, IngestEncoding.Json)]     // compat >= 130 -> json
        [TestCase("auto", null, 12, IngestEncoding.Xml)]     // 2014 binary (major < 14), compat unknown -> xml
        // SQL Server 2016 is the one band where compat and binary disagree: compat 130 makes OPENJSON
        // parse, but the JSON-path scripts also use STRING_AGG, which is 2017. Both must take XML.
        [TestCase("auto", 130, 13, IngestEncoding.Xml)]      // 2016 binary at compat 130 -> xml (no STRING_AGG)
        [TestCase("auto", null, 13, IngestEncoding.Xml)]     // 2016 binary, compat unknown -> xml
        [TestCase("auto", 130, 14, IngestEncoding.Json)]     // 2017 binary is the first with STRING_AGG -> json
        [TestCase(null, 130, 16, IngestEncoding.Json)]       // null override behaves as auto
        [TestCase("", 120, 16, IngestEncoding.Xml)]          // empty override behaves as auto
        [TestCase("AUTO", 110, 16, IngestEncoding.Xml)]      // override is case-insensitive
        [TestCase("Legacy", 130, 16, IngestEncoding.Xml)]    // legacy override, case-insensitive
        public void Select_resolves_encoding(string overrideValue, int? compat, int serverMajor, IngestEncoding expected)
            => Assert.That(CompatEncoding.Select(overrideValue, compat, serverMajor), Is.EqualTo(expected));

        // Only legacy and modern force the encoding; auto, a typo, or the Json / Xml an older sample named is chosen
        // automatically, and the log has to say so.
        [TestCase("legacy", " (forced by Target:CompatEncoding=legacy)", false)]
        [TestCase("Modern", " (forced by Target:CompatEncoding=Modern)", false)]
        [TestCase("auto", " (auto)", false)]
        [TestCase(null, " (auto)", false)]
        [TestCase("", " (auto)", false)]
        [TestCase("Json", " (auto)", true)]
        [TestCase("Xml", " (auto)", true)]
        [TestCase("legacyy", " (auto)", true)]
        public void HowChosen_SaysForcedOnlyForLegacyOrModern(string value, string expected, bool unrecognized)
        {
            Assert.That(CompatEncoding.HowChosen("Target:CompatEncoding", value), Is.EqualTo(expected));
            Assert.That(CompatEncoding.IsUnrecognized(value), Is.EqualTo(unrecognized));
        }
    }
}
