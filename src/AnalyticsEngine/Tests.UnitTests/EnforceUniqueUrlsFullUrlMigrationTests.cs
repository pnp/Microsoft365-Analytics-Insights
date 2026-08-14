using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data;
using System.Data.SqlClient;

namespace Tests.UnitTests
{
    [TestClass]
    public class EnforceUniqueUrlsFullUrlMigrationTests
    {
        private const string GreekUrl = "https://contoso.sharepoint.com/sites/example/Καλημέρα";

        [TestMethod]
        public void Up_CollapsesDuplicatesRepointsEveryReferenceAndCreatesUniqueIndex()
        {
            using (var db = ScratchDatabase.Create("UniqueUrls"))
            {
                CreateCompleteSchema(db);
                SeedDuplicateData(db);

                db.Execute(EnforceUniqueUrlsFullUrl.Up_Sql);

                var canonicalId = Scalar<int>(
                    db,
                    "SELECT id FROM dbo.urls WHERE full_url = @value",
                    new SqlParameter("@value", SqlDbType.NVarChar, 850) { Value = GreekUrl });

                Assert.AreEqual(1, Scalar<int>(
                    db,
                    "SELECT COUNT(*) FROM dbo.urls WHERE full_url = @value",
                    new SqlParameter("@value", SqlDbType.NVarChar, 850) { Value = GreekUrl }));
                Assert.AreEqual(GreekUrl, Scalar<string>(
                    db,
                    "SELECT full_url FROM dbo.urls WHERE id = @id",
                    new SqlParameter("@id", canonicalId)));

                AssertAllReferencesUseCanonicalUrl(db, canonicalId);

                Assert.AreEqual(2, Scalar<int>(db, "SELECT COUNT(*) FROM dbo.file_metadata_property_values"));
                Assert.AreEqual(1, Scalar<int>(
                    db,
                    @"SELECT COUNT(*) FROM dbo.file_metadata_property_values
                      WHERE url_id = @id AND field_id = 10 AND updated = '2026-02-01'",
                    new SqlParameter("@id", canonicalId)));
                Assert.AreEqual(2, Scalar<int>(db, "SELECT COUNT(*) FROM dbo.hits_clicked_elements"));

                Assert.AreEqual(1, Scalar<int>(
                    db,
                    @"SELECT COUNT(*) FROM sys.indexes
                      WHERE object_id = OBJECT_ID(N'dbo.urls')
                        AND name = N'IX_urls_full_url'
                        AND is_unique = 1
                        AND ignore_dup_key = 1"));

                // IGNORE_DUP_KEY rejects the racing duplicate without aborting the whole statement.
                db.Execute("INSERT INTO dbo.urls(full_url) VALUES (N'https://contoso.sharepoint.com/sites/example/Καλημέρα');");
                Assert.AreEqual(1, Scalar<int>(
                    db,
                    "SELECT COUNT(*) FROM dbo.urls WHERE full_url = @value",
                    new SqlParameter("@value", SqlDbType.NVarChar, 850) { Value = GreekUrl }));

                db.Execute(EnforceUniqueUrlsFullUrl.Up_Sql);
                Assert.AreEqual(1, Scalar<int>(
                    db,
                    "SELECT COUNT(*) FROM dbo.urls WHERE full_url = @value",
                    new SqlParameter("@value", SqlDbType.NVarChar, 850) { Value = GreekUrl }));
                AssertAllReferencesUseCanonicalUrl(db, canonicalId);
            }
        }

        [TestMethod]
        public void Up_UnknownForeignKeyStopsBeforeDeletingData()
        {
            using (var db = ScratchDatabase.Create("UniqueUrlsUnknownFk"))
            {
                db.Execute(@"
CREATE TABLE dbo.urls
(
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    full_url nvarchar(850) NOT NULL
);
CREATE NONCLUSTERED INDEX IX_urls_full_url ON dbo.urls(full_url);
CREATE TABLE dbo.future_url_reference
(
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    url_id int NOT NULL,
    CONSTRAINT FK_future_url_reference_urls
        FOREIGN KEY (url_id) REFERENCES dbo.urls(id)
);
INSERT INTO dbo.urls(full_url) VALUES (N'https://contoso.example/duplicate'), (N'https://contoso.example/duplicate');
INSERT INTO dbo.future_url_reference(url_id) VALUES (2);");

                Assert.ThrowsException<SqlException>(() => db.Execute(EnforceUniqueUrlsFullUrl.Up_Sql));
                Assert.AreEqual(2, Scalar<int>(db, "SELECT COUNT(*) FROM dbo.urls"));
                Assert.AreEqual(2, Scalar<int>(db, "SELECT url_id FROM dbo.future_url_reference"));
                Assert.AreEqual(0, Scalar<int>(
                    db,
                    @"SELECT COUNT(*) FROM sys.indexes
                      WHERE object_id = OBJECT_ID(N'dbo.urls')
                        AND name = N'IX_urls_full_url'
                        AND is_unique = 1"));
            }
        }

        [TestMethod]
        public void Down_RevertsIndexToNonUnique()
        {
            using (var db = ScratchDatabase.Create("UniqueUrlsDown"))
            {
                db.Execute(@"
CREATE TABLE dbo.urls
(
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    full_url nvarchar(850) NOT NULL
);
CREATE UNIQUE NONCLUSTERED INDEX IX_urls_full_url
ON dbo.urls(full_url) WITH (IGNORE_DUP_KEY = ON);");

                db.Execute(EnforceUniqueUrlsFullUrl.Down_Sql);

                Assert.AreEqual(1, Scalar<int>(
                    db,
                    @"SELECT COUNT(*) FROM sys.indexes
                      WHERE object_id = OBJECT_ID(N'dbo.urls')
                        AND name = N'IX_urls_full_url'
                        AND is_unique = 0"));
            }
        }

        private static void CreateCompleteSchema(ScratchDatabase db)
        {
            db.Execute(@"
CREATE TABLE dbo.urls
(
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    full_url nvarchar(850) NOT NULL
);
CREATE NONCLUSTERED INDEX IX_urls_full_url ON dbo.urls(full_url);

CREATE TABLE dbo.hits
(
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    url_id int NOT NULL,
    CONSTRAINT FK_hits_urls FOREIGN KEY (url_id) REFERENCES dbo.urls(id)
);
CREATE INDEX IX_hits_url_id ON dbo.hits(url_id);

CREATE TABLE dbo.copilot_event_files
(
    copilot_chat_id uniqueidentifier NOT NULL PRIMARY KEY,
    url_id int NOT NULL,
    CONSTRAINT FK_copilot_event_files_urls
        FOREIGN KEY (url_id) REFERENCES dbo.urls(id) ON DELETE CASCADE
);
CREATE INDEX IX_copilot_event_files_url_id ON dbo.copilot_event_files(url_id);

CREATE TABLE dbo.event_copilot_files
(
    copilot_chat_id uniqueidentifier NOT NULL PRIMARY KEY,
    url_id int NOT NULL,
    CONSTRAINT FK_event_copilot_files_urls
        FOREIGN KEY (url_id) REFERENCES dbo.urls(id) ON DELETE CASCADE
);
CREATE INDEX IX_event_copilot_files_url_id ON dbo.event_copilot_files(url_id);

CREATE TABLE dbo.file_metadata_property_values
(
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    url_id int NOT NULL,
    field_id int NOT NULL,
    updated datetime2 NULL,
    CONSTRAINT FK_file_metadata_property_values_urls
        FOREIGN KEY (url_id) REFERENCES dbo.urls(id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX IX_url_id_field_id
ON dbo.file_metadata_property_values(url_id, field_id);

CREATE TABLE dbo.hits_clicked_elements
(
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    hit_id int NOT NULL,
    url_id int NULL,
    [timestamp] datetime NOT NULL,
    CONSTRAINT FK_hits_clicked_elements_urls
        FOREIGN KEY (url_id) REFERENCES dbo.urls(id)
);
CREATE UNIQUE INDEX IX_hit_id_url_id_timestamp
ON dbo.hits_clicked_elements(hit_id, url_id, [timestamp]);

CREATE TABLE dbo.page_comments
(
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    url_id int NOT NULL,
    CONSTRAINT FK_page_comments_urls
        FOREIGN KEY (url_id) REFERENCES dbo.urls(id) ON DELETE CASCADE
);
CREATE INDEX IX_page_comments_url_id ON dbo.page_comments(url_id);

CREATE TABLE dbo.page_likes
(
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    url_id int NOT NULL,
    CONSTRAINT FK_page_likes_urls
        FOREIGN KEY (url_id) REFERENCES dbo.urls(id) ON DELETE CASCADE
);
CREATE INDEX IX_page_likes_url_id ON dbo.page_likes(url_id);

CREATE TABLE dbo.event_meta_sharepoint
(
    event_id uniqueidentifier NOT NULL PRIMARY KEY,
    url_id int NULL
);

CREATE TABLE dbo.audit_events
(
    id uniqueidentifier NOT NULL PRIMARY KEY,
    url_id int NULL,
    CONSTRAINT FK_audit_events_urls FOREIGN KEY (url_id) REFERENCES dbo.urls(id)
);");
        }

        private static void SeedDuplicateData(ScratchDatabase db)
        {
            db.Execute(@"
INSERT INTO dbo.urls(full_url)
VALUES
    (N'https://contoso.sharepoint.com/sites/example/Καλημέρα'),
    (N'https://contoso.sharepoint.com/sites/EXAMPLE/Καλημέρα'),
    (N'https://contoso.sharepoint.com/sites/example/other');

INSERT INTO dbo.hits(url_id) VALUES (2);
INSERT INTO dbo.copilot_event_files(copilot_chat_id, url_id) VALUES (NEWID(), 2);
INSERT INTO dbo.event_copilot_files(copilot_chat_id, url_id) VALUES (NEWID(), 2);

INSERT INTO dbo.file_metadata_property_values(url_id, field_id, updated)
VALUES
    (1, 10, '2026-01-01'),
    (2, 10, '2026-02-01'),
    (2, 11, '2026-03-01');

INSERT INTO dbo.hits_clicked_elements(hit_id, url_id, [timestamp])
VALUES
    (100, 1, '2026-01-01T10:00:00'),
    (100, 2, '2026-01-01T10:00:00'),
    (101, 2, '2026-01-01T10:00:00');

INSERT INTO dbo.page_comments(url_id) VALUES (2);
INSERT INTO dbo.page_likes(url_id) VALUES (2);
INSERT INTO dbo.event_meta_sharepoint(event_id, url_id) VALUES (NEWID(), 2);
INSERT INTO dbo.audit_events(id, url_id) VALUES (NEWID(), 2);");
        }

        private static void AssertAllReferencesUseCanonicalUrl(ScratchDatabase db, int canonicalId)
        {
            var tables = new[]
            {
                "hits",
                "copilot_event_files",
                "event_copilot_files",
                "file_metadata_property_values",
                "hits_clicked_elements",
                "page_comments",
                "page_likes",
                "event_meta_sharepoint",
                "audit_events",
            };

            foreach (var table in tables)
            {
                Assert.AreEqual(
                    0,
                    Scalar<int>(
                        db,
                        $"SELECT COUNT(*) FROM dbo.[{table}] WHERE url_id <> @id",
                        new SqlParameter("@id", canonicalId)),
                    $"All dbo.{table} rows should reference the canonical URL.");
            }
        }

        private static T Scalar<T>(ScratchDatabase db, string sql, params SqlParameter[] parameters)
        {
            using (var connection = new SqlConnection(db.ConnectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
                {
                    command.Parameters.AddRange(parameters);
                    var result = command.ExecuteScalar();
                    return (result == null || result == DBNull.Value) ? default(T) : (T)result;
                }
            }
        }
    }
}
