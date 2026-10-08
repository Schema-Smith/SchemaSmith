// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// A collation for the shared fixture databases that is NOT the server's. A temp table column declared without
/// COLLATE takes tempdb's collation, which is the server's; compared with a column of a database in any other
/// collation, the statement fails to compile ("Cannot resolve the collation conflict"). With the fixture databases
/// in a different collation, every test that deploys into them proves no procedure makes that comparison. Both
/// candidates are code page 1252 and case- and accent-insensitive, so nothing else a test observes changes.
/// </summary>
public static class ForeignCollation
{
    public static string For(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT CAST(SERVERPROPERTY('Collation') AS NVARCHAR(128))";
        var server = cmd.ExecuteScalar() as string;
        return string.Equals(server, "Latin1_General_CI_AS", StringComparison.OrdinalIgnoreCase)
            ? "SQL_Latin1_General_CP1_CI_AS"
            : "Latin1_General_CI_AS";
    }
}
