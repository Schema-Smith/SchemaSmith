// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Domain.PostgreSQL;
using Schema.Domain.MySQL;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

public class TokenHelperTests
{
    [Test]
    public void ShouldResolveFileTokens()
    {
        var basePath = "C:/Projects/MyMetadata/Templates/Main";
        var tokens = new Dictionary<string, string>
        {
            {"MainDB", "MainDB"},
            {"SecondaryDB", "SecondaryDB"},
            {"MyTableData", "<*File*>Tables/dbo.MyTable.data"}
        };
        var filePath = Path.Combine(basePath, "Tables/dbo.MyTable.data");
        var fileContent = "[{\"Status\":1,\"StatusDesc\":\"Open\"},{\"Status\":2,\"StatusDesc\":\"Closed\"}]";

        var mockFileWrapper = Substitute.For<IFile>();
        mockFileWrapper.Exists(filePath).Returns(true);
        mockFileWrapper.ReadAllText(filePath).Returns(fileContent);
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFileWrapper);
            TokenHelper.ResolveFileTokens(tokens, basePath, Platform.SqlServer);
            Assert.That(tokens["MyTableData"], Is.EqualTo(fileContent));
            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ShouldResolveBinaryFileTokens_SqlServer_EmitsHexWith0xPrefix()
    {
        var basePath = "C:/Projects/MyMetadata";
        var tokens = new Dictionary<string, string>
        {
            {"MainDB", "MainDB"},
            {"SecondaryDB", "SecondaryDB"},
            {"BinaryContent", "<*BinaryFile*>Files/MyBinary.dll"}
        };
        var filePath = Path.Combine(basePath, "Files/MyBinary.dll");
        var fileContent = new byte[] {12, 255, 6, 55, 77, 125};

        var mockFileWrapper = Substitute.For<IFile>();
        mockFileWrapper.Exists(filePath).Returns(true);
        mockFileWrapper.ReadAllBytes(filePath).Returns(fileContent);
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFileWrapper);
            TokenHelper.ResolveFileTokens(tokens, basePath, Platform.SqlServer);
            Assert.That(tokens["BinaryContent"], Is.EqualTo($"0x{BitConverter.ToString(fileContent).Replace("-", "")}"));
            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ShouldResolveBinaryFileTokens_MySQL_EmitsHexWith0xPrefix()
    {
        var basePath = "C:/Projects/MyMetadata";
        var tokens = new Dictionary<string, string>
        {
            {"MainDB", "MainDB"},
            {"BinaryContent", "<*BinaryFile*>Files/MyBinary.dll"}
        };
        var filePath = Path.Combine(basePath, "Files/MyBinary.dll");
        var fileContent = new byte[] {12, 255, 6, 55, 77, 125};

        var mockFileWrapper = Substitute.For<IFile>();
        mockFileWrapper.Exists(filePath).Returns(true);
        mockFileWrapper.ReadAllBytes(filePath).Returns(fileContent);
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFileWrapper);
            TokenHelper.ResolveFileTokens(tokens, basePath, Platform.MySQL);
            Assert.That(tokens["BinaryContent"], Is.EqualTo($"0x{BitConverter.ToString(fileContent).Replace("-", "")}"));
            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ShouldResolveBinaryFileTokens_PostgreSQL_EmitsByteaEscapedHex()
    {
        var basePath = "C:/Projects/MyMetadata";
        var tokens = new Dictionary<string, string>
        {
            {"MainDB", "MainDB"},
            {"BinaryContent", "<*BinaryFile*>Files/MyBinary.dll"}
        };
        var filePath = Path.Combine(basePath, "Files/MyBinary.dll");
        var fileContent = new byte[] {12, 255, 6, 55, 77, 125};

        var mockFileWrapper = Substitute.For<IFile>();
        mockFileWrapper.Exists(filePath).Returns(true);
        mockFileWrapper.ReadAllBytes(filePath).Returns(fileContent);
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFileWrapper);
            TokenHelper.ResolveFileTokens(tokens, basePath, Platform.PostgreSQL);
            Assert.That(tokens["BinaryContent"], Is.EqualTo($"E'\\\\x{BitConverter.ToString(fileContent).Replace("-", "")}'::bytea"));
            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ShouldThrowWhenFileTokenMissing()
    {
        var basePath = "C:/Projects/MyMetadata";
        var tokens = new Dictionary<string, string>
        {
            {"MissingFile", "<*File*>Tables/missing.data"}
        };

        var mockFileWrapper = Substitute.For<IFile>();
        mockFileWrapper.Exists(Arg.Any<string>()).Returns(false);
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFileWrapper);
            var ex = Assert.Throws<Schema.Domain.PackageException>(() => TokenHelper.ResolveFileTokens(tokens, basePath, Platform.SqlServer));
            Assert.That(ex.Message, Does.Contain("missing file"));
            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ShouldCollectAllErrorsWithoutThrowing_WhenTolerateErrorsIsTrue()
    {
        var basePath = "C:/Projects/MyMetadata";
        var tokens = new Dictionary<string, string>
        {
            {"FirstMissing", "<*File*>Tables/first.data"},
            {"SecondMissing", "<*File*>Tables/second.data"}
        };

        var mockFileWrapper = Substitute.For<IFile>();
        mockFileWrapper.Exists(Arg.Any<string>()).Returns(false);
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFileWrapper);
            var errors = TokenHelper.ResolveFileTokens(tokens, basePath, Platform.SqlServer, tolerateErrors: true);
            Assert.That(errors, Has.Count.EqualTo(2), "Both unresolvable tokens must be reported, not just the first.");
            Assert.That(errors, Has.Some.Contains("FirstMissing"));
            Assert.That(errors, Has.Some.Contains("SecondMissing"));
            Assert.That(errors, Has.Some.Contains(Path.Join(basePath, "Tables/first.data")));
            Assert.That(errors, Has.Some.Contains(Path.Join(basePath, "Tables/second.data")));
            // Unresolved tokens are left as their original literal — the caller (PackageLoader)
            // relies on this to keep loading the rest of the package without crashing on a
            // downstream {{Key}} substitution.
            Assert.That(tokens["FirstMissing"], Is.EqualTo("<*File*>Tables/first.data"));
            FactoryContainer.Clear();
        }
    }

    [Test]
    public void ShouldSplitOutQueryTokens()
    {
        var tokens = new Dictionary<string, string>
        {
            {"MainDB", "MainDB"},
            {"SecondaryDB", "SecondaryDB"},
            {"MyTableData", $"{TokenHelper.FileTag}Tables/dbo.MyTable.data"},
            {"MyBinaryData", $"{TokenHelper.BinaryFileTag}Files/MyBinary.dll"},
            {"MyQueryResult", $"{TokenHelper.QueryTag}SELECT 'Stuff'"},
            {"MyQueryFileResult", $"{TokenHelper.QueryFileTag}QueryFiles/MyQueryFile.sql"}
        };

        Assert.That(tokens, Has.Count.EqualTo(6));
        var queryTokens = TokenHelper.SplitOutQueryTokens(tokens);
        Assert.That(tokens, Has.Count.EqualTo(4));
        Assert.That(tokens, Does.Not.ContainKey("MyQueryResult"));
        Assert.That(queryTokens, Has.Count.EqualTo(2));
        Assert.That(queryTokens, Does.ContainKey("MyQueryResult"));
        Assert.That(queryTokens["MyQueryResult"], Is.EqualTo($"{TokenHelper.QueryTag}SELECT 'Stuff'"));
        Assert.That(queryTokens, Does.ContainKey("MyQueryFileResult"));
        Assert.That(queryTokens["MyQueryFileResult"], Is.EqualTo($"{TokenHelper.QueryFileTag}QueryFiles/MyQueryFile.sql"));
    }

    [Test]
    public void ShouldResolveQueryTokens()
    {
        var tokens = new Dictionary<string, string>
        {
            {"MainDB", "MainDB"},
            {"SecondaryDB", "SecondaryDB"}
        };

        var queryTokens = new Dictionary<string, string>
        {
            {"MyQueryResult", TokenHelper.QueryTag + "SELECT '{{MainDB}}'"}
        };

        var mockCmd = Substitute.For<IDbCommand>();
        var mockReader = Substitute.For<IDataReader>();
        mockCmd.ExecuteReader().Returns(mockReader);
        mockReader.Read().Returns(true, false);
        mockReader[0].Returns("MainDB");

        TokenHelper.ResolveQueryTokens(queryTokens, tokens.ToList(), mockCmd, "BasePath", Platform.SqlServer);
        Assert.That(mockCmd.CommandText, Is.EqualTo("SELECT 'MainDB'"));
        Assert.That(queryTokens["MyQueryResult"], Is.EqualTo("MainDB"));
    }

    [Test]
    public void ShouldResolveQueryFileTokens()
    {
        var basePath = "C:/Projects/MyMetadata/Templates/Main";
        var tokens = new Dictionary<string, string>
        {
            {"MainDB", "MainDB"},
            {"SecondaryDB", "SecondaryDB"},
            {"MyQueryResult", $"{TokenHelper.QueryFileTag}QueryFiles/MyQueryFile.sql"}
        };
        var filePath = Path.Combine(basePath, "QueryFiles/MyQueryFile.sql");
        var fileContent = "SELECT '{{SecondaryDB}}'";

        var mockFileWrapper = Substitute.For<IFile>();
        mockFileWrapper.Exists(filePath).Returns(true);
        mockFileWrapper.ReadAllText(filePath).Returns(fileContent);
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register(mockFileWrapper);
            TokenHelper.ResolveFileTokens(tokens, basePath, Platform.SqlServer);
            Assert.That(tokens["MyQueryResult"], Is.EqualTo($"{TokenHelper.QueryTag}{fileContent}"));
            FactoryContainer.Clear();
        }
    }

    [Test]
    public void GetTokensFromString_ReturnsTokensFromScript()
    {
        var script = "SELECT * FROM {{MainDB}}.dbo.{{TableName}} WHERE {{Column}} = 1";
        var tokens = TokenHelper.GetTokensFromString(script);
        Assert.That(tokens, Has.Count.EqualTo(3));
        Assert.That(tokens, Does.Contain("MainDB"));
        Assert.That(tokens, Does.Contain("TableName"));
        Assert.That(tokens, Does.Contain("Column"));
    }

    [Test]
    public void GetTokensFromString_ReturnsEmptyForNoTokens()
    {
        var script = "SELECT * FROM dbo.MyTable";
        var tokens = TokenHelper.GetTokensFromString(script);
        Assert.That(tokens, Is.Empty);
    }

    [Test]
    public void GetTokensFromString_ReturnsEmptyForNullOrEmpty()
    {
        Assert.That(TokenHelper.GetTokensFromString(null), Is.Empty);
        Assert.That(TokenHelper.GetTokensFromString(""), Is.Empty);
    }

    [Test]
    public void GetTokensFromString_IgnoresLongTokens()
    {
        var longToken = new string('A', 101);
        var script = $"SELECT * FROM {{{{{longToken}}}}}";
        var tokens = TokenHelper.GetTokensFromString(script);
        Assert.That(tokens, Is.Empty);
    }

    [Test]
    public void GetTokensFromString_IgnoresTokensWithNewlines()
    {
        var script = "SELECT * FROM {{\nBadToken\n}}";
        var tokens = TokenHelper.GetTokensFromString(script);
        Assert.That(tokens, Is.Empty);
    }

    #region Platform-Specific SpecificTable Tests

    [Test]
    public void ResolveSpecificTableTokens_SqlServer_ResolvesWithSchema()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>dbo.MyTable" }
        };
        var tables = new List<Table>
        {
            CreateSqlServerTable("dbo", "MyTable")
        };

        TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.SqlServer);

        Assert.That(tokens["TableInfo"], Is.Not.Null.And.Not.Empty);
        var jObj = JObject.Parse(tokens["TableInfo"]);
        Assert.That(jObj["Schema"]?.ToString(), Is.EqualTo("dbo"));
        Assert.That(jObj["Name"]?.ToString(), Is.EqualTo("MyTable"));
    }

    [Test]
    public void ResolveSpecificTableTokens_SqlServer_DefaultsToDbo()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>MyTable" }
        };
        var tables = new List<Table>
        {
            CreateSqlServerTable("dbo", "MyTable")
        };

        TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.SqlServer);

        var jObj = JObject.Parse(tokens["TableInfo"]);
        Assert.That(jObj["Schema"]?.ToString(), Is.EqualTo("dbo"));
    }

    [Test]
    public void ResolveSpecificTableTokens_SqlServer_StripsBrackets()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>[dbo].[MyTable]" }
        };
        var tables = new List<Table>
        {
            CreateSqlServerTable("[dbo]", "[MyTable]")
        };

        TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.SqlServer);

        Assert.That(tokens["TableInfo"], Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void ResolveSpecificTableTokens_PostgreSQL_DefaultsToPublic()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>my_table" }
        };
        var tables = new List<Table>
        {
            CreatePostgreSqlTable("public", "my_table")
        };

        TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.PostgreSQL);

        var jObj = JObject.Parse(tokens["TableInfo"]);
        Assert.That(jObj["Schema"]?.ToString(), Is.EqualTo("public"));
    }

    [Test]
    public void ResolveSpecificTableTokens_PostgreSQL_StripsDoubleQuotes()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>\"public\".\"my_table\"" }
        };
        var tables = new List<Table>
        {
            CreatePostgreSqlTable("public", "my_table")
        };

        TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.PostgreSQL);

        Assert.That(tokens["TableInfo"], Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void ResolveSpecificTableTokens_MySQL_MatchesByNameOnly()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>my_table" }
        };
        var tables = new List<Table>
        {
            new MySqlTable { Name = "my_table" }
        };

        TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.MySQL);

        var jObj = JObject.Parse(tokens["TableInfo"]);
        Assert.That(jObj["Name"]?.ToString(), Is.EqualTo("my_table"));
    }

    [Test]
    public void ResolveSpecificTableTokens_MySQL_StripsBackticks()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>`my_table`" }
        };
        var tables = new List<Table>
        {
            new MySqlTable { Name = "my_table" }
        };

        TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.MySQL);

        Assert.That(tokens["TableInfo"], Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void ResolveSpecificTableTokens_ThrowsIfTableNotFound()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>dbo.MissingTable" }
        };
        var tables = new List<Table>
        {
            CreateSqlServerTable("dbo", "MyTable")
        };

        var ex = Assert.Throws<Schema.Domain.PackageException>(() => TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.SqlServer));
        Assert.That(ex.Message, Does.Contain("missing table"));
    }

    [Test]
    public void ResolveSpecificTableTokens_ThrowsIfNoTableNameProvided()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>" }
        };
        var tables = new List<Table>
        {
            CreateSqlServerTable("dbo", "MyTable")
        };

        var ex = Assert.Throws<Schema.Domain.PackageException>(() => TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.SqlServer));
        Assert.That(ex.Message, Does.Contain("No table name was provided"));
    }

    [Test]
    public void ResolveSpecificTableTokens_XmlTag_ResolvesToIngestXml()
    {
        // A2: <*SpecificTableXml*> resolves to the object's ingest XML (not JSON), so a below-cliff
        // SQL Server can shred one table with XQuery.
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTableXml*>dbo.MyTable" }
        };
        var tables = new List<Table> { CreateSqlServerTable("dbo", "MyTable") };

        TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.SqlServer);

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => System.Xml.Linq.XElement.Parse(tokens["TableInfo"]));
            Assert.That(tokens["TableInfo"], Does.StartWith("<Table>"));
            Assert.That(tokens["TableInfo"], Does.Contain("MyTable"));
        });
    }

    [Test]
    public void ResolveSpecificTableTokens_XmlTag_MissingTable_ThrowsSameAsJson()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTableXml*>dbo.MissingTable" }
        };
        var tables = new List<Table> { CreateSqlServerTable("dbo", "MyTable") };

        var ex = Assert.Throws<Schema.Domain.PackageException>(() => TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.SqlServer));
        Assert.That(ex.Message, Does.Contain("missing table"));
    }

    [Test]
    public void ResolveSpecificMaterializedViewTokens_XmlTag_ResolvesToIngestXml()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificMaterializedViewXml*>public.my_view" }
        };
        var views = new List<PostgreSqlMaterializedView>
        {
            new() { Schema = "public", Name = "my_view", Definition = "SELECT 1" }
        };

        TokenHelper.ResolveSpecificMaterializedViewTokens(tokens, views);

        Assert.That(tokens["MyView"], Does.StartWith("<MaterializedView>"));
        Assert.That(tokens["MyView"], Does.Contain("my_view"));
    }

    [Test]
    public void ResolveSpecificIndexedViewTokens_XmlTag_ResolvesToIngestXml()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificIndexedViewXml*>dbo.vOrderSummary" }
        };
        var views = new List<SqlServerIndexedView>
        {
            new() { Schema = "[dbo]", Name = "[vOrderSummary]", Definition = "SELECT 1 AS Col1" }
        };

        TokenHelper.ResolveSpecificIndexedViewTokens(tokens, views);

        Assert.That(tokens["MyView"], Does.StartWith("<IndexedView>"));
        Assert.That(tokens["MyView"], Does.Contain("vOrderSummary"));
    }

    #endregion

    #region SpecificMaterializedView Tests

    [Test]
    public void ResolveSpecificMaterializedViewTokens_FindsViewBySchemaAndName()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificMaterializedView*>person.vstateprovincecountryregion" }
        };
        var views = new List<PostgreSqlMaterializedView>
        {
            new() { Schema = "person", Name = "vstateprovincecountryregion", Definition = "SELECT 1" }
        };
        TokenHelper.ResolveSpecificMaterializedViewTokens(tokens, views);
        Assert.That(tokens["MyView"], Does.Contain("vstateprovincecountryregion"));
    }

    [Test]
    public void ResolveSpecificMaterializedViewTokens_DefaultsToPublicSchema()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificMaterializedView*>my_view" }
        };
        var views = new List<PostgreSqlMaterializedView>
        {
            new() { Schema = "public", Name = "my_view", Definition = "SELECT 1" }
        };
        TokenHelper.ResolveSpecificMaterializedViewTokens(tokens, views);
        Assert.That(tokens["MyView"], Does.Contain("my_view"));
    }

    [Test]
    public void ResolveSpecificMaterializedViewTokens_ThrowsForMissingView()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificMaterializedView*>nonexistent" }
        };
        Assert.Throws<Schema.Domain.PackageException>(() =>
            TokenHelper.ResolveSpecificMaterializedViewTokens(tokens, []));
    }

    [Test]
    public void ResolveSpecificMaterializedViewTokens_ThrowsForEmptyName()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificMaterializedView*>" }
        };
        var ex = Assert.Throws<Schema.Domain.PackageException>(() =>
            TokenHelper.ResolveSpecificMaterializedViewTokens(tokens, []));
        Assert.That(ex.Message, Does.Contain("No view name was provided"));
    }

    [Test]
    public void ResolveSpecificMaterializedViewTokens_StripsDoubleQuotes()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificMaterializedView*>\"person\".\"my_view\"" }
        };
        var views = new List<PostgreSqlMaterializedView>
        {
            new() { Schema = "person", Name = "my_view", Definition = "SELECT 1" }
        };
        TokenHelper.ResolveSpecificMaterializedViewTokens(tokens, views);
        Assert.That(tokens["MyView"], Does.Contain("my_view"));
    }

    [Test]
    public void ResolveSpecificMaterializedViewTokens_SerializesFullView()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificMaterializedView*>public.mv_test" }
        };
        var views = new List<PostgreSqlMaterializedView>
        {
            new()
            {
                Schema = "public",
                Name = "mv_test",
                Definition = "SELECT 1",
                Indexes = [new PostgreSqlIndex { Name = "ix_test", Unique = true, IndexColumns = "id" }]
            }
        };
        TokenHelper.ResolveSpecificMaterializedViewTokens(tokens, views);
        var jObj = JObject.Parse(tokens["MyView"]);
        Assert.That(jObj["Name"]?.ToString(), Is.EqualTo("mv_test"));
        Assert.That(jObj["Indexes"]?.Count(), Is.EqualTo(1));
    }

    [Test]
    public void TagConstants_IncludesSpecificMaterializedViewTag()
    {
        Assert.That(TokenHelper.SpecificMaterializedViewTag, Is.EqualTo("<*SpecificMaterializedView*>"));
    }

    #endregion

    #region SpecificIndexedView Tests

    [Test]
    public void ResolveSpecificIndexedViewTokens_FindsViewBySchemaAndName()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificIndexedView*>dbo.vOrderSummary" }
        };
        var views = new List<SqlServerIndexedView>
        {
            new() { Schema = "[dbo]", Name = "[vOrderSummary]", Definition = "SELECT 1 AS Col1" }
        };
        TokenHelper.ResolveSpecificIndexedViewTokens(tokens, views);
        Assert.That(tokens["MyView"], Does.Contain("vOrderSummary"));
    }

    [Test]
    public void ResolveSpecificIndexedViewTokens_DefaultsToDboSchema()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificIndexedView*>vOrderSummary" }
        };
        var views = new List<SqlServerIndexedView>
        {
            new() { Schema = "[dbo]", Name = "[vOrderSummary]", Definition = "SELECT 1" }
        };
        TokenHelper.ResolveSpecificIndexedViewTokens(tokens, views);
        Assert.That(tokens["MyView"], Does.Contain("vOrderSummary"));
    }

    [Test]
    public void ResolveSpecificIndexedViewTokens_ThrowsForMissingView()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificIndexedView*>dbo.vNonExistent" }
        };
        Assert.Throws<Schema.Domain.PackageException>(() =>
            TokenHelper.ResolveSpecificIndexedViewTokens(tokens, []));
    }

    [Test]
    public void ResolveSpecificIndexedViewTokens_ThrowsForEmptyName()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificIndexedView*>" }
        };
        var ex = Assert.Throws<Schema.Domain.PackageException>(() =>
            TokenHelper.ResolveSpecificIndexedViewTokens(tokens, []));
        Assert.That(ex.Message, Does.Contain("No view name was provided"));
    }

    [Test]
    public void ResolveSpecificIndexedViewTokens_SerializesFullView()
    {
        var tokens = new Dictionary<string, string>
        {
            { "MyView", "<*SpecificIndexedView*>dbo.vTest" }
        };
        var views = new List<SqlServerIndexedView>
        {
            new()
            {
                Schema = "[dbo]", Name = "[vTest]", Definition = "SELECT 1",
                Indexes = [new SqlServerIndex { Name = "[IX_Clustered]", Unique = true, Clustered = true, IndexColumns = "[Col1]" }]
            }
        };
        TokenHelper.ResolveSpecificIndexedViewTokens(tokens, views);
        var jObj = JObject.Parse(tokens["MyView"]);
        Assert.That(jObj["Name"]?.ToString(), Is.EqualTo("[vTest]"));
        Assert.That(jObj["Indexes"]?.Count(), Is.EqualTo(1));
    }

    [Test]
    public void TagConstants_IncludesSpecificIndexedViewTag()
    {
        Assert.That(TokenHelper.SpecificIndexedViewTag, Is.EqualTo("<*SpecificIndexedView*>"));
    }

    #endregion

    #region Platform-Specific DropTempTables Tests

    // The sweep runs before every query token on every supported SQL Server version, but CI's oldest SQL Server is 2017,
    // where STRING_AGG (2017) and DROP TABLE IF EXISTS (2016) both exist, so an integration test cannot see either
    // creep back in. On 2008 R2 to 2016 they failed every query token.
    [Test]
    public void GetDropTempTablesScript_SqlServer_UsesNothingNewerThan2008()
    {
        var script = TokenHelper.GetDropTempTablesScript(Platform.SqlServer).ToUpperInvariant();
        Assert.Multiple(() =>
        {
            Assert.That(script, Does.Not.Contain("STRING_AGG"));
            Assert.That(script, Does.Not.Contain("IF EXISTS"));
        });
    }

    [Test]
    public void GetDropTempTablesScript_SqlServer_ContainsTempdb()
    {
        var script = TokenHelper.GetDropTempTablesScript(Platform.SqlServer);
        Assert.That(script, Does.Contain("tempdb"));
    }

    [Test]
    public void GetDropTempTablesScript_PostgreSQL_ContainsPgCatalog()
    {
        var script = TokenHelper.GetDropTempTablesScript(Platform.PostgreSQL);
        Assert.That(script, Does.Contain("pg_catalog"));
    }

    [Test]
    public void GetDropTempTablesScript_MySQL_IsNoOp()
    {
        var script = TokenHelper.GetDropTempTablesScript(Platform.MySQL);
        Assert.That(script, Is.EqualTo("SELECT 1"));
    }

    #endregion

    #region ResolveQueryTokens Error Handling

    [Test]
    public void ResolveQueryTokens_ThrowsOnError()
    {
        var queryTokens = new Dictionary<string, string>
        {
            {"BadQuery", TokenHelper.QueryTag + "SELECT BAD SYNTAX"}
        };
        var nonQueryTokens = new List<KeyValuePair<string, string>>();

        var mockCmd = Substitute.For<IDbCommand>();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => { }); // DropTempTables succeeds
        mockCmd.ExecuteReader().Returns(_ => throw new Exception("SQL Error"));

        var ex = Assert.Throws<Exception>(() =>
            TokenHelper.ResolveQueryTokens(queryTokens, nonQueryTokens, mockCmd, "BasePath", Platform.SqlServer));
        Assert.That(ex.Message, Does.Contain("Error resolving BadQuery"));
    }

    [Test]
    public void ResolveQueryTokens_HandlesNullValues()
    {
        var queryTokens = new Dictionary<string, string>
        {
            {"NullResult", TokenHelper.QueryTag + "SELECT NULL"}
        };
        var nonQueryTokens = new List<KeyValuePair<string, string>>();

        var mockCmd = Substitute.For<IDbCommand>();
        var mockReader = Substitute.For<IDataReader>();
        mockCmd.ExecuteReader().Returns(mockReader);
        mockReader.Read().Returns(true, false);
        mockReader[0].Returns(DBNull.Value);

        TokenHelper.ResolveQueryTokens(queryTokens, nonQueryTokens, mockCmd, "BasePath", Platform.PostgreSQL);
        Assert.That(queryTokens["NullResult"], Is.EqualTo(""));
    }

    [Test]
    public void ResolveQueryTokens_HandlesMultipleRows()
    {
        var queryTokens = new Dictionary<string, string>
        {
            {"MultiRow", TokenHelper.QueryTag + "SELECT name FROM sys.objects"}
        };
        var nonQueryTokens = new List<KeyValuePair<string, string>>();

        var mockCmd = Substitute.For<IDbCommand>();
        var mockReader = Substitute.For<IDataReader>();
        mockCmd.ExecuteReader().Returns(mockReader);
        mockReader.Read().Returns(true, true, false);
        mockReader[Arg.Any<int>()].Returns("Row1", "Row2");

        TokenHelper.ResolveQueryTokens(queryTokens, nonQueryTokens, mockCmd, "BasePath", Platform.SqlServer);
        // Result contains at least two lines (one per Read()==true)
        var lines = queryTokens["MultiRow"].Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines.Length, Is.GreaterThanOrEqualTo(2));
    }

    #endregion

    [Test]
    public void TagConstants_HaveCorrectValues()
    {
        Assert.That(TokenHelper.QueryTag, Is.EqualTo("<*Query*>"));
        Assert.That(TokenHelper.QueryFileTag, Is.EqualTo("<*QueryFile*>"));
        Assert.That(TokenHelper.FileTag, Is.EqualTo("<*File*>"));
        Assert.That(TokenHelper.BinaryFileTag, Is.EqualTo("<*BinaryFile*>"));
        Assert.That(TokenHelper.SpecificTableTag, Is.EqualTo("<*SpecificTable*>"));
    }

    [Test]
    public void FindTable_SqlServerTable_FindsByTypedSchemaProperty()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>hr.Users" }
        };
        var table = new SqlServerTable { Name = "Users", Schema = "hr" };
        var tables = new List<Table> { table };

        TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.SqlServer);

        var jObj = JObject.Parse(tokens["TableInfo"]);
        Assert.That(jObj["Schema"]?.ToString(), Is.EqualTo("hr"));
        Assert.That(jObj["Name"]?.ToString(), Is.EqualTo("Users"));
    }

    [Test]
    public void FindTable_PostgreSqlTable_FindsByTypedSchemaProperty()
    {
        var tokens = new Dictionary<string, string>
        {
            { "TableInfo", "<*SpecificTable*>auth.users" }
        };
        var table = new PostgreSqlTable { Name = "users", Schema = "auth" };
        var tables = new List<Table> { table };

        TokenHelper.ResolveSpecificTableTokens(tokens, tables, Platform.PostgreSQL);

        var jObj = JObject.Parse(tokens["TableInfo"]);
        Assert.That(jObj["Schema"]?.ToString(), Is.EqualTo("auth"));
        Assert.That(jObj["Name"]?.ToString(), Is.EqualTo("users"));
    }

    private static Table CreateSqlServerTable(string schema, string name)
    {
        return new SqlServerTable { Name = name, Schema = schema };
    }

    private static Table CreatePostgreSqlTable(string schema, string name)
    {
        return new PostgreSqlTable { Name = name, Schema = schema };
    }
}
