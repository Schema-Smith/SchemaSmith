-- The database you inherited: built by hand, never by a package.
CREATE TYPE dbo.ShortChar FROM varchar(10) NULL;
GO
CREATE XML SCHEMA COLLECTION dbo.InvoiceSchema AS N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="invoice"><xs:complexType><xs:sequence><xs:element name="total" type="xs:decimal"/></xs:sequence></xs:complexType></xs:element></xs:schema>';
GO
CREATE TABLE dbo.account (
  account_id  int NOT NULL PRIMARY KEY,
  login_name  varchar(40) COLLATE Latin1_General_CS_AS NOT NULL,
  region_code dbo.ShortChar,
  invoice     xml(DOCUMENT dbo.InvoiceSchema) NULL
);
INSERT INTO dbo.account VALUES (1, 'Alice', 'NE', N'<invoice><total>10.50</total></invoice>'), (2, 'alice', 'SW', NULL);
