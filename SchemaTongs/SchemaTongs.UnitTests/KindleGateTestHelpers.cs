// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NSubstitute;
using Schema.Domain;
using Schema.Utility;

namespace SchemaTongs.UnitTests;

internal static class KindleGateTestHelpers
{
    /// <summary>
    /// True when the SQL is the read-only probe ForgeKindler runs before it would kindle. Extraction can
    /// read a replica, so it verifies the helpers instead of installing them — and that probe is one more
    /// ExecuteScalar these fixtures have to answer.
    /// <para>Answering it matters because the mocks return table JSON for any scalar query they do not
    /// recognise, and <c>Convert.ToInt32</c> on JSON throws <c>FormatException</c> — which is how this
    /// surfaced. Every fixture here models a WRITABLE target, so the honest answer is 0.</para>
    /// </summary>
    public static bool IsReadOnlyProbe(string sql) =>
        sql != null && (sql.Contains("pg_is_in_recovery")
                        || sql.Contains("Updateability")
                        || sql.Contains("@@read_only"));

    /// <summary>
    /// Stub an NSubstitute IDbCommand mock so a SchemaTongs unit test flowing through
    /// ForgeKindler.KindleTheForge takes the version-gate SKIP path without executing any
    /// kindle DDL on the mock. The stub returns 1L for the MySQL GET_LOCK acquire, 1L for the
    /// SchemaSmith_KindleStamp existence probe against information_schema.tables, and the current
    /// ComputeKindleStamp(MySQL) for the actual stamp SELECT (so the gate compares equal and
    /// returns). Unrelated ExecuteScalar calls fall through to null, preserving any per-test
    /// stubbing the caller has set up for its own queries.
    /// </summary>
    public static void StubMySqlKindleGate(IDbCommand command, Platform platform = Platform.MySQL)
    {
        // MariaDB's stamp also records whether it reports CHECK constraints, which this stub answers no to.
        var stamp = ForgeKindler.ComputeKindleStamp(platform, checkConstraintsReadable: platform == Platform.MariaDb ? false : null);
        command.ExecuteScalar().Returns(_ =>
        {
            var sql = command.CommandText ?? string.Empty;
            if (IsReadOnlyProbe(sql))
                return (object)0;
            if (sql.Contains("GET_LOCK"))
                return (object)1L;
            if (sql.Contains("information_schema.tables"))
                return (object)1L;
            if (sql.Contains("SchemaSmith_KindleStamp") && sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                return (object)stamp;
            return null;
        });
    }
}
