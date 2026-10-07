// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Utility;

/// <summary>
/// Catalog predicates that match a MySQL or MariaDB database or table name the way the server does.
/// </summary>
public static class MySqlNameMatch
{
    // With lower_case_table_names >= 1 the catalog spells databases and tables in lowercase while callers pass the
    // configured spelling, so an exact compare finds nothing. The bare '=' is an index-usable prefilter in the
    // catalog's own collation; the folded binary compare then decides, so lower_case_table_names = 0 stays exact.
    // Written inline rather than through SchemaSmith_IdentifierKey because DataTongs runs without kindling.
    public static string Folded(string catalogColumn, string valueSql) =>
        $"{catalogColumn} = {valueSql} AND {Key(catalogColumn)} = {Key(valueSql)}";

    // For a routine's schema (ROUTINES.ROUTINE_SCHEMA, PARAMETERS.SPECIFIC_SCHEMA). With lower_case_table_names = 2,
    // MariaDB stores it in lowercase and answers a constant compare from a case-sensitive key, so the configured
    // spelling of a mixed-case database finds no routines. The prefilter therefore compares the folded value.
    public static string FoldedRoutineSchema(string catalogColumn, string valueSql) =>
        $"{catalogColumn} = {FoldedValue(valueSql)} AND {Key(catalogColumn)} = {Key(valueSql)}";

    private static string FoldedValue(string sql) => $"IF(@@lower_case_table_names = 0, {sql}, LOWER({sql}))";

    private static string Key(string sql) => $"CONVERT({FoldedValue(sql)} USING utf8mb4) COLLATE utf8mb4_bin";
}
