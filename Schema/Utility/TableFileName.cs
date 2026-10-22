// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Utility;

/// <summary>
/// Derives the canonical on-disk file name for a table from its content identity
/// (<c>schema</c> + <c>name</c> + optional <c>variantName</c>). The filename is a validated
/// convention, never the source of identity — variants of one table sort together because the
/// variant label is appended after the schema and table segments. Blank variant reproduces the
/// historical <c>&lt;schema&gt;.&lt;table&gt;.json</c> name, so existing packages are unchanged.
/// A schema template refuses a dot in a bare file name, so there the variant follows a <c>~</c> instead.
/// </summary>
public static class TableFileName
{
    public const char SchemaTemplateVariantSeparator = '~';

    public static string Canonical(string schema, string name, string variantName, bool schemaLess, bool inSchemaTemplate = false)
    {
        var body = schemaLess
            ? FileNameEncoder.Encode(NormalizeIdentifier(name))
            : $"{FileNameEncoder.Encode(NormalizeIdentifier(schema))}.{FileNameEncoder.Encode(NormalizeIdentifier(name))}";
        var variant = NormalizeIdentifier(variantName);
        var separator = inSchemaTemplate ? SchemaTemplateVariantSeparator : '.';
        var variantSuffix = variant.Length == 0 ? "" : $"{separator}{FileNameEncoder.Encode(variant)}";
        return $"{body}{variantSuffix}.json";
    }

    /// <summary>
    /// Strips identifier quoting (brackets, double-quotes, backticks) so the canonical filename and
    /// content-identity comparisons use the bare identifier — matching how extraction names files
    /// from the unquoted <c>INFORMATION_SCHEMA</c> form.
    /// </summary>
    public static string NormalizeIdentifier(string identifier) =>
        (identifier ?? "").Trim().Trim('[', ']', '"', '`').Trim();
}
