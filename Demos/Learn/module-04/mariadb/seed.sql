-- Module 4 seed (MariaDB): a small "existing" music-catalog database that nobody
-- has under source control yet. Plain DDL + a little data — NOT a SchemaSmith
-- package. You'll point SchemaTongs at it and cast it into one.
-- A trimmed-down slice of the classic Chinook sample schema.

CREATE DATABASE IF NOT EXISTS chinook;
USE chinook;

CREATE TABLE Artist (
    ArtistId INT NOT NULL AUTO_INCREMENT,
    Name VARCHAR(120) NULL,
    CONSTRAINT PK_Artist PRIMARY KEY (ArtistId)
);

CREATE TABLE Genre (
    GenreId INT NOT NULL AUTO_INCREMENT,
    Name VARCHAR(120) NULL,
    CONSTRAINT PK_Genre PRIMARY KEY (GenreId)
);

CREATE TABLE MediaType (
    MediaTypeId INT NOT NULL AUTO_INCREMENT,
    Name VARCHAR(120) NULL,
    CONSTRAINT PK_MediaType PRIMARY KEY (MediaTypeId)
);

CREATE TABLE Album (
    AlbumId INT NOT NULL AUTO_INCREMENT,
    Title VARCHAR(160) NOT NULL,
    ArtistId INT NOT NULL,
    CONSTRAINT PK_Album PRIMARY KEY (AlbumId),
    CONSTRAINT FK_Album_Artist FOREIGN KEY (ArtistId) REFERENCES Artist(ArtistId)
);

CREATE TABLE Track (
    TrackId INT NOT NULL AUTO_INCREMENT,
    Name VARCHAR(200) NOT NULL,
    AlbumId INT NULL,
    MediaTypeId INT NOT NULL,
    GenreId INT NULL,
    Composer VARCHAR(220) NULL,
    Milliseconds INT NOT NULL,
    UnitPrice DECIMAL(10,2) NOT NULL,
    CONSTRAINT PK_Track PRIMARY KEY (TrackId),
    CONSTRAINT FK_Track_Album FOREIGN KEY (AlbumId) REFERENCES Album(AlbumId),
    CONSTRAINT FK_Track_MediaType FOREIGN KEY (MediaTypeId) REFERENCES MediaType(MediaTypeId),
    CONSTRAINT FK_Track_Genre FOREIGN KEY (GenreId) REFERENCES Genre(GenreId)
);

CREATE TABLE Playlist (
    PlaylistId INT NOT NULL AUTO_INCREMENT,
    Name VARCHAR(120) NULL,
    CONSTRAINT PK_Playlist PRIMARY KEY (PlaylistId)
);

CREATE TABLE PlaylistTrack (
    PlaylistId INT NOT NULL,
    TrackId INT NOT NULL,
    CONSTRAINT PK_PlaylistTrack PRIMARY KEY (PlaylistId, TrackId),
    CONSTRAINT FK_PlaylistTrack_Playlist FOREIGN KEY (PlaylistId) REFERENCES Playlist(PlaylistId),
    CONSTRAINT FK_PlaylistTrack_Track FOREIGN KEY (TrackId) REFERENCES Track(TrackId)
);

INSERT INTO Genre (Name) VALUES ('Rock'), ('Jazz'), ('Metal');
INSERT INTO MediaType (Name) VALUES ('MPEG audio file'), ('AAC audio file');
INSERT INTO Artist (Name) VALUES ('AC/DC'), ('Accept'), ('Aerosmith'), ('Miles Davis'), ('Metallica');
INSERT INTO Album (Title, ArtistId) VALUES
    ('For Those About To Rock We Salute You', 1),
    ('Balls to the Wall', 2),
    ('Big Ones', 3),
    ('Kind of Blue', 4),
    ('Master of Puppets', 5);
INSERT INTO Track (Name, AlbumId, MediaTypeId, GenreId, Composer, Milliseconds, UnitPrice) VALUES
    ('For Those About To Rock (We Salute You)', 1, 1, 1, 'Young, Young, Johnson', 343719, 0.99),
    ('Balls to the Wall', 2, 1, 3, NULL, 342562, 0.99),
    ('So What', 4, 1, 2, 'Miles Davis', 562000, 0.99),
    ('Master of Puppets', 5, 1, 3, 'Hetfield, Ulrich, Burton, Hammett', 515000, 0.99);
INSERT INTO Playlist (Name) VALUES ('My favorites');
INSERT INTO PlaylistTrack (PlaylistId, TrackId) VALUES (1, 1), (1, 4);
