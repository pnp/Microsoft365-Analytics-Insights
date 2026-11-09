using Common.Entities;
using Common.Entities.Config;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Infrastructure.Interception;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql;
using static WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents.PageUpdateEventAppInsightsQueryResult;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// The App Insights importer's page-metadata save, and the titles webs are stored with, against a throwaway database
    /// with every migration applied: taxonomy fields, values changed in place, how stored values are read, and what a page
    /// that cannot be saved does to the rest of its chunk.
    /// </summary>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class PageMetadataImportSqlTests
    {
        private static ScratchDatabase _database;
        private static string _databaseName;

        /// <summary>A value the database refuses (see <see cref="CreateMigratedDatabase"/>): a page carrying it cannot be saved.</summary>
        private const string UnsaveableValue = "Contoso unsaveable value";

        private static readonly Guid Finance = new Guid("f5b7ced7-2039-47f9-a22d-32c66d2eec65");
        private static readonly Guid Legal = new Guid("9c8d2e3a-4b5f-4a6e-8d7c-1b2a3c4d5e6f");

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _database = ScratchDatabase.Create("PageMetadata");
            _databaseName = new SqlConnectionStringBuilder(_database.ConnectionString).InitialCatalog;
            new DbMigrator(new Configuration
            {
                TargetDatabase = new DbConnectionInfo(_database.ConnectionString, "Microsoft.Data.SqlClient"),
            }).Update();

            // A real constraint violation (SQL error 547), as a row the database refuses would raise
            _database.Execute("ALTER TABLE dbo.file_metadata_property_values ADD CONSTRAINT CK_test_unsaveable_value " +
                $"CHECK (field_value IS NULL OR field_value <> N'{UnsaveableValue}');");
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            SqlConnection.ClearAllPools();
            _database?.Dispose();
        }

        #region Page metadata

        /// <summary>
        /// A managed-metadata field that a tracker before 1.6.5 sent only as its raw value, "id;#Label|termGuid", is
        /// stored as that text with no term id. Once the tracker sends the term, the stored value must become the term and
        /// stay it. The term's id used to be set only on a new value, so the raw text, which still arrives with the other
        /// properties, was written straight back over the label - and a changed term kept the old one's id.
        /// </summary>
        [TestMethod]
        public async Task TaxonomyField_StoredAsRawText_BecomesTheTerm_AndFollowsAChangedTerm()
        {
            var url = await NewUrl("taxonomy");
            var manager = Manager();
            var financeRaw = "2;#Contoso Finance|" + Finance;
            var legalRaw = "3;#Contoso Legal|" + Legal;

            // A tracker before 1.6.5: the raw value only
            await manager.SaveAll(new[] { Update(url, Props("ContosoDepartment", financeRaw)) });
            Assert.AreEqual((financeRaw, (Guid?)null), (await StoredValues(url))["ContosoDepartment"]);

            // 1.6.5: the raw value and the term
            RefreshWindowPasses(url);
            await manager.SaveAll(new[] { Update(url, Props("ContosoDepartment", financeRaw), Term("ContosoDepartment", Finance, "Contoso Finance")) });
            Assert.AreEqual(("Contoso Finance", (Guid?)Finance), (await StoredValues(url))["ContosoDepartment"],
                "The term's label and id, not the raw text the other properties carry.");

            // The page tagged with another term
            RefreshWindowPasses(url);
            await manager.SaveAll(new[] { Update(url, Props("ContosoDepartment", legalRaw), Term("ContosoDepartment", Legal, "Contoso Legal")) });
            Assert.AreEqual(("Contoso Legal", (Guid?)Legal), (await StoredValues(url))["ContosoDepartment"]);

            // An older tracker again: a stored term is not overwritten with its raw text
            RefreshWindowPasses(url);
            await manager.SaveAll(new[] { Update(url, Props("ContosoDepartment", legalRaw)) });
            var values = await StoredValues(url);
            Assert.AreEqual(("Contoso Legal", (Guid?)Legal), values["ContosoDepartment"]);
            Assert.AreEqual(1, values.Count);
        }

        /// <summary>
        /// Automatic change detection is off while a chunk is saved, and changes are detected once before each page's
        /// save. A value changed in place is saved only because of that, so this reads one back.
        /// </summary>
        [TestMethod]
        public async Task StoredValues_ChangedInPlace_AreSaved_AndNewOnesAdded()
        {
            var url = await NewUrl("in-place");
            var manager = Manager();
            await manager.SaveAll(new[] { Update(url, Props("Title", "Contoso plans", "ContosoOwner", "Contoso IT")) });

            RefreshWindowPasses(url);
            var saved = await manager.SaveAll(new[] { Update(url, Props("Title", "Contoso plans 2027", "ContosoOwner", "Contoso IT", "ContosoRegion", "North")) });

            CollectionAssert.AreEqual(new[] { url.FullUrl }, saved);
            var values = await StoredValues(url);
            Assert.AreEqual("Contoso plans 2027", values["Title"].Value, "A value changed in place must be saved.");
            Assert.AreEqual("Contoso IT", values["ContosoOwner"].Value);
            Assert.AreEqual("North", values["ContosoRegion"].Value);
            Assert.AreEqual(3, values.Count);
            Assert.IsNotNull(await RefreshedOn(url));
        }

        /// <summary>
        /// What a chunk's pages already have stored is read with one query for the chunk. It used to be one query for every
        /// property of every page, and two more per page for its comments and likes: tens of thousands of round trips for a
        /// chunk of 1,000 pages with dozens of properties each.
        /// </summary>
        [TestMethod]
        public async Task StoredValues_CommentsAndLikes_AreReadOnceForAChunk()
        {
            var urls = new List<Url>();
            for (var i = 0; i < 3; i++)
            {
                urls.Add(await NewUrl("batched-" + i));
            }
            var props = Props("Title", "Contoso", "ContosoOwner", "Contoso IT", "ContosoRegion", "North", "ContosoTopic", "Plans");
            var manager = Manager();
            await manager.SaveAll(urls.Select(u => Update(u, props)).ToList());
            urls.ForEach(RefreshWindowPasses);

            var recorder = new CommandRecorder(_databaseName);
            DbInterception.Add(recorder);
            try
            {
                Assert.AreEqual(3, (await manager.SaveAll(urls.Select(u => Update(u, props)).ToList())).Count);
            }
            finally
            {
                DbInterception.Remove(recorder);
            }

            var commands = string.Join(Environment.NewLine + Environment.NewLine, recorder.Commands);
            Assert.AreEqual(1, recorder.Reads("file_metadata_property_values"), commands);
            Assert.AreEqual(1, recorder.Reads("page_comments"), commands);
            Assert.AreEqual(1, recorder.Reads("page_likes"), commands);
        }

        /// <summary>
        /// A page the database refuses fails on its own: the pages around it in its chunk are saved and marked as
        /// refreshed, and it is not, so the tracker's next send for it is read. The chunk used to share one context: once a
        /// page's rows were refused, every later save in the chunk tried them again and failed, the final failure was
        /// logged and swallowed, and every page had already been listed as updated - so pages were marked as refreshed with
        /// their metadata lost.
        /// </summary>
        [TestMethod]
        public async Task APageThatCannotBeSaved_IsNotMarkedRefreshed_AndTheRestOfItsChunkIsSaved()
        {
            // Named so the page that fails is read between the other two, whichever index SQL Server reads them by
            var first = await NewUrl("chunk1");
            var unsaveable = await NewUrl("chunk2");
            var last = await NewUrl("chunk3");
            var like = new UserBasedCustomAIEvent { SharePointId = 1, Email = "reader@contoso.com", Created = DateTime.UtcNow };
            var logger = new CapturingLogger();

            var saved = await Manager(logger).SaveAll(new[]
            {
                Update(first, Props("Title", "Contoso first")),
                Update(unsaveable, Props("Title", "Contoso unsaveable page", "ContosoOwner", UnsaveableValue), like: like),
                Update(last, Props("Title", "Contoso last")),
            });

            CollectionAssert.AreEquivalent(new[] { first.FullUrl, last.FullUrl }, saved);
            Assert.AreEqual("Contoso first", (await StoredValues(first))["Title"].Value);
            Assert.AreEqual("Contoso last", (await StoredValues(last))["Title"].Value);
            Assert.IsNotNull(await RefreshedOn(first));
            Assert.IsNotNull(await RefreshedOn(last));

            Assert.AreEqual(0, (await StoredValues(unsaveable)).Count, "A page's changes are saved together or not at all.");
            Assert.AreEqual(0, Convert.ToInt32(_database.Scalar($"SELECT COUNT(*) FROM dbo.page_likes WHERE url_id = {unsaveable.ID};")));
            Assert.IsNull(await RefreshedOn(unsaveable), "A page whose metadata was not saved must not be marked as refreshed.");

            var log = string.Join(Environment.NewLine, logger.Lines.Select(l => l.Message));
            var errors = logger.Lines.Where(l => l.Level == LogLevel.Error).Select(l => l.Message).ToList();
            Assert.IsTrue(errors.Any(e => e.Contains($"URL id {unsaveable.ID}:") && e.Contains("SqlException 547")), log);
            Assert.IsTrue(errors.Any(e => e.StartsWith("The metadata of 1 page(s) could not be saved")), log);
            Assert.IsFalse(log.Contains(unsaveable.FullUrl), "The URL is tenant content: " + log);
            Assert.IsFalse(log.Contains(UnsaveableValue), "Page values are tenant content: " + log);
        }

        /// <summary>
        /// Comments the tracker sent as something other than a JSON list are reported by URL id. They used to be printed to
        /// the console with the page's address (#853).
        /// </summary>
        [TestMethod]
        public async Task AnUnreadablePageUpdate_IsReportedByUrlId_NotByItsAddress()
        {
            var url = await NewUrl("unreadable");
            var update = Update(url, Props("Title", "Contoso unreadable"));
            update.CustomProperties.CommentsString = "[{ not json";
            var logger = new CapturingLogger();

            var originalOut = Console.Out;
            var console = new System.IO.StringWriter();
            Console.SetOut(console);
            try
            {
                await Manager(logger).SaveAll(new[] { update });
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var log = string.Join(Environment.NewLine, logger.Lines.Select(l => l.Message));
            Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Warning && l.Message.Contains($"URL id {url.ID}:") && l.Message.Contains("CommentsString")), log);
            Assert.IsFalse(log.Contains(url.FullUrl), log);
            Assert.IsFalse(console.ToString().Contains(url.FullUrl), console.ToString());
            Assert.AreEqual("Contoso unreadable", (await StoredValues(url))["Title"].Value, "The rest of the update is still saved.");
        }

        /// <summary>
        /// A transient SQL failure - a dropped connection, a timeout - is not one page's problem. It is thrown, so the
        /// page-update section fails and the day is retried, and no page is marked as refreshed. It used to be logged and
        /// swallowed, and the page marked as refreshed with nothing saved.
        /// </summary>
        [TestMethod]
        public async Task ATransientFailure_IsThrown_AndMarksNothingRefreshed()
        {
            var url = await NewUrl("transient");
            var failure = new FailingWrites(_databaseName, "file_metadata_property_values");
            Exception thrown = null;

            DbInterception.Add(failure);
            try
            {
                await Manager().SaveAll(new[] { Update(url, Props("Title", "Contoso transient")) });
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                DbInterception.Remove(failure);
            }

            Assert.IsNotNull(thrown, "A transient failure must not be swallowed.");
            Assert.IsTrue(TransientSqlRetry.IsTransient(thrown), thrown.ToString());
            Assert.IsNull(await RefreshedOn(url));
            Assert.AreEqual(0, (await StoredValues(url)).Count);
        }

        #endregion

        #region Web titles

        /// <summary>
        /// A new web is stored with a title its own page views report. The title used to be taken from any page view in
        /// the site collection, matched by site URL: a subsite got none, and a site collection could get a subsite's.
        /// </summary>
        [TestMethod]
        public async Task NewWebs_GetTheTitleTheirOwnPageViewsReport()
        {
            var site = $"https://contoso.sharepoint.com/sites/titles-{Guid.NewGuid():N}";
            var subsite = site + "/hr";

            // The subsite's page views first, where a title taken by site URL would come from
            await SaveHits(PageView(site, subsite, "Contoso HR"), PageView(site, subsite, "Contoso HR"), PageView(site, site, "Contoso Intranet"));

            Assert.AreEqual("Contoso Intranet", WebTitle(site));
            Assert.AreEqual("Contoso HR", WebTitle(subsite));
        }

        /// <summary>
        /// A web stored before this keeps whatever title it got first, so a stored title that is missing or differs takes
        /// the one the web's page views report now. A multilingual web reports a title per language and takes the one most
        /// of its page views reported. A web whose page views report no title keeps the one it has.
        /// </summary>
        [TestMethod]
        public async Task StoredWebs_TakeTheTitleTheirPageViewsReportNow()
        {
            var site = $"https://contoso.sharepoint.com/sites/titles-{Guid.NewGuid():N}";
            var subsite = site + "/hr";
            var archive = site + "/archive";

            // As earlier imports stored them: the site collection with its subsite's title, the subsite with none
            _database.Execute($@"
INSERT INTO dbo.sites (url_base) VALUES (N'{site}');
DECLARE @site int = SCOPE_IDENTITY();
INSERT INTO dbo.webs (url_base, site_id, title) VALUES
    (N'{site}', @site, N'Contoso HR'), (N'{subsite}', @site, NULL), (N'{archive}', @site, N'Contoso Archive');");

            await SaveHits(
                PageView(site, site, "Contoso Intranet"),
                PageView(site, site, "Intranet de Contoso"),
                PageView(site, site, "Contoso Intranet"),
                PageView(site, subsite, "Contoso HR"),
                PageView(site, archive, ""));

            Assert.AreEqual("Contoso Intranet", WebTitle(site));
            Assert.AreEqual("Contoso HR", WebTitle(subsite));
            Assert.AreEqual("Contoso Archive", WebTitle(archive));
            Assert.AreEqual(3, Convert.ToInt32(_database.Scalar($"SELECT COUNT(*) FROM dbo.webs WHERE url_base LIKE N'{site}%';")),
                "No web is stored twice.");
        }

        #endregion

        #region Helpers

        private sealed class ScratchContextFactory : IAnalyticsDbContextFactory
        {
            public AnalyticsEntitiesContext Create() => new AnalyticsEntitiesContext(_database.ConnectionString, true, false);
        }

        /// <summary>No cognitive services, so no comment is sent anywhere.</summary>
        private static PageUpdateManager Manager(ILogger logger = null) =>
            new PageUpdateManager(logger ?? new CapturingLogger(), 1000, new AppConfig { CognitiveEndpoint = null }, null, new ScratchContextFactory());

        private static async Task<Url> NewUrl(string name)
        {
            using (var db = new ScratchContextFactory().Create())
            {
                var url = new Url { FullUrl = $"https://contoso.sharepoint.com/sites/metadata/SitePages/{name}-{Guid.NewGuid():N}.aspx" };
                db.urls.Add(url);
                await db.SaveChangesAsync();
                return url;
            }
        }

        private static Dictionary<string, string> Props(params string[] namesAndValues)
        {
            var props = new Dictionary<string, string>();
            for (var i = 0; i < namesAndValues.Length; i += 2)
            {
                props.Add(namesAndValues[i], namesAndValues[i + 1]);
            }
            return props;
        }

        private static TaxonomoyProperty Term(string field, Guid id, string label) => new TaxonomoyProperty { Id = id, Label = label, PropName = field };

        private static PageUpdateEventAppInsightsQueryResult Update(Url url, Dictionary<string, string> props, TaxonomoyProperty term = null,
            UserBasedCustomAIEvent like = null) => new PageUpdateEventAppInsightsQueryResult
            {
                CustomProperties = new PageUpdateEventCustomProps
                {
                    Url = url.FullUrl,
                    PropsString = JsonConvert.SerializeObject(props),
                    TaxonomyPropsString = JsonConvert.SerializeObject(term == null ? new TaxonomoyProperty[0] : new[] { term }),
                    LikesString = JsonConvert.SerializeObject(like == null ? new UserBasedCustomAIEvent[0] : new[] { like }),
                },
                AppInsightsTimestamp = DateTime.UtcNow,
            };

        /// <summary>The page's stored values by field name.</summary>
        private static async Task<Dictionary<string, (string Value, Guid? Term)>> StoredValues(Url url)
        {
            using (var db = new ScratchContextFactory().Create())
            {
                var rows = await db.FileMetadataPropertyValues.Where(v => v.UrlId == url.ID)
                    .Select(v => new { v.Field.Name, v.FieldValue, v.TagGuid })
                    .ToListAsync();
                return rows.ToDictionary(r => r.Name, r => (r.FieldValue, r.TagGuid));
            }
        }

        private static async Task<DateTime?> RefreshedOn(Url url)
        {
            using (var db = new ScratchContextFactory().Create())
            {
                return await db.urls.Where(u => u.ID == url.ID).Select(u => u.MetadataLastRefreshed).SingleAsync();
            }
        }

        /// <summary>The page's refresh window has passed, so its metadata is read again.</summary>
        private static void RefreshWindowPasses(Url url) => _database.Execute($"UPDATE dbo.urls SET file_last_refreshed = NULL WHERE id = {url.ID};");

        private static PageViewAppInsightsQueryResult PageView(string siteUrl, string webUrl, string webTitle) => new PageViewAppInsightsQueryResult
        {
            Url = webUrl + "/SitePages/Home.aspx",
            CustomProperties = new PageViewCustomProps
            {
                PageRequestId = Guid.NewGuid(),
                SessionId = Guid.NewGuid().ToString(),
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                WebTitle = webTitle,
                PageTitle = "Home",
            },
            AppInsightsTimestamp = DateTime.UtcNow,
            Browser = "Edge",
            DeviceModel = "PC",
            ClientOS = "Windows",
            Username = "reader@contoso.com",
        };

        private static async Task SaveHits(params PageViewAppInsightsQueryResult[] pageViews)
        {
            var collection = new PageViewCollection();
            collection.Rows.AddRange(pageViews);
            using (var db = new ScratchContextFactory().Create())
            {
                await collection.SaveToSQL(db, new CapturingLogger());
            }
        }

        private static string WebTitle(string webUrl) => (string)_database.Scalar($"SELECT title FROM dbo.webs WHERE url_base = N'{webUrl}';");

        /// <summary>Records the commands EF sends to this class's database.</summary>
        private sealed class CommandRecorder : DbCommandInterceptor
        {
            private readonly string _database;

            public CommandRecorder(string database)
            {
                _database = database;
            }

            public List<string> Commands { get; } = new List<string>();

            /// <summary>The SELECTs that read the table. EF puts one space or two after FROM, depending on the query's shape.</summary>
            public int Reads(string table) => Commands.Count(c => c.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(c, $@"FROM\s+\[dbo\]\.\[{table}\]", RegexOptions.IgnoreCase));

            public override void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext) => Record(command);

            public override void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) => Record(command);

            public override void ScalarExecuting(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) => Record(command);

            private void Record(DbCommand command)
            {
                if (command.Connection?.Database == _database)
                {
                    Commands.Add(command.CommandText);
                }
            }
        }

        /// <summary>
        /// Fails EF's writes to one table of this class's database, without running them, as SqlClient fails a command on
        /// a connection the server dropped. Not a timeout: EF's execution strategy would retry that for half a minute first.
        /// </summary>
        private sealed class FailingWrites : DbCommandInterceptor
        {
            private readonly string _database;
            private readonly string _table;

            public FailingWrites(string database, string table)
            {
                _database = database;
                _table = table;
            }

            public override void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext)
            {
                if (IsWrite(command)) interceptionContext.Exception = BrokenConnection();
            }

            public override void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext)
            {
                if (IsWrite(command)) interceptionContext.Exception = BrokenConnection();
            }

            private static Exception BrokenConnection() => new InvalidOperationException(
                "The connection is broken and recovery is not possible. The connection is marked by the server as unrecoverable.");

            private bool IsWrite(DbCommand command)
            {
                var text = command.CommandText.TrimStart();
                return command.Connection?.Database == _database
                    && (text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                    && text.IndexOf($"[dbo].[{_table}]", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        #endregion
    }
}
