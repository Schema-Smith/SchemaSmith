// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Delivery;

/// <summary>
/// Structured column metadata returned by IMergeScriptHelper.GetColumnMetadata.
/// Provides enough detail to build deferred merge scripts with platform-appropriate
/// type casting and NULL handling.
/// </summary>
public class MergeColumnInfo
{
    /// <summary>Column name (unquoted).</summary>
    public string Name { get; set; }

    /// <summary>Platform-specific data type (e.g., "NVARCHAR", "int4", "varchar").</summary>
    public string DataType { get; set; }

    /// <summary>Full type expression for JSON parsing (e.g., "NVARCHAR(100)", "integer", "varchar(255)").</summary>
    public string JsonParseType { get; set; }

    /// <summary>Whether the column is nullable.</summary>
    public bool IsNullable { get; set; }

    /// <summary>Whether the column has an identity/auto-increment.</summary>
    public bool IsIdentity { get; set; }

    /// <summary>Whether the column is computed/generated.</summary>
    public bool IsComputed { get; set; }

    /// <summary>Whether the column is a geometry/geography type requiring special handling.</summary>
    public bool IsGeometry { get; set; }

    /// <summary>Whether the column is a binary type requiring special encoding.</summary>
    public bool IsBinary { get; set; }

    /// <summary>Whether the column is an XML type.</summary>
    public bool IsXml { get; set; }

    /// <summary>Max character/binary length. -1 for MAX.</summary>
    public int MaxLength { get; set; }

    /// <summary>Numeric precision.</summary>
    public int Precision { get; set; }

    /// <summary>Numeric scale.</summary>
    public int Scale { get; set; }

    /// <summary>Fractional-seconds precision of a date/time type.</summary>
    public int DateTimePrecision { get; set; }

    /// <summary>SQL Server: the column's type as the catalog names it -- an alias type by its own name, schema-qualified
    /// outside dbo and sys. Null on the other engines.</summary>
    public string DeclaredType { get; set; }

    /// <summary>SQL Server: the bracketed schema-qualified XML schema collection a typed xml column is bound to; null when
    /// it has none.</summary>
    public string XmlSchemaCollection { get; set; }
}
