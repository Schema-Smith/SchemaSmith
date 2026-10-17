// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// MY-026: COMPRESSION, KEY_BLOCK_SIZE and MariaDB's PAGE_COMPRESSED / PAGE_COMPRESSION_LEVEL were applied when the table
/// was created and never compared again, so an edited package left the existing table as it was, at exit 0.
/// </summary>
public abstract class TableQuench_CompressionSharedTests : BaseTableQuenchTests
{
    private string Json(string t, string options) => $$"""
        [{ "Name": "{{t}}"{{options}},
           "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ],
           "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] }]
        """;

    private void WithTable(Action<IDbCommand, string, Func<string>> body)
    {
        var t = $"cmp_{Guid.NewGuid():N}"[..14];
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Options()
        {
            cmd.CommandText = $"SELECT UPPER(REPLACE(CREATE_OPTIONS, '`', '')) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{t}'";
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        try { body(cmd, t, Options); }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{t}`; DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectName LIKE '{t}%'";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void AnEditedKeyBlockSize_IsApplied()
    {
        WithTable((cmd, t, options) =>
        {
            RunTableQuenchProc(cmd, Json(t, """, "RowFormat": "COMPRESSED", "KeyBlockSize": 8"""));
            Assert.That(options(), Does.Contain("KEY_BLOCK_SIZE=8"), "precondition");
            RunTableQuenchProc(cmd, Json(t, """, "RowFormat": "COMPRESSED", "KeyBlockSize": 4"""));
            Assert.That(options(), Does.Contain("KEY_BLOCK_SIZE=4"));
        });
    }

    [Test]
    public void AnEditedCompression_IsApplied()
    {
        if (Platform != Platform.MySQL) Assert.Ignore("COMPRESSION is MySQL's; MariaDB has PAGE_COMPRESSED.");
        WithTable((cmd, t, options) =>
        {
            RunTableQuenchProc(cmd, Json(t, """, "Compression": "zlib" """));
            Assert.That(options(), Does.Contain("COMPRESSION=\"ZLIB\""), "precondition");
            RunTableQuenchProc(cmd, Json(t, """, "Compression": "lz4" """));
            Assert.That(options(), Does.Contain("COMPRESSION=\"LZ4\""));
        });
    }

    [Test]
    public void PageCompression_IsTurnedOn_AndItsLevelApplied()
    {
        if (Platform != Platform.MariaDb) Assert.Ignore("PAGE_COMPRESSED is MariaDB's.");
        WithTable((cmd, t, options) =>
        {
            RunTableQuenchProc(cmd, Json(t, ""));
            Assert.That(options(), Does.Not.Contain("PAGE_COMPRESSED"), "precondition");
            RunTableQuenchProc(cmd, Json(t, """, "PageCompressed": true, "PageCompressionLevel": 3"""));
            // Some MariaDB patches (10.6.20) render the values unquoted; the deploy's reader accepts either, so does this.
            Assert.That(options().Replace("'", ""), Does.Contain("PAGE_COMPRESSED=1").And.Contain("PAGE_COMPRESSION_LEVEL=3"));
            RunTableQuenchProc(cmd, Json(t, """, "PageCompressed": true, "PageCompressionLevel": 6"""));
            Assert.That(options().Replace("'", ""), Does.Contain("PAGE_COMPRESSION_LEVEL=6"));
        });
    }
}
