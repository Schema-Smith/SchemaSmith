// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using System.Collections.Generic;

using NUnit.Framework;
using Schema.Utility;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// Shared helper-function integration tests for the MySQL/MariaDb family. The MySQL and MariaDb
/// subclasses supply the platform + fixture accessors; every [Test] body here runs on both engines.
/// </summary>
public abstract class HelperFunctionsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection _connection;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();

        // ForgeKindler is already deployed by FixtureSetup
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    #region QuoteIdentifier Tests

    [Test]
    public void QuoteIdentifier_SimpleIdentifier_AddsBackticks()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_QuoteIdentifier('table_name')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("`table_name`"));
    }

    [Test]
    public void QuoteIdentifier_AlreadyQuoted_ReturnsQuoted()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_QuoteIdentifier('`table_name`')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("`table_name`"));
    }

    [Test]
    public void QuoteIdentifier_WithEmbeddedBacktick_EscapesBacktick()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_QuoteIdentifier('table`name')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("`table``name`"));
    }

    [Test]
    public void QuoteIdentifier_ReservedWord_AddsBackticks()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_QuoteIdentifier('order')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("`order`"));
    }

    [Test]
    public void QuoteIdentifier_WithSpaces_AddsBackticks()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_QuoteIdentifier('table name')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("`table name`"));
    }

    #endregion

    #region StripBacktickWrapping Tests

    [Test]
    public void StripBacktickWrapping_QuotedIdentifier_RemovesBackticks()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_StripBacktickWrapping('`table_name`')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("table_name"));
    }

    [Test]
    public void StripBacktickWrapping_UnquotedIdentifier_ReturnsAsIs()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_StripBacktickWrapping('table_name')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("table_name"));
    }

    [Test]
    public void StripBacktickWrapping_WithEscapedBacktick_UnescapesBacktick()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_StripBacktickWrapping('`table``name`')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("table`name"));
    }

    #endregion

    #region SafeBacktickWrap Tests

    [Test]
    public void SafeBacktickWrap_WithWhitespace_TrimsAndQuotes()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_SafeBacktickWrap('  table_name  ')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("`table_name`"));
    }

    [Test]
    public void SafeBacktickWrap_SimpleIdentifier_AddsBackticks()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SchemaSmith_SafeBacktickWrap('column_name')";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("`column_name`"));
    }

    #endregion

    #region ProductOwnership Table Tests

    [Test]
    public void ProductOwnership_TableExists()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{MainDb}'
            AND TABLE_NAME = 'SchemaSmith_ProductOwnership'";
        var result = command.ExecuteScalar();

        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), CatalogName.Matches(_connection, "SchemaSmith_ProductOwnership"));
    }

    [Test]
    public void ProductOwnership_HasExpectedColumns()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{MainDb}'
            AND TABLE_NAME = 'SchemaSmith_ProductOwnership'
            ORDER BY ORDINAL_POSITION";

        var columns = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                columns.Add(reader.GetString(0));
        }

        Assert.That(columns, Does.Contain("Id"));
        Assert.That(columns, Does.Contain("ObjectType"));
        Assert.That(columns, Does.Contain("ObjectSchema"));
        Assert.That(columns, Does.Contain("ObjectName"));
        Assert.That(columns, Does.Contain("ProductName"));
        Assert.That(columns, Does.Contain("TemplateName"));
        Assert.That(columns, Does.Contain("CreatedAt"));
        Assert.That(columns, Does.Contain("UpdatedAt"));
    }

    #endregion

    #region Name key Tests

    // Columns, indexes and constraints compare case-insensitively on every lower_case_table_names setting, so their key
    // folds unconditionally. The binary collation makes the comparison at each call site the key's, not the column's.
    [TestCase("CustomerName", "customername")]
    [TestCase("customername", "customername")]
    [TestCase("IX_Orders_Date", "ix_orders_date")]
    public void NameKeyCI_FoldsOnEveryServer(string name, string expected)
    {
        Assert.That(Scalar($"SELECT SchemaSmith_NameKeyCI('{name}')"), Is.EqualTo(expected));
    }

    [Test]
    public void NameKeyCI_AndIdentifierKey_ReturnABinaryCollation()
    {
        Assert.That(Scalar("SELECT COLLATION(SchemaSmith_NameKeyCI('x'))"), Is.EqualTo("utf8mb4_bin"));
        Assert.That(Scalar("SELECT COLLATION(SchemaSmith_IdentifierKey('x'))"), Is.EqualTo("utf8mb4_bin"));
    }

    // Database, table and view names are folded by the server only when lower_case_table_names >= 1; on 0 two
    // spellings are two objects.
    [Test]
    public void IdentifierKey_FoldsOnlyWhenTheServerFoldsTableNames()
    {
        var expected = Scalar("SELECT @@lower_case_table_names") == "0" ? "MixedCase" : "mixedcase";
        Assert.That(Scalar("SELECT SchemaSmith_IdentifierKey('MixedCase')"), Is.EqualTo(expected));
    }

    private string Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    #endregion

    #region Roundtrip Tests

    [Test]
    public void QuoteAndStrip_Roundtrip_PreservesIdentifier()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT SchemaSmith_StripBacktickWrapping(
                SchemaSmith_QuoteIdentifier('my_table')
            )";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("my_table"));
    }

    [Test]
    public void QuoteAndStrip_RoundtripWithSpecialChars_PreservesIdentifier()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT SchemaSmith_StripBacktickWrapping(
                SchemaSmith_QuoteIdentifier('my`special`table')
            )";
        var result = command.ExecuteScalar()?.ToString();

        Assert.That(result, Is.EqualTo("my`special`table"));
    }

    #endregion
}
