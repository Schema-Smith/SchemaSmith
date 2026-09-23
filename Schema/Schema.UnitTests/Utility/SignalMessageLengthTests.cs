// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Schema.Capabilities;

namespace Schema.UnitTests.Utility
{
    // MySQL caps SIGNAL's MESSAGE_TEXT at 128 characters and ERRORS on a longer one ("Data too long for
    // condition item 'MESSAGE_TEXT'") rather than truncating — so an over-long message does not merely read
    // badly, it REPLACES the diagnostic the operator needed with one that says nothing about their problem.
    //
    // This has shipped three separate times on one branch (the bootstrap rename guards, and both
    // functional-index degrade sites), each caught only by running the exact failure path. Every instance was
    // written by someone who could see a sibling message a few lines above sitting just under the limit.
    // A static scan costs nothing and catches all of them at build time instead.
    [TestFixture]
    public class SignalMessageLengthTests
    {
        private const int MySqlMessageTextLimit = 128;

        // Only the MySQL family uses SIGNAL; SQL Server (THROW/RAISERROR) and PostgreSQL (RAISE) have no
        // comparable cap, so scanning their scripts would report nothing and invite confusion.
        private static IEnumerable<string> MySqlFamilyScripts() =>
            typeof(Capability).Assembly.GetManifestResourceNames()
                .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                            && (n.Contains(".MySQL.", StringComparison.OrdinalIgnoreCase)
                                || n.Contains(".MariaDb.", StringComparison.OrdinalIgnoreCase)));

        [Test]
        public void SignalMessageLiterals_AreWithinMySqlsMessageTextLimit()
        {
            var asm = typeof(Capability).Assembly;
            // Matches a literal assigned to the @ss_msg convention these procs use before SIGNAL, and a
            // literal set directly on SIGNAL. A message built by CONCAT cannot be measured statically —
            // those sites are expected to bound themselves (see the LEFT(...) uses in the quench procs).
            var literal = new Regex(@"(?:SET\s+@ss_msg\s*=|MESSAGE_TEXT\s*=)\s*'((?:[^']|'')*)'",
                RegexOptions.IgnoreCase);
            var offenders = new List<string>();

            foreach (var name in MySqlFamilyScripts())
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var reader = new StreamReader(stream);
                var body = reader.ReadToEnd();
                foreach (Match m in literal.Matches(body))
                {
                    var text = m.Groups[1].Value.Replace("''", "'");
                    if (text.Length > MySqlMessageTextLimit)
                        offenders.Add($"{name.Split('.')[^2]}: {text.Length} chars — \"{text[..60]}...\"");
                }
            }

            Assert.That(offenders, Is.Empty,
                $"MySQL errors on a SIGNAL MESSAGE_TEXT over {MySqlMessageTextLimit} characters, replacing the "
                + "operator's diagnostic with \"Data too long for condition item\". Shorten these, leaving "
                + "headroom rather than landing on the limit:\r\n  " + string.Join("\r\n  ", offenders));
        }
    }
}
