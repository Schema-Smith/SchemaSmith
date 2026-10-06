// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Schema.IntegrationTests;

public static class CatalogName
{
    /// <summary>
    /// What a MySQL-family name the server reports back must equal. Exact where names are case-sensitive
    /// (lower_case_table_names = 0), so a wrongly cased object still fails; case-insensitive where the server folds
    /// them, since it reports lowercase on 1 and DATABASE() can differ from the declared spelling on 2.
    /// </summary>
    public static IResolveConstraint Matches(IDbConnection connection, string declared)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT @@lower_case_table_names";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 0 ? Is.EqualTo(declared) : Is.EqualTo(declared).IgnoreCase;
    }
}
