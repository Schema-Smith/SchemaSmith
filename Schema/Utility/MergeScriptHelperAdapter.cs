// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using Schema.Domain;
using Schema.Delivery;

namespace Schema.Utility;

/// <summary>
/// Adapts the static MergeScriptHelper to the IMergeScriptHelper interface.
/// Provides the platform-specific SQL fragments a merge script is assembled from.
/// Registered in FactoryContainer by tool startup code.
/// </summary>
public class MergeScriptHelperAdapter : IMergeScriptHelper
{
    private readonly Platform _platform;

    public Platform Platform => _platform;

    public MergeScriptHelperAdapter(Platform platform)
    {
        _platform = platform;
    }

    public string GetKeyColumns(IDbCommand cmd, string schemaOrDb, string tableName)
        => MergeScriptHelper.GetKeyColumns(_platform, cmd, schemaOrDb, tableName);

    public string GetMatchColumns(string keyColumns)
        => MergeScriptHelper.GetMatchColumns(_platform, keyColumns);

    public string GetJsonColumnDefinitions(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null)
        => MergeScriptHelper.GetJsonColumnDefinitions(_platform, cmd, schemaOrDb, tableName, jsonKeys);

    public string GetJsonSelectColumns(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null)
        => MergeScriptHelper.GetJsonSelectColumns(_platform, cmd, schemaOrDb, tableName, jsonKeys);

    public string GetInsertColumns(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null)
        => MergeScriptHelper.GetInsertColumns(_platform, cmd, schemaOrDb, tableName, jsonKeys);

    public string GetUpdateColumns(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null)
        => MergeScriptHelper.GetUpdateColumns(_platform, cmd, schemaOrDb, tableName, jsonKeys);

    public bool NeedsIdentityInsert(IDbCommand cmd, string schemaOrDb, string tableName)
        => MergeScriptHelper.NeedsIdentityInsert(_platform, cmd, schemaOrDb, tableName);

    public string GetIdentitySequence(IDbCommand cmd, string schemaOrDb, string tableName)
        => MergeScriptHelper.GetIdentitySequence(_platform, cmd, schemaOrDb, tableName);

    public string GetUnsupportedColumnComments(IDbCommand cmd, string schemaOrDb, string tableName)
        => MergeScriptHelper.GetUnsupportedColumnComments(_platform, cmd, schemaOrDb, tableName);

    public (string Disable, string Enable) GetRuleStatements(IDbCommand cmd, string schemaOrDb, string tableName, bool updateDescendents = false)
        => MergeScriptHelper.GetRuleStatements(_platform, cmd, schemaOrDb, tableName, updateDescendents);

    public string BuildMergeScript(IDbCommand cmd, string schemaOrDb, string tableName,
        string tableData, string keyColumns,
        bool mergeUpdate, bool mergeDelete, bool disableTriggers,
        bool tokenizeScripts, string mergeFilter,
        bool disableRules = false, bool updateDescendents = false,
        int pgServerVersionNum = 0, int mySqlServerVersionNum = 0,
        string contentEncoding = "Json")
        => MergeScriptHelper.BuildMergeScript(_platform, cmd, schemaOrDb, tableName,
            tableData, keyColumns, mergeUpdate, mergeDelete, disableTriggers,
            tokenizeScripts, mergeFilter, disableRules, updateDescendents,
            destSchemaOverride: null, pgServerVersionNum: pgServerVersionNum,
            mySqlServerVersionNum: mySqlServerVersionNum, contentEncoding: contentEncoding);

    public List<MergeColumnInfo> GetColumnMetadata(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null)
        => MergeScriptHelper.GetColumnMetadata(_platform, cmd, schemaOrDb, tableName, jsonKeys);
}
