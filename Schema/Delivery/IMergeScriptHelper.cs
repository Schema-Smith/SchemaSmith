// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;

namespace Schema.Delivery;

/// <summary>
/// Callback interface for platform-specific SQL fragment generation used by
/// merge script assembly. Implemented by wrapping the existing MergeScriptHelper
/// static methods. The data delivery layer owns script assembly logic including
/// deferred merge and CASCADE validation.
/// </summary>
public interface IMergeScriptHelper
{
    /// <summary>Gets PK/UK key columns. Nullable columns prefixed with *.</summary>
    string GetKeyColumns(IDbCommand cmd, string schemaOrDb, string tableName);

    /// <summary>Builds the ON clause for MERGE/conflict matching from key column CSV.</summary>
    string GetMatchColumns(string keyColumns);

    /// <summary>Gets JSON column definitions for parsing (OPENJSON WITH / json_populate_recordset / JSON_TABLE).</summary>
    string GetJsonColumnDefinitions(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null);

    /// <summary>Gets SELECT column expressions from JSON source. May include type conversions.</summary>
    string GetJsonSelectColumns(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null);

    /// <summary>Gets the insert column list (excludes computed, identity-excluded, etc.).</summary>
    string GetInsertColumns(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null);

    /// <summary>Gets the update column list with type-prefixed markers for comparison logic.</summary>
    string GetUpdateColumns(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null);

    /// <summary>Whether the table has an identity column requiring SET IDENTITY_INSERT ON (SQL Server).</summary>
    bool NeedsIdentityInsert(IDbCommand cmd, string schemaOrDb, string tableName);

    /// <summary>Gets identity column and sequence name (PostgreSQL). Returns null if none.</summary>
    string GetIdentitySequence(IDbCommand cmd, string schemaOrDb, string tableName);

    /// <summary>Gets SQL comment lines for columns with unsupported data types.</summary>
    string GetUnsupportedColumnComments(IDbCommand cmd, string schemaOrDb, string tableName);

    /// <summary>Gets DISABLE/ENABLE rule statements (PostgreSQL). Returns (disable, enable) or (null, null).</summary>
    (string Disable, string Enable) GetRuleStatements(IDbCommand cmd, string schemaOrDb, string tableName, bool updateDescendents = false);

    /// <summary>
    /// Builds a complete standard merge script. The underlying MergeScriptHelper provides
    /// the baseline implementation. Used for standard (non-deferred) delivery.
    /// </summary>
    string BuildMergeScript(IDbCommand cmd, string schemaOrDb, string tableName,
        string tableData, string keyColumns,
        bool mergeUpdate, bool mergeDelete, bool disableTriggers,
        bool tokenizeScripts, string mergeFilter,
        bool disableRules = false, bool updateDescendents = false,
        int pgServerVersionNum = 0, int mySqlServerVersionNum = 0,
        string contentEncoding = "Json", string declaredColumns = null);

    /// <summary>
    /// Gets detailed column metadata for a table. Used to build deferred merge scripts
    /// where specific FK columns are NULLed. Returns structured data rather than
    /// pre-assembled SQL so callers can modify column handling.
    /// </summary>
    List<MergeColumnInfo> GetColumnMetadata(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null);
}
