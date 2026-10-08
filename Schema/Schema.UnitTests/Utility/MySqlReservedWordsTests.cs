// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class MySqlReservedWordsTests
{
    [Test]
    [TestCase("SELECT")]
    [TestCase("select")]
    [TestCase("INSERT")]
    [TestCase("TABLE")]
    [TestCase("FROM")]
    [TestCase("WHERE")]
    [TestCase("intersect")]
    [TestCase("QUALIFY")]
    [TestCase("TABLESAMPLE")]
    [TestCase("MASTER_BIND")]
    public void IsReserved_ReturnsTrue_ForReservedWords(string word)
    {
        Assert.That(MySqlReservedWords.IsReserved(word), Is.True);
    }

    [Test]
    [TestCase("Customers")]
    [TestCase("my_table")]
    [TestCase("OrderDetails")]
    public void IsReserved_ReturnsFalse_ForNonReservedWords(string word)
    {
        Assert.That(MySqlReservedWords.IsReserved(word), Is.False);
    }

    [Test]
    public void IsReserved_ReturnsFalse_ForNullOrEmpty()
    {
        Assert.That(MySqlReservedWords.IsReserved(null), Is.False);
        Assert.That(MySqlReservedWords.IsReserved(""), Is.False);
    }

    [Test]
    public void QuoteIfNeeded_QuotesReservedWord()
    {
        Assert.That(MySqlReservedWords.QuoteIfNeeded("SELECT"), Is.EqualTo("`SELECT`"));
    }

    [Test]
    public void QuoteIfNeeded_DoesNotQuoteNonReservedWord()
    {
        Assert.That(MySqlReservedWords.QuoteIfNeeded("Customers"), Is.EqualTo("Customers"));
    }

    [Test]
    public void QuoteIfNeeded_DoesNotDoubleQuote()
    {
        Assert.That(MySqlReservedWords.QuoteIfNeeded("`SELECT`"), Is.EqualTo("`SELECT`"));
    }

    [Test]
    public void QuoteIfNeeded_QuotesIdentifierStartingWithDigit()
    {
        Assert.That(MySqlReservedWords.QuoteIfNeeded("1table"), Is.EqualTo("`1table`"));
    }

    [Test]
    public void QuoteIfNeeded_QuotesIdentifierWithSpecialChars()
    {
        Assert.That(MySqlReservedWords.QuoteIfNeeded("my-table"), Is.EqualTo("`my-table`"));
    }

    [Test]
    public void Quote_AlwaysQuotes()
    {
        Assert.That(MySqlReservedWords.Quote("Customers"), Is.EqualTo("`Customers`"));
    }

    [Test]
    public void Quote_EscapesEmbeddedBackticks()
    {
        Assert.That(MySqlReservedWords.Quote("my`table"), Is.EqualTo("`my``table`"));
    }

    [Test]
    public void Quote_HandlesNull()
    {
        Assert.That(MySqlReservedWords.Quote(null), Is.EqualTo("``"));
    }

    [Test]
    public void Quote_HandlesEmpty()
    {
        Assert.That(MySqlReservedWords.Quote(""), Is.EqualTo("``"));
    }

    [Test]
    public void Unquote_RemovesBackticks()
    {
        Assert.That(MySqlReservedWords.Unquote("`Customers`"), Is.EqualTo("Customers"));
    }

    [Test]
    public void Unquote_UnescapesDoubleBackticks()
    {
        Assert.That(MySqlReservedWords.Unquote("`my``table`"), Is.EqualTo("my`table"));
    }

    [Test]
    public void Unquote_ReturnsAsIs_WhenNotQuoted()
    {
        Assert.That(MySqlReservedWords.Unquote("Customers"), Is.EqualTo("Customers"));
    }

    [Test]
    public void Unquote_HandlesNull()
    {
        Assert.That(MySqlReservedWords.Unquote(null), Is.Null);
    }

    [Test]
    public void UnquoteList_UnquotesEachIdentifier()
    {
        Assert.That(MySqlReservedWords.UnquoteList("`a`,`b`,`c`"), Is.EqualTo("a,b,c"));
    }

    [Test]
    public void QuoteList_QuotesEachIdentifier()
    {
        Assert.That(MySqlReservedWords.QuoteList("a,b,c"), Is.EqualTo("`a`,`b`,`c`"));
    }

    [Test]
    public void QuoteList_HandlesNull()
    {
        Assert.That(MySqlReservedWords.QuoteList(null), Is.Null);
    }

    [Test]
    public void UnquoteList_HandlesNull()
    {
        Assert.That(MySqlReservedWords.UnquoteList(null), Is.Null);
    }

    [Test]
    public void QuoteIfNeeded_ReturnsNull_ForNullInput()
    {
        Assert.That(MySqlReservedWords.QuoteIfNeeded(null), Is.Null);
    }

    [Test]
    public void QuoteIfNeeded_ReturnsEmpty_ForEmptyInput()
    {
        Assert.That(MySqlReservedWords.QuoteIfNeeded(""), Is.EqualTo(""));
    }
}
