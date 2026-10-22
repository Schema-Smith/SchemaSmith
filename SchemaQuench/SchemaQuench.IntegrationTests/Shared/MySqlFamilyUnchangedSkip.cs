// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>The MySQL-family package for <see cref="UnchangedModuleSkipSharedTests"/>, in the shapes SchemaTongs writes.</summary>
public static class MySqlFamilyUnchangedSkip
{
    public const string TableJson = """
        { "Name": "`unchanged_t`", "Columns": [ { "Name": "`x`", "DataType": "INT", "Nullable": false }, { "Name": "`w`", "DataType": "INT", "Nullable": true } ],
          "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`x`" } ] }
        """;

    public static readonly Dictionary<string, string> Scripts = new()
    {
        ["Views"] = "CREATE OR REPLACE VIEW `v_unchanged` AS select `x` from `unchanged_t`;",
        ["Functions"] = "DROP FUNCTION IF EXISTS `f_unchanged`;\nDELIMITER //\nCREATE FUNCTION `f_unchanged`(a INT) RETURNS INT DETERMINISTIC RETURN a + 1 //\nDELIMITER ;\n",
        ["Procedures"] = "DROP PROCEDURE IF EXISTS `p_unchanged`;\nDELIMITER //\nCREATE PROCEDURE `p_unchanged`() SELECT 1 //\nDELIMITER ;\n",
        ["Triggers"] = "DROP TRIGGER IF EXISTS `tr_unchanged`;\nDELIMITER //\nCREATE TRIGGER `tr_unchanged` BEFORE INSERT ON `unchanged_t` FOR EACH ROW SET NEW.w = 1 //\nDELIMITER ;\n"
    };

    public const string AlterViewByHand = "CREATE OR REPLACE VIEW `v_unchanged` AS select `x`, `w` from `unchanged_t`";
    public const string ChangedFunctionScript = "DROP FUNCTION IF EXISTS `f_unchanged`;\nDELIMITER //\nCREATE FUNCTION `f_unchanged`(a INT) RETURNS INT DETERMINISTIC RETURN a + 2 //\nDELIMITER ;\n";
    public static readonly string[] DropStatements =
    [
        "DROP TRIGGER IF EXISTS `tr_unchanged`", "DROP VIEW IF EXISTS `v_unchanged`", "DROP FUNCTION IF EXISTS `f_unchanged`",
        "DROP PROCEDURE IF EXISTS `p_unchanged`", "DROP TABLE IF EXISTS `unchanged_t`"
    ];
}
