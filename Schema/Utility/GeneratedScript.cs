// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Schema.Utility;

/// <summary>
/// A script SchemaTongs writes in full on every extraction, such as a prerequisite script, carries a first line naming
/// a hash of the rest. The next extraction overwrites the file, so a hand edit is lost; <c>--Validate</c> compares the
/// hash to say so before that happens. Line endings and trailing whitespace are not part of the hash, so a checkout
/// that converts them is not an edit.
/// </summary>
public static class GeneratedScript
{
    public const string MarkerPrefix = "-- SchemaSmith: generated, sha256=";

    public static string Stamp(string body) => MarkerPrefix + Hash(body) + "\r\n" + body;

    /// <summary>True when the text carries the marker and the rest no longer matches it.</summary>
    public static bool IsHandEdited(string text)
    {
        if (!text.StartsWith(MarkerPrefix, StringComparison.Ordinal)) return false;
        var lineEnd = text.IndexOf('\n');
        if (lineEnd < 0) return true;
        var recorded = text[MarkerPrefix.Length..lineEnd].Trim();
        return !string.Equals(recorded, Hash(text[(lineEnd + 1)..]), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGenerated(string text) => text.StartsWith(MarkerPrefix, StringComparison.Ordinal);

    private static string Hash(string body)
    {
        var normalized = string.Join("\n", body.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).TrimEnd();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
