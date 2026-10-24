// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer
{
    [TestFixture]
    [Category("SqlServer")]
    public class BootstrapTableXmlQuenchTests : BaseTableQuenchTests
    {
        // Same structure, differing only in table name, so the JSON and XML bootstraps build distinct tables
        // to compare. Exercises columns (nullable + default), a clustered PK, and a nonclustered index.
        private static string DefJson(string tableName) => @"{
  ""Schema"":""dbo"",""Name"":""" + tableName + @""",
  ""Columns"":[
    {""Name"":""Id"",""DataType"":""INT"",""Nullable"":false},
    {""Name"":""Label"",""DataType"":""NVARCHAR(100)"",""Nullable"":true,""Default"":""'x'""}
  ],
  ""Indexes"":[
    {""Name"":""PK_" + tableName + @""",""PrimaryKey"":true,""Unique"":true,""Clustered"":true,""IndexColumns"":""Id""},
    {""Name"":""IX_" + tableName + @"_Label"",""Unique"":false,""Clustered"":false,""IndexColumns"":""Label""}
  ]
}";

        [Test]
        public void XmlBootstrap_BuildsTableIdenticalTo_JsonBootstrap()
        {
            const string jsonTable = "BootstrapEquivJson";
            const string xmlTable = "BootstrapEquivXml";
            var xmlScript = ResourceOrThrow().Replace("SchemaSmith.BootstrapTableQuench", "SchemaSmith.BootstrapTableQuenchXmlTest");

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);

            Exec(conn, "DROP TABLE IF EXISTS dbo." + jsonTable + "; DROP TABLE IF EXISTS dbo." + xmlTable +
                       "; DROP PROCEDURE IF EXISTS SchemaSmith.BootstrapTableQuenchXmlTest;");
            try
            {
                // JSON side: the real kindled proc.
                Exec(conn, "EXEC SchemaSmith.BootstrapTableQuench @TableDefinitions = @p", ("@p", DefJson(jsonTable)));

                // XML side: a renamed copy of the XML proc so the shared _mainDb's real proc is untouched.
                foreach (var b in SqlServerBatchSplitter.Split(xmlScript)) Exec(conn, b);
                Exec(conn, "EXEC SchemaSmith.BootstrapTableQuenchXmlTest @TableDefinitions = @p",
                    ("@p", ModelXmlSerializer.ToIngestXmlObject(DefJson(xmlTable), "Table")));

                var jsonCols = Columns(conn, jsonTable);
                var xmlCols = Columns(conn, xmlTable);
                var jsonIdx = Indexes(conn, jsonTable);
                var xmlIdx = Indexes(conn, xmlTable);

                Assert.Multiple(() =>
                {
                    Assert.That(jsonCols, Is.Not.Empty, "JSON bootstrap created no columns");
                    Assert.That(xmlCols, Is.EqualTo(jsonCols), "XML bootstrap columns differ from JSON bootstrap");
                    Assert.That(jsonIdx, Is.Not.Empty, "JSON bootstrap created no indexes");
                    Assert.That(xmlIdx, Is.EqualTo(jsonIdx), "XML bootstrap indexes differ from JSON bootstrap");
                });
            }
            finally
            {
                Exec(conn, "DROP TABLE IF EXISTS dbo." + jsonTable + "; DROP TABLE IF EXISTS dbo." + xmlTable +
                           "; DROP PROCEDURE IF EXISTS SchemaSmith.BootstrapTableQuenchXmlTest;");
            }
        }

        private static string ResourceOrThrow() =>
            ResourceLoader.Load("SchemaSmith.BootstrapTableXmlQuench.sql", Platform.SqlServer)
            ?? throw new System.Exception("SchemaSmith.BootstrapTableXmlQuench.sql not found");

        // Column signature: name, type, size, nullability, ordinal — table-name-independent.
        private static List<string> Columns(IDbConnection conn, string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT c.name + '|' + ty.name + '|' + CONVERT(VARCHAR,c.max_length) + '|' +
                                CONVERT(VARCHAR,c.is_nullable) + '|' + CONVERT(VARCHAR,c.column_id)
                                FROM sys.columns c JOIN sys.types ty ON c.user_type_id = ty.user_type_id
                                WHERE c.object_id = OBJECT_ID('dbo.' + @t) ORDER BY c.column_id";
            AddParam(cmd, "@t", table);
            return ReadStrings(cmd);
        }

        // Index signature: pk/unique/clustered flags + ordered key columns — index NAME excluded (it embeds
        // the table name, which differs by design).
        private static List<string> Indexes(IDbConnection conn, string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT CONVERT(VARCHAR,i.is_primary_key) + '|' + CONVERT(VARCHAR,i.is_unique) + '|' + i.type_desc + '|' +
                                STUFF((SELECT ',' + col.name FROM sys.index_columns ic
                                         JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                                        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                                        ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, '')
                                FROM sys.indexes i WHERE i.object_id = OBJECT_ID('dbo.' + @t) AND i.type_desc <> 'HEAP'
                                ORDER BY i.is_primary_key DESC, i.is_unique DESC";
            AddParam(cmd, "@t", table);
            return ReadStrings(cmd);
        }

        private static List<string> ReadStrings(IDbCommand cmd)
        {
            var rows = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(reader.GetString(0));
            return rows;
        }

        private static void Exec(IDbConnection conn, string sql, params (string name, string value)[] parameters)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in parameters) AddParam(cmd, n, v);
            TransientRetry.Run(() => cmd.ExecuteNonQuery());
        }

        private static void AddParam(IDbCommand cmd, string name, string value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            p.DbType = DbType.String;
            cmd.Parameters.Add(p);
        }
    }
}
