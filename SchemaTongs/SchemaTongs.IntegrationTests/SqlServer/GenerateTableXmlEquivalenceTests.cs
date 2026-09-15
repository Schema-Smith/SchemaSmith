// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.SqlServer;

// Compare-side equivalence: GenerateTableXml (FOR XML PATH, for the pre-2016 binary floor that lacks
// FOR JSON) must extract a table into the SAME domain model as GenerateTableJSON — MINUS the Extensions
// bag, which is dropped on the legacy encoding by design. Runs on the modern container (FOR JSON works at
// any compat there, so this asserts the FOR-XML-PATH emit path is equivalent; genuine old-binary absence
// is a tier-2 concern). The XML proc is created from its resource (not yet kindled) and its output is
// converted back to JSON via ModelXmlSerializer.FromIngestXml before deserialization.
[Category("SqlServer")]
[TestFixture]
public class GenerateTableXmlEquivalenceTests
{
    private string _integrationDb = "";
    private string _connectionString;
    private string _testConnectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = $"GenerateTableXml_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
        _testConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], _integrationDb, config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_integrationDb}];";
        cmd.ExecuteNonQuery();

        // A non-default filegroup for the #filegroups JSON/XML equivalence test below -- reuses the new
        // database's own data-file directory rather than assuming one.
        cmd.CommandText = $@"
DECLARE @v_DataPath NVARCHAR(500) = (SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX('\', REVERSE(physical_name)) + 1) FROM sys.master_files WHERE database_id = DB_ID('{_integrationDb}') AND file_id = 1);
ALTER DATABASE [{_integrationDb}] ADD FILEGROUP [FG_Test];
EXEC('ALTER DATABASE [{_integrationDb}] ADD FILE (NAME = ''FG_Test_1'', FILENAME = ''' + @v_DataPath + 'FG_Test_1.ndf'') TO FILEGROUP [FG_Test]');";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        cmd.CommandText = @"
CREATE FULLTEXT CATALOG [FT_Catalog];
CREATE FULLTEXT STOPLIST [SL_Test];";
        cmd.ExecuteNonQuery();

        // GenerateTableXml is not kindled yet (that is the later ForgeKindler encoding-aware wiring task);
        // create it from its resource so this equivalence test can exercise it directly. Via KindleOneFile so
        // the GO-batched idempotent create form (pre-2016 CREATE OR ALTER replacement) is split correctly.
        ForgeKindler.KindleOneFile(cmd, "SchemaSmith.GenerateTableXml.sql", Platform.SqlServer);

        conn.Close();
    }

    [Test]
    public void GenerateTableXml_ExtractsSameModelAs_GenerateTableJson_MinusExtensions()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // A rich table exercising every array container: identity, computed+persisted, default, NOT NULL/NULL
        // mix, PK clustered, a nonclustered index with a DESC key + INCLUDE + filter, an FK with a referential
        // action, a user statistic, an XML column with primary + secondary XML indexes, a genuine table-level
        // check (parent_column_id = 0), a full-text index (the single-object, no-json:Array container), and
        // (backlog E3) a pair of sparse columns plus their COLUMN_SET FOR ALL_SPARSE_COLUMNS aggregator --
        // the JSON/XML twin pairing the new IsColumnSet property must keep in lockstep.
        cmd.CommandText = @"
CREATE TABLE dbo.Parent (Id INT NOT NULL PRIMARY KEY, Code VARCHAR(20) NOT NULL);
CREATE UNIQUE INDEX UX_Parent_Code ON dbo.Parent (Code);

CREATE TABLE dbo.Rich (
    Id INT IDENTITY(1,1) NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    Amount DECIMAL(10,2) NULL CONSTRAINT DF_Rich_Amount DEFAULT 0,
    ParentCode VARCHAR(20) NULL,
    Computed AS (Amount * 2) PERSISTED,
    Flag BIT NOT NULL,
    Doc XML NULL,
    SparseProp VARCHAR(20) SPARSE NULL,
    SpecialCols XML COLUMN_SET FOR ALL_SPARSE_COLUMNS,
    CONSTRAINT PK_Rich PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT CK_Rich_Amount CHECK (Amount >= 0),
    CONSTRAINT CK_Rich_Table CHECK (Amount < 100000 OR Name IS NOT NULL),
    CONSTRAINT FK_Rich_Parent FOREIGN KEY (ParentCode) REFERENCES dbo.Parent (Code) ON DELETE SET NULL
);
CREATE NONCLUSTERED INDEX IX_Rich_Name ON dbo.Rich (Name DESC) INCLUDE (Amount) WHERE Flag = 1;
CREATE STATISTICS ST_Rich_Amount ON dbo.Rich (Amount);
CREATE PRIMARY XML INDEX XI_Primary_Doc ON dbo.Rich (Doc);
CREATE XML INDEX XI_Secondary_Doc_Path ON dbo.Rich (Doc) USING XML INDEX XI_Primary_Doc FOR PATH;
CREATE FULLTEXT INDEX ON dbo.Rich (Name) KEY INDEX PK_Rich ON FT_Catalog WITH CHANGE_TRACKING = AUTO, STOPLIST = SL_Test;

-- A user extended property on the table + a column, to prove Extensions is the ONLY documented divergence.
EXEC sys.sp_addextendedproperty 'MS_Description', 'A rich table', 'SCHEMA', [dbo], 'TABLE', [Rich], NULL, NULL;
";
        cmd.ExecuteNonQuery();

        var jsonModel = PlatformDeserializer.DeserializeTable(GenerateTableJson(cmd, "dbo", "Rich"), Platform.SqlServer);
        var xmlModel = PlatformDeserializer.DeserializeTable(
            ModelXmlSerializer.FromIngestXml(GenerateTableXml(cmd, "dbo", "Rich")), Platform.SqlServer);

        // The JSON path carries Extensions; the XML (legacy) path drops it by design. Strip Extensions from
        // both and assert the rest of the model is identical.
        Assert.That(NormalizeMinusExtensions(xmlModel), Is.EqualTo(NormalizeMinusExtensions(jsonModel)));

        conn.Close();
    }

    [Test]
    public void GenerateTableXml_PartitionedTableWithMixedCompression_ExtractsSameModelAs_GenerateTableJson()
    {
        // Task C1-0b: sys.partitions is one row PER PARTITION; the prior scalar CompressionType
        // subquery raised Msg 512 the moment a table had more than one partition. Partition 2 is
        // rebuilt to PAGE while the rest stay NONE so the two encodings are compared on the highest-
        // risk case -- non-uniform compression producing the 'MIXED' sentinel -- not just the already-
        // covered uniform case, since a JSON/XML divergence here is an encoding-dependent bug that
        // would only surface on a legacy-compat target.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE PARTITION FUNCTION PF_XmlEquivPartitioned (INT) AS RANGE LEFT FOR VALUES (100, 200, 300);
CREATE PARTITION SCHEME PS_XmlEquivPartitioned AS PARTITION PF_XmlEquivPartitioned ALL TO ([PRIMARY]);

CREATE TABLE dbo.XmlEquivPartitioned (
    Id INT NOT NULL,
    Val VARCHAR(50) NULL,
    CONSTRAINT PK_XmlEquivPartitioned PRIMARY KEY CLUSTERED (Id) ON PS_XmlEquivPartitioned(Id)
) ON PS_XmlEquivPartitioned(Id);

ALTER TABLE dbo.XmlEquivPartitioned REBUILD PARTITION = 2 WITH (DATA_COMPRESSION = PAGE);
";
        cmd.ExecuteNonQuery();

        var jsonModel = (SqlServerTable)PlatformDeserializer.DeserializeTable(GenerateTableJson(cmd, "dbo", "XmlEquivPartitioned"), Platform.SqlServer);
        var xmlModel = (SqlServerTable)PlatformDeserializer.DeserializeTable(
            ModelXmlSerializer.FromIngestXml(GenerateTableXml(cmd, "dbo", "XmlEquivPartitioned")), Platform.SqlServer);

        Assert.That(jsonModel.CompressionType, Is.EqualTo("MIXED"), "the JSON proc must flag non-uniform per-partition compression rather than emit one partition's value");
        Assert.That(xmlModel.CompressionType, Is.EqualTo("MIXED"), "the XML twin must agree with the JSON proc's compression reading");
        Assert.That(NormalizeMinusExtensions(xmlModel), Is.EqualTo(NormalizeMinusExtensions(jsonModel)));

        conn.Close();
    }

    [Test]
    public void GenerateTableXml_FileGroupPlacement_ExtractsSameModelAs_GenerateTableJson()
    {
        // #filegroups: table on a non-default filegroup, one index left on the default (PRIMARY) and one
        // explicitly placed on the same non-default filegroup -- exercising the emit-only-when-non-default
        // rule on both sides (table AND index) and both encodings at once.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.XmlEquivFileGroup (
    Id INT NOT NULL,
    Somedata VARCHAR(50) NULL,
    CONSTRAINT PK_XmlEquivFileGroup PRIMARY KEY NONCLUSTERED (Id) ON [PRIMARY]
) ON [FG_Test];
CREATE NONCLUSTERED INDEX IX_XmlEquivFileGroup_Somedata ON dbo.XmlEquivFileGroup (Somedata) ON [FG_Test];
";
        cmd.ExecuteNonQuery();

        var jsonModel = (SqlServerTable)PlatformDeserializer.DeserializeTable(GenerateTableJson(cmd, "dbo", "XmlEquivFileGroup"), Platform.SqlServer);
        var xmlModel = (SqlServerTable)PlatformDeserializer.DeserializeTable(
            ModelXmlSerializer.FromIngestXml(GenerateTableXml(cmd, "dbo", "XmlEquivFileGroup")), Platform.SqlServer);

        Assert.Multiple(() =>
        {
            Assert.That(jsonModel.FileGroup, Is.EqualTo("[FG_Test]"));
            Assert.That(xmlModel.FileGroup, Is.EqualTo("[FG_Test]"), "the XML twin must agree with the JSON proc's table-level filegroup reading");
            Assert.That(NormalizeMinusExtensions(xmlModel), Is.EqualTo(NormalizeMinusExtensions(jsonModel)));
        });

        conn.Close();
    }

    [Test]
    public void GenerateTableXml_CdcFilegroup_ExtractsSameModelAs_GenerateTableJson()
    {
        // #417: the legacy XML encoding must carry CdcFilegroup exactly as the JSON proc does, or a pre-2016
        // target's extraction would silently drop the placement.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1) EXEC sys.sp_cdc_enable_db;
CREATE TABLE dbo.XmlEquivCdcFg (Id INT NOT NULL PRIMARY KEY, Val INT NULL);
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'XmlEquivCdcFg', @role_name = NULL, @filegroup_name = N'FG_Test';
";
        cmd.ExecuteNonQuery();

        var jsonModel = (SqlServerTable)PlatformDeserializer.DeserializeTable(GenerateTableJson(cmd, "dbo", "XmlEquivCdcFg"), Platform.SqlServer);
        var xmlModel = (SqlServerTable)PlatformDeserializer.DeserializeTable(
            ModelXmlSerializer.FromIngestXml(GenerateTableXml(cmd, "dbo", "XmlEquivCdcFg")), Platform.SqlServer);

        Assert.Multiple(() =>
        {
            Assert.That(jsonModel.CdcFilegroup, Is.EqualTo("[FG_Test]"));
            Assert.That(xmlModel.CdcFilegroup, Is.EqualTo("[FG_Test]"));
            Assert.That(NormalizeMinusExtensions(xmlModel), Is.EqualTo(NormalizeMinusExtensions(jsonModel)));
        });

        conn.Close();
    }

    private string GenerateTableJson(IDbCommand cmd, string schema, string table)
    {
        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableJson @p_Schema = '{schema}', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var json = new StringBuilder();
        while (reader.Read()) json.Append(reader.GetString(0)).Append("\r\n");
        return json.ToString();
    }

    // FOR XML output is split across rows once it exceeds ~2033 chars, so concatenate every row into the
    // single <Table> document.
    private string GenerateTableXml(IDbCommand cmd, string schema, string table)
    {
        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableXml @p_Schema = '{schema}', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var xml = new StringBuilder();
        while (reader.Read()) xml.Append(reader.GetValue(0)?.ToString());
        return xml.ToString();
    }

    // Serialize the model to JSON, then drop (a) the Extensions bag — dropped on the legacy encoding by
    // design — and (b) any empty-string scalar. The XML encoding round-trips an empty value as an empty
    // element, which SerializeXNode maps to null (then omitted on serialize); "" and absent are semantically
    // identical for these placeholder fields (OldName, Collation, mask/encryption sentinels), so normalizing
    // both to absent asserts the meaningful model is identical without a spurious ""-vs-null mismatch.
    private static string NormalizeMinusExtensions(Table table)
    {
        var token = JToken.Parse(JsonHelper.Serialize(table));
        Normalize(token);
        return token.ToString(Formatting.Indented);
    }

    private static void Normalize(JToken node)
    {
        switch (node)
        {
            case JObject o:
                foreach (var p in o.Properties().ToList())
                {
                    if (p.Name == "Extensions" || (p.Value is JValue { Type: JTokenType.String } v && (string)v.Value! == ""))
                        p.Remove();
                    else
                        Normalize(p.Value);
                }
                break;
            case JArray a:
                foreach (var item in a) Normalize(item);
                break;
        }
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
IF DB_ID('{_integrationDb}') IS NOT NULL
  ALTER DATABASE [{_integrationDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE IF EXISTS [{_integrationDb}];";
        cmd.ExecuteNonQuery();
        conn.Close();
    }
}
