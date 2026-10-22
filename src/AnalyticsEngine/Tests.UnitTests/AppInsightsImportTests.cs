using Azure;
using Azure.AI.TextAnalytics;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.Properties;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.PageUpdates;
using WebJob.AppInsightsImporter.Engine.Sql;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;
using static WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents.PageUpdateEventAppInsightsQueryResult;
using User = Common.Entities.User;

namespace Tests.UnitTests
{
    [TestClass]
    public class AppInsightsImportTests
    {

        /// <summary>
        /// Make sure an event with timestamp in custom props gets that timestamp
        /// </summary>
        [TestMethod]
        public void EventTimestampFromCustomProps()
        {
            var today = DateTime.Now;
            var yesterday = DateTime.Now.AddDays(-1);

            var eventWithTimestampInCustomProps = new ClickEventAppInsightsQueryResult
            {
                CustomProperties = new ClickCustomProps
                {
                    EventTimestamp = yesterday
                },
                AppInsightsTimestamp = today
            };
            Assert.IsTrue(eventWithTimestampInCustomProps.Timestamp == yesterday);


            var eventWithNoTimestampInCustomProps = new ClickEventAppInsightsQueryResult
            {
                AppInsightsTimestamp = today
            };
            Assert.IsTrue(eventWithNoTimestampInCustomProps.Timestamp == today);
        }

        /// <summary>
        /// Check updates with PageUpdateManager are seen and work as expected
        /// </summary>
        [TestMethod]
        public async Task PageUpdateManagerTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Insert test URLs
                var urlNeedingUpdate = new Url() { FullUrl = "http://whatever/" + DateTime.Now.Ticks };
                var urlNotNeedingUpdate = new Url() { FullUrl = "http://whatever2/" + DateTime.Now.Ticks, MetadataLastRefreshed = DateTime.Now };
                db.urls.Add(urlNeedingUpdate);
                db.urls.Add(urlNotNeedingUpdate);
                await db.SaveChangesAsync();

                var randoGuid = Guid.NewGuid();


                // Test empty/invalid props
                var pageUpdatesEmpty = new List<PageUpdateEventAppInsightsQueryResult>()
                {
                    new PageUpdateEventAppInsightsQueryResult{
                        CustomProperties = new PageUpdateEventCustomProps {
                            Url = urlNeedingUpdate.FullUrl
                        }
                    }
                };
                var pageUpdateManager = new PageUpdateManager(AnalyticsLogger.ConsoleOnlyTracer(), 10, new AppConfig());
                var saveResultsEmpty = await pageUpdateManager.SaveAll(pageUpdatesEmpty);
                Assert.IsTrue(saveResultsEmpty.Count == 0);     // Should have no URLs updated because nothing to do - no custom props or taxonomy fields

                // Now save something with data over 
                dynamic testPropsUpdate1 = new ExpandoObject();
                testPropsUpdate1.prop1 = 1;
                testPropsUpdate1.prop2 = "2";


                dynamic testPropsUpdate2 = new ExpandoObject();
                testPropsUpdate2.prop1 = 2;     // Duplicate prop name
                testPropsUpdate2.prop3 = 1;

                dynamic testTaxProps = new ExpandoObject();
                testTaxProps.prop1 = 1;
                testTaxProps.prop2 = "2";
                testTaxProps.invalidProp = new { whatever = "whoever" };

                // Add random props to both updates
                var randoPropName = "Prop" + DateTime.Now.Ticks;
                var randoPropVal = "PropVal" + DateTime.Now.Ticks;
                ((IDictionary<string, object>)testPropsUpdate1).Add(randoPropName, randoPropVal);
                ((IDictionary<string, object>)testPropsUpdate2).Add(randoPropName, randoPropVal);

                // Add tax props
                var taxProps = new List<TaxonomoyProperty>
                {
                    new TaxonomoyProperty { Id = randoGuid, Label = "Test Tax Prop", PropName = "TestProp" },
                    new TaxonomoyProperty { Id = Guid.NewGuid(), Label = randoPropVal, PropName = randoPropName },  // Duplicate name of a normal prop
                    new TaxonomoyProperty { PropName = "InvalidProp" }        // Invalid entry
                };

                // Save page over multiple updates. When a URL has multiple update messages, the update to SQL should include all messages
                var pageUpdates = new List<PageUpdateEventAppInsightsQueryResult>()
                {
                    new PageUpdateEventAppInsightsQueryResult{
                        CustomProperties = new PageUpdateEventCustomProps {
                            Url = urlNeedingUpdate.FullUrl,
                            PropsString = JsonConvert.SerializeObject(testPropsUpdate1),
                            TaxonomyPropsString = JsonConvert.SerializeObject(taxProps)
                        },
                        AppInsightsTimestamp = DateTime.Now.AddHours(-1)
                    },
                    new PageUpdateEventAppInsightsQueryResult{      // Other props being included in a subsequent update msg
                        CustomProperties = new PageUpdateEventCustomProps {
                            Url = urlNeedingUpdate.FullUrl,
                            PropsString = JsonConvert.SerializeObject(testPropsUpdate2)
                        },
                        AppInsightsTimestamp = DateTime.Now
                    }
                };
                var saveResults = await pageUpdateManager.SaveAll(pageUpdates);

                // One URL only should've been updated
                Assert.IsTrue(saveResults.Count == 1);

                // Save again. Nothing should be updated
                saveResults = await pageUpdateManager.SaveAll(pageUpdates);
                Assert.IsTrue(saveResults.Count == 0);

                // Check saved taxonomy & fields
                var matchingMMDef = await db.FileMetadataPropertyValues.Where(f => f.TagGuid.HasValue && f.TagGuid.Value == randoGuid).ToListAsync();
                Assert.IsTrue(matchingMMDef.Count == 1);
                var matchingMM = await db.FileMetadataPropertyValues
                    .Include(m => m.Field)
                    .Where(f => f.TagGuid == randoGuid && f.Url.ID == urlNeedingUpdate.ID)
                        .ToListAsync();
                Assert.IsTrue(matchingMM.Count == 1);
                Assert.IsNotNull(matchingMM[0].Field.Name);
                Assert.IsNotNull(matchingMM[0].TagGuid);
                Assert.IsTrue(matchingMM[0].TagGuid == randoGuid);

                // Check our rando prop is inserted
                var matchingFieldNameDefs = await db.FileMetadataFields.Where(f => f.Name == randoPropName).ToListAsync();
                Assert.IsTrue(matchingFieldNameDefs.Count == 1);

                // Check saved properties
                var urlProps = await db.FileMetadataPropertyValues.Where(m => m.Url.ID == urlNeedingUpdate.ID).ToListAsync();
                Assert.IsTrue(urlProps.Count == 5);        // x3 static props (over x2 updates); 1 dynamic named; 1 taxonomy

            }
        }

        [TestMethod]
        public async Task PageUpdateManagerMultithreadTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Insert test URLs
                var urlNeedingUpdate = new Url() { FullUrl = "http://whatever/" + DateTime.Now.Ticks };
                db.urls.Add(urlNeedingUpdate);

                await db.SaveChangesAsync(); var randoGuid = Guid.NewGuid();


                var pageUpdateManager = new PageUpdateManager(AnalyticsLogger.ConsoleOnlyTracer(), 1, new AppConfig());      // Allow 1 update per thread
                var pageUpdates = new List<PageUpdateEventAppInsightsQueryResult>();

                // Add a bunch of updates
                const int LOOPS = 100;
                for (int i = 0; i < LOOPS; i++)
                {
                    dynamic testPropsUpdate = new ExpandoObject();

                    // Add random props to both updates
                    var randoPropName = "Prop" + i;
                    var randoPropVal = "PropVal" + i;
                    ((IDictionary<string, object>)testPropsUpdate).Add(randoPropName, randoPropVal);

                    var taxProp = new List<TaxonomoyProperty>
                    {
                        new TaxonomoyProperty { Id = Guid.NewGuid(), Label = "Test Tax Prop " + i, PropName = "TestProp" + i }
                    };

                    pageUpdates.Add(new PageUpdateEventAppInsightsQueryResult
                    {
                        CustomProperties = new PageUpdateEventCustomProps
                        {
                            Url = urlNeedingUpdate.FullUrl,
                            PropsString = JsonConvert.SerializeObject(testPropsUpdate),
                            TaxonomyPropsString = JsonConvert.SerializeObject(taxProp)
                        },
                        AppInsightsTimestamp = DateTime.Now
                    });
                }

                var saveResults = await pageUpdateManager.SaveAll(pageUpdates);

                // One URL only should've been updated
                Assert.IsTrue(saveResults.Count == 1);

                // Check saved properties
                var urlProps = await db.FileMetadataPropertyValues.Where(m => m.Url.ID == urlNeedingUpdate.ID).ToListAsync();
                Assert.IsTrue(urlProps.Count == LOOPS * 2);         // Taxonomy + normal props

            }
        }

        [TestMethod]
        public async Task PageUpdateManagerMultithreadSamePropNameValTests()
        {
            var ticks = DateTime.Now.Ticks;
            using (var db = new AnalyticsEntitiesContext())
            {
                // Insert test URLs
                var urlNeedingUpdate = new Url() { FullUrl = "http://whatever/" + DateTime.Now.Ticks };
                db.urls.Add(urlNeedingUpdate);

                await db.SaveChangesAsync();

                var pageUpdateManager = new PageUpdateManager(AnalyticsLogger.ConsoleOnlyTracer(), 1, new AppConfig());      // Allow 1 update per thread
                var pageUpdates = new List<PageUpdateEventAppInsightsQueryResult>();

                // Add a bunch of updates
                const int LOOPS = 100;
                for (int i = 0; i < LOOPS; i++)
                {
                    dynamic testPropsUpdate = new ExpandoObject();

                    // Add random props to both updates
                    var randoPropName = "Prop-" + ticks;
                    var randoPropVal = "PropVal-" + ticks;
                    ((IDictionary<string, object>)testPropsUpdate).Add(randoPropName, randoPropVal);

                    var taxProp = new List<TaxonomoyProperty>
                    {
                        new TaxonomoyProperty { Id = Guid.NewGuid(), Label = "Test Tax Prop " + ticks, PropName = "TestProp-" + ticks }
                    };

                    pageUpdates.Add(new PageUpdateEventAppInsightsQueryResult
                    {
                        CustomProperties = new PageUpdateEventCustomProps
                        {
                            Url = urlNeedingUpdate.FullUrl,
                            PropsString = JsonConvert.SerializeObject(testPropsUpdate),
                            TaxonomyPropsString = JsonConvert.SerializeObject(taxProp)
                        },
                        AppInsightsTimestamp = DateTime.Now
                    });
                }

                var saveResults = await pageUpdateManager.SaveAll(pageUpdates);

                // One URL only should've been updated
                Assert.IsTrue(saveResults.Count == 1);
            }
        }

        [TestMethod]
        public async Task PageUpdateManagerSamePropNameValTests()
        {
            var ticks = DateTime.Now.Ticks;
            const int LOOPS = 2;
            using (var db = new AnalyticsEntitiesContext())
            {
                // Insert test URLs
                var urlNeedingUpdate = new Url() { FullUrl = "http://whatever/" + DateTime.Now.Ticks };
                db.urls.Add(urlNeedingUpdate);

                var randoPropName = "Test Tax Prop " + ticks;
                var randoProp = new FileMetadataFieldName { Name = randoPropName };
                db.FileMetadataFields.Add(randoProp);
                await db.SaveChangesAsync();


                var pageUpdateManager = new PageUpdateManager(AnalyticsLogger.ConsoleOnlyTracer(), LOOPS, new AppConfig());
                var pageUpdates = new List<PageUpdateEventAppInsightsQueryResult>();

                dynamic testPropsUpdate = new ExpandoObject();

                // Add random props to both updates
                var randoPropVal = "PropVal-" + ticks;
                ((IDictionary<string, object>)testPropsUpdate).Add(randoPropName, randoPropVal);

                var taxProp = new List<TaxonomoyProperty>
                    {
                        new TaxonomoyProperty { Id = Guid.NewGuid(), Label = "Tax Prop Label " + ticks, PropName = randoPropName }
                    };

                pageUpdates.Add(new PageUpdateEventAppInsightsQueryResult
                {
                    CustomProperties = new PageUpdateEventCustomProps
                    {
                        Url = urlNeedingUpdate.FullUrl,
                        PropsString = JsonConvert.SerializeObject(testPropsUpdate),
                        TaxonomyPropsString = JsonConvert.SerializeObject(taxProp)
                    },
                    AppInsightsTimestamp = DateTime.Now
                });

                var saveResults = await pageUpdateManager.SaveAll(pageUpdates);

                // One URL only should've been updated
                Assert.IsTrue(saveResults.Count == 1);
            }
        }

        [TestMethod]
        public async Task CommentsCognitiveTests()
        {
            var config = new AppConfig();

            if (!config.IsValidCognitiveConfig)
            {
                Assert.Inconclusive("CognitiveEndpoint or credentials are not configured.");
            }
            var client = config.CreateCognitiveServicesClient(AnalyticsLogger.ConsoleOnlyTracer());
            if (client == null)
            {
                Assert.Inconclusive("CognitiveEndpoint or credentials are not configured.");
            }

            var propsString1 = @"[
                        {
                            ""id"": ""1"",
                            ""comment"": ""This is amazing"",
                        },
                        {
                            ""id"": ""2"",
                            ""comment"": ""Todo eso está fatal. No puede ser peor."",
                        }
                    ]";

            var pageUpdate = new PageUpdateEventAppInsightsQueryResult
            {
                CustomProperties = new PageUpdateEventCustomProps { Url = "https://site", CommentsString = propsString1 },
                AppInsightsTimestamp = DateTime.Now
            };
            // Trigger fake comments build
            pageUpdate.CustomProperties.SimplePropsDic.Count();

            var cog = await pageUpdate.CustomProperties.PageComments
                .ToTextAnalysisSampleList()
                .GetCognitiveDataStats(client, AnalyticsLogger.ConsoleOnlyTracer());
            Assert.IsNotNull(cog);


            Assert.IsTrue(cog.Count == 2);

            Assert.IsTrue(cog.First().CognitiveStat.SentimentScore == 1);       // Happy
            Assert.IsTrue(cog.First().CognitiveStat.LanguageName == "English");
            Assert.IsTrue(cog.Last().CognitiveStat.SentimentScore == 0);        // Unhappy
            Assert.IsTrue(cog.Last().CognitiveStat.LanguageName == "Spanish");
        }

        /// <summary>
        /// Checks saving comments and page likes works
        /// </summary>
        [TestMethod]
        public async Task CommentsSaveTests()
        {
            const int LOOPS = 10;
            using (var db = new AnalyticsEntitiesContext())
            {
                // Clear table
                var allComments = await db.UrlComments.ToListAsync();
                db.UrlComments.RemoveRange(allComments);
                await db.SaveChangesAsync();

                // Add test user
                var testUser = new User { UserPrincipalName = "testuser" + DateTime.Now.Ticks };
                db.users.Add(testUser);
                await db.SaveChangesAsync();

                // Add x2 test URLs
                var url1 = new Url() { FullUrl = "http://whatever/" + DateTime.Now.Ticks };
                var url2 = new Url() { FullUrl = "http://whatever2/" + DateTime.Now.Ticks };
                db.urls.Add(url1);
                db.urls.Add(url2);
                await db.SaveChangesAsync();

                var comments = new List<PageCommentTemp>();

                // Insert test URLs for URL 1
                for (int i = 0; i < LOOPS; i++)
                {
                    int? parentSpId = null;
                    if (i > 0) parentSpId = i;

                    comments.Add(new PageCommentTemp("Url 1 Comment " + i, DateTime.Now, testUser.ID, i + 1, url1.ID, parentSpId));
                }

                // Insert test URLs for URL 1
                for (int i = 0; i < LOOPS; i++)
                {
                    int? parentSpId = null;
                    if (i > 0) parentSpId = i;
                    comments.Add(new PageCommentTemp("Url 2 Comment " + i, DateTime.Now, testUser.ID, i + 1, url2.ID, parentSpId));
                }

                // Save comments
                await comments.Save(db, AnalyticsLogger.ConsoleOnlyTracer());

                // Check saved total count
                var savedComments = await db.UrlComments.ToListAsync();
                Assert.IsTrue(savedComments.Count == LOOPS * 2);


                // Make sure parents are right
                var url1Comments = await db.UrlComments.Include(c => c.ParentComment).Where(c => c.Url.ID == url1.ID).ToListAsync();
                foreach (var ur1c in url1Comments)
                {
                    Assert.IsTrue(ur1c.ParentComment == null || ur1c.ParentComment.Url.ID == url1.ID);
                }

                var url2Comments = await db.UrlComments.Include(c => c.ParentComment).Where(c => c.Url.ID == url2.ID).ToListAsync();
                foreach (var ur2c in url2Comments)
                {
                    Assert.IsTrue(ur2c.ParentComment == null || ur2c.ParentComment.Url.ID == url2.ID);
                }
            }
        }

        /// <summary>
        /// Checks saving comments and page likes works
        /// </summary>
        [TestMethod]
        public async Task PageUpdateManagerCommentsAndLikesTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Insert test URLs
                var urlNeedingUpdate = new Url() { FullUrl = "http://whatever/" + DateTime.Now.Ticks };
                db.urls.Add(urlNeedingUpdate);
                await db.SaveChangesAsync();

                // Insert new
                var pageUpdateManager = new PageUpdateManager(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig());
                var propsString1 = @"
                {
                    ""PageLikes"": [
                        {
                            ""creationDate"": ""2023-09-27T09:51:28.423Z"",
                            ""email"": ""admin@MODERNCOMMS933270.onmicrosoft.com"",
                            ""id"": 6
                        },
                        {
                            ""creationDate"": ""2023-09-27T09:51:28.423Z"",
                            ""email"": ""bob@MODERNCOMMS933270.onmicrosoft.com"",
                            ""id"": 7
                        }
                    ],
                    ""PageLikesCount"": 1,
                    ""Comments"": [
                        {
                            ""id"": ""1"",
                            ""comment"": ""Comment 1"",
                            ""email"": ""admin@MODERNCOMMS933270.onmicrosoft.com"",
                            ""isReply"": false,
                            ""creationDate"": ""2023-09-27T09:28:51.747Z""
                        },
                        {
                            ""id"": ""2"",
                            ""comment"": ""Comment 2"",
                            ""email"": ""admin@MODERNCOMMS933270.onmicrosoft.com"",
                            ""isReply"": true,
                            ""creationDate"": ""2023-09-27T09:28:58.393Z"",
                            ""parentId"": ""1""
                        }
                    ]
                }";

                var pageUpdates1 = new List<PageUpdateEventAppInsightsQueryResult>()
                {
                    new PageUpdateEventAppInsightsQueryResult
                    {
                        CustomProperties = new PageUpdateEventCustomProps { Url = urlNeedingUpdate.FullUrl, PropsString = propsString1 },
                        AppInsightsTimestamp = DateTime.Now
                    }
                };

                var saveResults1 = await pageUpdateManager.SaveAll(pageUpdates1);

                // One URL only should've been updated
                Assert.IsTrue(saveResults1.Count == 1);

                var urlComments = await db.UrlComments.Where(c => c.Url.ID == urlNeedingUpdate.ID).ToListAsync();
                var urlLikes = await db.UrlLikes.Where(c => c.Url.ID == urlNeedingUpdate.ID).ToListAsync();

                // Reset page update 
                await db.Entry(urlNeedingUpdate).ReloadAsync();
                urlNeedingUpdate.MetadataLastRefreshed = null;
                await db.SaveChangesAsync();

                // Delete and comment & like; insert new one of each
                var propsString2 = @"
                {
                    ""PageLikes"": [
                        {
                            ""creationDate"": ""2023-09-27T09:51:28.423Z"",
                            ""email"": ""admin@MODERNCOMMS933270.onmicrosoft.com"",
                            ""id"": 6
                        },
                        {
                            ""creationDate"": ""2023-09-27T09:51:28.423Z"",
                            ""email"": ""newliker@MODERNCOMMS933270.onmicrosoft.com"",
                            ""id"": 88
                        }
                    ],
                    ""PageLikesCount"": 1,
                    ""Comments"": [
                        {
                            ""id"": ""1"",
                            ""comment"": ""Comment 1"",
                            ""email"": ""admin@MODERNCOMMS933270.onmicrosoft.com"",
                            ""isReply"": false,
                            ""creationDate"": ""2023-09-27T09:28:51.747Z""
                        },
                        {
                            ""id"": ""44"",
                            ""comment"": ""Comment 3"",
                            ""email"": ""admin@MODERNCOMMS933270.onmicrosoft.com"",
                            ""isReply"": true,
                            ""creationDate"": ""2023-09-27T09:28:58.393Z"",
                            ""parentId"": ""1""
                        }
                    ]
                }";

                var pageUpdates2 = new List<PageUpdateEventAppInsightsQueryResult>()
                {
                    new PageUpdateEventAppInsightsQueryResult
                    {
                        CustomProperties = new PageUpdateEventCustomProps { Url = urlNeedingUpdate.FullUrl, PropsString = propsString2 },
                        AppInsightsTimestamp = DateTime.Now
                    }
                };

                await pageUpdateManager.SaveAll(pageUpdates2);

                var urlComments2 = await db.UrlComments.Where(c => c.Url.ID == urlNeedingUpdate.ID).ToListAsync();
                var urlLikes2 = await db.UrlLikes.Where(c => c.Url.ID == urlNeedingUpdate.ID).ToListAsync();

                foreach (var c in urlComments2)
                {
                    db.Entry(c).Reload();
                }
                foreach (var l in urlLikes2)
                {
                    db.Entry(l).Reload();
                }
            }
        }

        /// <summary>
        /// Tests an issue where the same taxonomy fields would have different guids but same name, causing a crash
        /// </summary>
        [TestMethod]
        public async Task DupTaxonomoyPropertiesWithDifferentGuidsPageUpdateManagerTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Insert test URLs
                var urlNeedingUpdate = new Url() { FullUrl = "http://whatever/" + DateTime.Now.Ticks };
                var urlNotNeedingUpdate = new Url() { FullUrl = "http://whatever2/" + DateTime.Now.Ticks, MetadataLastRefreshed = DateTime.Now };
                db.urls.Add(urlNeedingUpdate);
                db.urls.Add(urlNotNeedingUpdate);
                await db.SaveChangesAsync();

                var pageUpdateManager = new PageUpdateManager(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig());

                // Add random props to both updates
                var randoPropName = "Prop" + DateTime.Now.Ticks;
                var randoPropVal = "PropVal" + DateTime.Now.Ticks;

                // Add tax props
                var taxProps = new List<TaxonomoyProperty>
                {
                    new TaxonomoyProperty { Id = Guid.NewGuid(), Label = randoPropVal, PropName = randoPropName },
                    new TaxonomoyProperty { Id = Guid.NewGuid(), Label = randoPropVal + "val2", PropName = randoPropName }
                };

                // Save page over multiple updates. When a URL has multiple update messages, the update to SQL should include all messages
                var pageUpdates = new List<PageUpdateEventAppInsightsQueryResult>()
                {
                    new PageUpdateEventAppInsightsQueryResult{
                        CustomProperties = new PageUpdateEventCustomProps {
                            Url = urlNeedingUpdate.FullUrl,
                            TaxonomyPropsString = JsonConvert.SerializeObject(taxProps)
                        },
                        AppInsightsTimestamp = DateTime.Now.AddHours(-1)
                    }
                };
                await pageUpdateManager.SaveAll(pageUpdates);
            }
        }

        [TestMethod]
        public void PageUpdateEventAppInsightsQueryResultMergeTest()
        {
            const string URL = "http://url";

            var randoGuid = Guid.NewGuid();

            dynamic testPropsUpdate1 = new ExpandoObject();
            testPropsUpdate1.prop1 = 1;
            testPropsUpdate1.prop2 = "2";


            dynamic testPropsUpdate2 = new ExpandoObject();
            testPropsUpdate1.prop3 = 1;

            var taxProps1 = new List<TaxonomoyProperty>
                {
                    new TaxonomoyProperty { Id = randoGuid, Label = "Test Tax Prop1", PropName = "TestProp1" },
                };
            var taxProps2 = new List<TaxonomoyProperty>
                {
                    new TaxonomoyProperty { Id = randoGuid, Label = "Duplicate", PropName = "TestProp2" },  // Shouldn't be added
                    new TaxonomoyProperty { Id = Guid.NewGuid(), Label = "Test Tax Prop2", PropName = "TestProp2" }
                };

            var e1 = new PageUpdateEventAppInsightsQueryResult
            {
                CustomProperties = new PageUpdateEventCustomProps
                {
                    Url = URL,
                    PropsString = JsonConvert.SerializeObject(testPropsUpdate1),
                    TaxonomyPropsString = JsonConvert.SerializeObject(taxProps1)
                },
                AppInsightsTimestamp = DateTime.Now.AddHours(-1)
            };
            var e2 = new PageUpdateEventAppInsightsQueryResult
            {
                CustomProperties = new PageUpdateEventCustomProps
                {
                    Url = URL,
                    PropsString = JsonConvert.SerializeObject(testPropsUpdate2),
                    TaxonomyPropsString = JsonConvert.SerializeObject(taxProps2)
                },
                AppInsightsTimestamp = DateTime.Now.AddHours(-1)
            };

            var l = new List<PageUpdateEventAppInsightsQueryResult>()
            {
                e1, e2
            };

            // Check our single update contains all updates
            var compiled = new PageUpdateEventAppInsightsQueryResult(l);
            Assert.IsTrue(compiled.CustomProperties.SimplePropsDic.Count == 3);
            Assert.IsTrue(compiled.CustomProperties.TaxonomyProps.Count == 2);

            Assert.IsTrue(compiled.CustomProperties.Url == URL);
        }

        [TestMethod]
        public void PageUpdateEventAppInsightsQueryResultCommentsAndLikesMergeTest()
        {
            const string URL = "http://url";
            var propsStringComments1 = @"[
                        {
                            ""id"": ""1"",
                            ""comment"": ""reply\n"",
                            ""email"": ""admin@MODERNCOMMS933270.onmicrosoft.com"",
                            ""isReply"": false,
                            ""creationDate"": ""2023-09-27T09:28:51.747Z""
                        }
                    ]
                ";
            var propsStringLikes1 = @"[
                        {
                            ""creationDate"": ""2023-09-27T09:51:28.423Z"",
                            ""email"": ""admin@MODERNCOMMS933270.onmicrosoft.com"",
                            ""id"": 6
                        }
                    ]";

            var e1 = new PageUpdateEventAppInsightsQueryResult
            {
                CustomProperties = new PageUpdateEventCustomProps { Url = URL, CommentsString = propsStringComments1, LikesString = propsStringLikes1 },
                AppInsightsTimestamp = DateTime.Now
            };

            var propsStringComments2 = @"[
                        {
                            ""id"": ""2"",
                            ""comment"": ""child reply"",
                            ""email"": ""admin@MODERNCOMMS933270.onmicrosoft.com"",
                            ""isReply"": true,
                            ""creationDate"": ""2023-09-27T09:28:58.393Z"",
                            ""parentId"": ""1""
                        }
                    ]";
            var propsStringLikes2 = @"[
                        {
                            ""creationDate"": ""2023-09-27T09:51:28.423Z"",
                            ""email"": ""bob@MODERNCOMMS933270.onmicrosoft.com"",
                            ""id"": 7
                        }
                    ]";

            var e2 = new PageUpdateEventAppInsightsQueryResult
            {
                CustomProperties = new PageUpdateEventCustomProps { Url = URL, CommentsString = propsStringComments2, LikesString = propsStringLikes2 },
                AppInsightsTimestamp = DateTime.Now
            };

            var l = new List<PageUpdateEventAppInsightsQueryResult>() { e1, e2 };

            // Check our single update contains all updates
            var compiled = new PageUpdateEventAppInsightsQueryResult(l);
            Assert.IsTrue(compiled.CustomProperties.PageComments.Count == 2);
            Assert.IsTrue(compiled.CustomProperties.Likes.Count == 2);
        }

        [TestMethod]
        public void PageUpdateEventCustomPropsModelTests()
        {
            // Basic
            var basicProps = new PageUpdateEventCustomProps();

            var testProps = new
            {
                prop1 = 1,
                prop2 = "2",
                propArray = new string[0],
                Tags = new
                {
                    __metadata = new { type = "SP.Taxonomy.TaxonomyFieldValue" },       // 
                    Label = "1",
                    TermGuid = "59bb9c81-b4ae-44e9-83f6-c09ca45888e8",
                    WssId = 1
                },
            };

            // Use real data
            basicProps.PropsString = JsonConvert.SerializeObject(testProps);

            Assert.IsTrue(basicProps.SimplePropsDic.Count == 2);
            Assert.IsTrue(basicProps.TaxonomyProps.Count == 0);     // Not parsed - MM props deserialised directly

            var realTest = new PageUpdateEventAppInsightsQueryResult();

            // We have just the custom props serialised in test resources
            realTest.CustomProperties = JsonConvert.DeserializeObject<PageUpdateEventCustomProps>(Resources.PageUpdateEventAppInsightsQueryResult);

            Assert.IsTrue(realTest.CustomProperties.SimplePropsDic.Count == 13);
            Assert.IsTrue(realTest.CustomProperties.TaxonomyProps.Count == 0);
        }

        [TestMethod]
        public void AllModelAppInsightsQueryResultAreValidTests()
        {
            var pageUpdate = new PageUpdateEventAppInsightsQueryResult();
            Assert.IsFalse(pageUpdate.IsValid);
            pageUpdate.CustomProperties.Url = "http://whatever";
            Assert.IsTrue(pageUpdate.IsValid);

            // Page exit
            var pe = new PageExitEventAppInsightsQueryResult();
            Assert.IsFalse(pe.IsValid);
            pe.CustomProperties.PageRequestId = Guid.NewGuid();
            pe.CustomProperties.ActiveTime = 46;

            Assert.IsTrue(pe.IsValid);

            // Search
            var search = new SearchEventAppInsightsQueryResult();
            Assert.IsFalse(search.IsValid);
            search.CustomProperties.SessionId = Guid.NewGuid().ToString();
            search.CustomProperties.SearchText = "search";
            Assert.IsTrue(search.IsValid);


            // Clicks
            var click = new ClickEventAppInsightsQueryResult();
            Assert.IsFalse(click.IsValid);
            click.AppInsightsTimestamp = DateTime.Now;
            click.CustomProperties.LinkText = "Whatevs";
            click.CustomProperties.PageRequestId = Guid.NewGuid();
            Assert.IsTrue(click.IsValid);
        }

        [TestMethod]
        public async Task EmptySaveTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var emptyBatch = new PageViewCollection();
                await emptyBatch.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());
            }
        }

        [TestMethod]
        public async Task DuplicatePageRequestIdTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var duplicateHitsInSingleBatch = new PageViewCollection();
                var guid = Guid.NewGuid();

                var hitsPreInsert = db.hits.Count();

                duplicateHitsInSingleBatch.Rows.Add(new PageViewAppInsightsQueryResult
                {
                    Url = "http://unititesting",
                    CustomProperties = new PageViewCustomProps
                    {
                        PageRequestId = guid,
                        SessionId = Guid.NewGuid().ToString()
                    },
                    AppInsightsTimestamp = DateTime.Now,
                    Browser = "Whatevs",
                    DeviceModel = "Whoever",
                    Username = "bob",
                    ClientOS = "Win"
                });
                duplicateHitsInSingleBatch.Rows.Add(new PageViewAppInsightsQueryResult
                {
                    Url = "http://unititesting2",
                    CustomProperties = new PageViewCustomProps
                    {
                        PageRequestId = guid,
                        SessionId = Guid.NewGuid().ToString()
                    },
                    AppInsightsTimestamp = DateTime.Now,
                    Browser = "Whatevs",
                    DeviceModel = "Whoever",
                    Username = "bob",
                    ClientOS = "Win"
                });
                await duplicateHitsInSingleBatch.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());
                var hitsPostInsert = db.hits.Count();

                // We should have only 1 extra hit as they both share same req ID
                Assert.IsTrue(hitsPreInsert == hitsPostInsert - 1);

                var seperateBatch = new PageViewCollection();
                duplicateHitsInSingleBatch.Rows.Add(new PageViewAppInsightsQueryResult
                {
                    Url = "http://unititesting",
                    CustomProperties = new PageViewCustomProps
                    {
                        PageRequestId = guid,
                        SessionId = Guid.NewGuid().ToString()
                    },
                    AppInsightsTimestamp = DateTime.Now,
                    Browser = "Whatevs",
                    DeviceModel = "Whoever",
                    Username = "bob",
                    ClientOS = "Win"
                });
                await seperateBatch.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());


                // We should still have only 1 extra hit as they both share same req ID
                hitsPostInsert = db.hits.Count();
                Assert.IsTrue(hitsPreInsert == hitsPostInsert - 1);
            }
        }

        /// <summary>
        /// Test we can save lots of page hits
        /// </summary>
        [TestMethod]
        public async Task PageViewCollectionSaveTests()
        {
            // Model tests
            var v = new PageViewAppInsightsQueryResult();
            Assert.IsFalse(v.IsValid);

            using (var db = new AnalyticsEntitiesContext())
            {
                var hitsToSave = new PageViewCollection();

                var lastHitBeforeTest = await db.hits.OrderByDescending(h => h.ID).Take(1).FirstOrDefaultAsync();

                const int HITS_INSERTS = 5003;  // Uneven number
                for (int i = 0; i < HITS_INSERTS; i++)
                {
                    hitsToSave.Rows.Add(new PageViewAppInsightsQueryResult
                    {
                        Url = "http://unititesting/" + i,
                        CustomProperties = new PageViewCustomProps
                        {
                            PageRequestId = Guid.NewGuid(),
                            SessionId = Guid.NewGuid().ToString()
                        },
                        AppInsightsTimestamp = DateTime.Now,
                        Browser = "Whatevs",
                        DeviceModel = "Whoever",
                        Username = "bob",
                        ClientOS = "Win"
                    });

                }
                await hitsToSave.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());

                var lastHitAfterTest = await db.hits.OrderByDescending(h => h.ID).Take(1).FirstOrDefaultAsync();

                // Load & verify
                var hitsInserted = await db.hits
                    .Where(h => h.ID > lastHitBeforeTest.ID && h.ID <= lastHitAfterTest.ID)
                    .Include(h => h.url)
                    .ToListAsync();

                Assert.IsTrue(hitsInserted.Count == HITS_INSERTS);

                int foundCount = 0;
                foreach (var insertedHit in hitsInserted.OrderBy(h => h.url.FullUrl))
                {
                    // Check each URL was as it went in
                    Assert.IsTrue(hitsToSave.Rows.Where(h => h.Url == "http://unititesting/" + foundCount).Count() == 1);
                    foundCount++;
                }

                // Make sure 
                Assert.IsTrue(foundCount == HITS_INSERTS);
            }
        }

        /// <summary>
        /// Test our page exit events are seen in hits
        /// </summary>
        [TestMethod]
        public async Task PageExitEventSave()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = new AnalyticsEntitiesContext())
            {
                var hitsToSave = new PageViewCollection();

                var pageStatsToSave = new CustomEventsResultCollection();

                var lastHitBeforeTest = await db.hits.OrderByDescending(h => h.ID).Take(1).FirstOrDefaultAsync();

                var timeOnPageRando = new Random(DateTime.Now.Millisecond);
                const int HITS_INSERTS = 10;
                for (int i = 0; i < HITS_INSERTS; i++)
                {
                    // Match events on same req id
                    var reqId = Guid.NewGuid();
                    pageStatsToSave.Rows.Add(new PageExitEventAppInsightsQueryResult()
                    {
                        CustomProperties = new PageExitCustomProps()
                        {
                            ActiveTime = timeOnPageRando.NextDouble(),
                            PageRequestId = reqId
                        }
                    });

                    hitsToSave.Rows.Add(new PageViewAppInsightsQueryResult
                    {
                        Url = "http://unititesting/" + i,
                        CustomProperties = new PageViewCustomProps
                        {
                            PageRequestId = reqId,
                            SessionId = Guid.NewGuid().ToString()
                        },
                        AppInsightsTimestamp = DateTime.Now,
                        Browser = "Whatevs",
                        DeviceModel = "Whoever",
                        Username = "bob",
                        ClientOS = "Win"
                    });

                }
                await hitsToSave.SaveToSQL(db, logger);
                await pageStatsToSave.SaveHitsUpdatesToSQL(logger, db);
                var lastHitAfterTest = await db.hits.OrderByDescending(h => h.ID).Take(1).FirstOrDefaultAsync();

                // Load & verify
                var hitsInserted = await db.hits
                    .Where(h => h.ID > lastHitBeforeTest.ID && h.ID <= lastHitAfterTest.ID)
                    .Include(h => h.url)
                    .ToListAsync();

                foreach (var insertedHit in hitsInserted.OrderBy(h => h.url.FullUrl))
                {
                    Assert.IsTrue(insertedHit.seconds_on_page != null);
                }
            }
        }

        /// <summary>
        /// Test various clicks are saved
        /// </summary>
        [TestMethod]
        public async Task ClickCollectionEventSave()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = new AnalyticsEntitiesContext())
            {
                var clicksToSave = new CustomEventsResultCollection();

                // Test empty
                await clicksToSave.SaveClicksToSQL(logger, db);

                // Insert required hit & session for clicks to work
                var testUser = new User { UserPrincipalName = "testuser" + DateTime.Now.Ticks };
                var testSesh = new UserSession() { ai_session_id = Guid.NewGuid().ToString(), user = testUser };
                var testHit = new Hit() { session = testSesh, page_request_id = Guid.NewGuid(), hit_timestamp = DateTime.Now, url = new Url { FullUrl = "http://whatever/" + DateTime.Now.Ticks } };
                db.hits.Add(testHit);
                await db.SaveChangesAsync();

                var lastClickBeforeTest = await db.Clicks.OrderByDescending(h => h.ID).Take(1).FirstOrDefaultAsync() ?? new Common.Entities.Entities.WebTraffic.Clicks();

                // Use all fields
                const int CLICKS_INSERTS = 10;
                const string URL_BASE = "http://web/page?param=val#bookmark";
                for (int testDataCreateIndex = 0; testDataCreateIndex < CLICKS_INSERTS; testDataCreateIndex++)
                {
                    // Match events on same req id
                    clicksToSave.Rows.Add(new ClickEventAppInsightsQueryResult()
                    {
                        CustomProperties = new ClickCustomProps()
                        {
                            AltText = testDataCreateIndex.ToString(),
                            ClassNames = testDataCreateIndex.ToString(),
                            HRef = URL_BASE + testDataCreateIndex,
                            LinkText = testDataCreateIndex.ToString(),
                            PageRequestId = testHit.page_request_id
                        },
                        AppInsightsTimestamp = DateTime.Now,
                    });
                }

                await clicksToSave.SaveAllEventTypesToSql(logger, new AppConfig());
                var lastClickAfterTest = await db.Clicks.OrderByDescending(h => h.ID).Take(1).FirstOrDefaultAsync() ?? new Common.Entities.Entities.WebTraffic.Clicks() { ID = int.MaxValue };

                // Load & verify
                var clicksInserted = await db.Clicks
                    .Where(h => h.ID > lastClickBeforeTest.ID && h.ID <= lastClickAfterTest.ID)
                        .Include(h => h.ClassNames)
                        .Include(c => c.Url)
                        .Include(c => c.Title)
                        .Include(c => c.PageView)
                        .Include(c => c.PageView.url)
                            .ToListAsync();

                Assert.IsTrue(clicksInserted.Count == CLICKS_INSERTS);

                // Verify each field
                var testDataVerifyIndex = 0;
                foreach (var c in clicksInserted.OrderBy(h => h.Url.FullUrl))
                {
                    Assert.IsTrue(c.ClassNames.AllClassNames == testDataVerifyIndex.ToString());
                    Assert.IsTrue(c.Url.FullUrl == URL_BASE + testDataVerifyIndex);
                    Assert.IsTrue(c.Title.Name == testDataVerifyIndex.ToString());
                    Assert.IsTrue(c.ClassNames.AllClassNames == testDataVerifyIndex.ToString());
                    Assert.IsTrue(c.PageView.page_request_id == testHit.page_request_id);
                    testDataVerifyIndex++;
                }
            }
        }

        /// <summary>
        /// Tests duplicates don't crash import
        /// </summary>
        [TestMethod]
        public async Task ClickEventDuplicateSave()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = new AnalyticsEntitiesContext())
            {
                var clicksToSave = new CustomEventsResultCollection();

                // Insert required hit & session for clicks to work
                var testUser = new User { UserPrincipalName = "testuser" + DateTime.Now.Ticks };
                var testSesh = new UserSession() { ai_session_id = Guid.NewGuid().ToString(), user = testUser };
                var testHit = new Hit() { session = testSesh, page_request_id = Guid.NewGuid(), hit_timestamp = DateTime.Now, url = new Url { FullUrl = "http://whatever/" + DateTime.Now.Ticks } };
                db.hits.Add(testHit);
                await db.SaveChangesAsync();

                // Match events on same req id
                var dt = DateTime.Now;
                clicksToSave.Rows.Add(new ClickEventAppInsightsQueryResult()
                {
                    CustomProperties = new ClickCustomProps()
                    {
                        AltText = "whatevs",
                        ClassNames = "whatevs",
                        HRef = "whatevs",
                        LinkText = "whatevs",
                        PageRequestId = testHit.page_request_id
                    },
                    AppInsightsTimestamp = dt,
                });

                // Duplicate in same save set
                clicksToSave.Rows.Add(new ClickEventAppInsightsQueryResult()
                {
                    CustomProperties = new ClickCustomProps()
                    {
                        AltText = "whatevs",
                        ClassNames = "whatevs",
                        HRef = "whatevs",
                        LinkText = "whatevs",
                        PageRequestId = testHit.page_request_id
                    },
                    AppInsightsTimestamp = dt,
                });

                await clicksToSave.SaveAllEventTypesToSql(logger, new AppConfig());
                clicksToSave.Rows.Clear();

                // Duplicate in new save set
                clicksToSave.Rows.Add(new ClickEventAppInsightsQueryResult()
                {
                    CustomProperties = new ClickCustomProps()
                    {
                        AltText = "whatevs",
                        ClassNames = "whatevs",
                        HRef = "whatevs",
                        LinkText = "whatevs",
                        PageRequestId = testHit.page_request_id
                    },
                    AppInsightsTimestamp = dt,
                });

                // Make sure doesn't crash
                await clicksToSave.SaveAllEventTypesToSql(logger, new AppConfig());

                // ...and that the click was stored exactly once across both saves.
                Assert.AreEqual(1, await db.Clicks.CountAsync(c => c.HitID == testHit.ID));
            }
        }

        /// <summary>
        /// Every cycle re-reads the whole day, so a click that App Insights ingested after a later-stamped click
        /// was already imported arrives in a later batch. Clicks used to be de-duplicated against the newest click
        /// timestamp in the whole table, which dropped such a click for ever.
        /// </summary>
        [TestMethod]
        public async Task ClickEvent_OlderThanTheNewestStoredClick_IsStillImported()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = new AnalyticsEntitiesContext())
            {
                var firstHit = await CreateTestHitAsync(db);
                var laterArrivingHit = await CreateTestHitAsync(db);
                try
                {
                    // Anchored on the newest click already stored, so the test holds whatever other tests left behind.
                    var newestStored = await db.Clicks.MaxAsync(c => (DateTime?)c.TimeStamp) ?? DateTime.UtcNow;
                    var newer = newestStored.AddMinutes(10);
                    var older = newer.AddMinutes(-5);

                    await ClicksOf(TestClick(firstHit.page_request_id, newer)).SaveClicksToSQL(logger, db);
                    await ClicksOf(TestClick(laterArrivingHit.page_request_id, older)).SaveClicksToSQL(logger, db);

                    Assert.AreEqual(1, await db.Clicks.CountAsync(c => c.HitID == firstHit.ID));
                    Assert.AreEqual(1, await db.Clicks.CountAsync(c => c.HitID == laterArrivingHit.ID),
                        "A click older than the newest stored click is a late arrival, not a duplicate.");
                }
                finally
                {
                    await DeleteClicksOf(db, firstHit, laterArrivingHit);
                }
            }
        }

        /// <summary>
        /// The click time is the browser's clock. One browser with its clock in the future used to stop every
        /// click in the tenant being imported until real time caught up with it.
        /// </summary>
        [TestMethod]
        public async Task ClickEvent_FromABrowserWithAFutureClock_DoesNotBlockOtherClicks()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = new AnalyticsEntitiesContext())
            {
                var skewedHit = await CreateTestHitAsync(db);
                var normalHit = await CreateTestHitAsync(db);
                try
                {
                    await ClicksOf(TestClick(skewedHit.page_request_id, DateTime.UtcNow.AddYears(1))).SaveClicksToSQL(logger, db);
                    await ClicksOf(TestClick(normalHit.page_request_id, DateTime.UtcNow)).SaveClicksToSQL(logger, db);

                    Assert.AreEqual(1, await db.Clicks.CountAsync(c => c.HitID == normalHit.ID));
                }
                finally
                {
                    await DeleteClicksOf(db, skewedHit, normalHit);
                }
            }
        }

        /// <summary>
        /// Two clicks in one batch that share the key the unique index enforces - (hit_id, url_id, timestamp) - but
        /// differ in title used to both pass the merge's SELECT DISTINCT and fail the whole click batch on the index.
        /// </summary>
        [TestMethod]
        public async Task ClickEvent_TwoClicksWithTheSameKeyInOneBatch_AreSavedOnceRatherThanFailingTheBatch()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = new AnalyticsEntitiesContext())
            {
                var hit = await CreateTestHitAsync(db);
                try
                {
                    var at = (await db.Clicks.MaxAsync(c => (DateTime?)c.TimeStamp) ?? DateTime.UtcNow).AddMinutes(10);
                    var href = "https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε.pdf";
                    var batch = ClicksOf(
                        TestClick(hit.page_request_id, at, "Contoso A", href),
                        TestClick(hit.page_request_id, at, "Contoso B", href));

                    await batch.SaveClicksToSQL(logger, db);
                    await batch.SaveClicksToSQL(logger, db);

                    Assert.AreEqual(1, await db.Clicks.CountAsync(c => c.HitID == hit.ID));
                }
                finally
                {
                    await DeleteClicksOf(db, hit);
                }
            }
        }

        /// <summary>
        /// The tracker can report the time on a page more than once (two tabs reading the same exit cookie, a resent
        /// batch). The stored time used to be whichever update ran last; it is now the longest reported.
        /// </summary>
        [TestMethod]
        public async Task PageExitEvents_RepeatedForOnePageRequest_StoreTheLongestTime_AndAReplayChangesNothing()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = new AnalyticsEntitiesContext())
            {
                var hit = await CreateTestHitAsync(db);

                var exits = new CustomEventsResultCollection();
                exits.Rows.Add(new PageExitEventAppInsightsQueryResult { CustomProperties = new PageExitCustomProps { PageRequestId = hit.page_request_id, ActiveTime = 12.5 } });
                exits.Rows.Add(new PageExitEventAppInsightsQueryResult { CustomProperties = new PageExitCustomProps { PageRequestId = hit.page_request_id, ActiveTime = 5 } });

                await exits.SaveHitsUpdatesToSQL(logger, db);

                Assert.AreEqual(12.5, await db.hits.Where(h => h.ID == hit.ID).Select(h => h.seconds_on_page).SingleAsync());

                // Every cycle re-reads the whole day, so the same exits come round again: nothing should be rewritten.
                Assert.AreEqual(0, await exits.SaveHitsUpdatesToSQL(logger, db));
                Assert.AreEqual(12.5, await db.hits.Where(h => h.ID == hit.ID).Select(h => h.seconds_on_page).SingleAsync());
            }
        }

        /// <summary>
        /// Tracker session ids are case-sensitive (dbo.sessions.ai_session_id has a CS collation). The search merge
        /// used to join on LOWER() of both sides, which linked a search to every session differing only by case.
        /// </summary>
        [TestMethod]
        public async Task Searches_AreLinkedOnlyToTheSessionWithExactlyTheSameTrackerId()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var token = Guid.NewGuid().ToString("N");
                var user = new User { UserPrincipalName = $"searcher-{token}@contoso.com" };
                db.users.Add(user);
                var trackerSessionId = "session-" + token;
                var exactSession = new UserSession { ai_session_id = trackerSessionId, user = user };
                db.sessions.Add(exactSession);
                db.sessions.Add(new UserSession { ai_session_id = trackerSessionId.ToUpperInvariant(), user = user });
                await db.SaveChangesAsync();

                var searchTerm = "Καλημέρα κόσμε " + token;
                var searches = new CustomEventsResultCollection();
                var search = new SearchEventAppInsightsQueryResult { AppInsightsTimestamp = DateTime.UtcNow, Username = user.UserPrincipalName };
                search.CustomProperties.SearchText = searchTerm;
                search.CustomProperties.SessionId = trackerSessionId;
                searches.Rows.Add(search);

                await searches.SaveSearchesToSQL(AnalyticsLogger.ConsoleOnlyTracer(), db);

                var saved = await db.searches.Where(s => s.search_term.search_term == searchTerm).Select(s => s.session.ID).ToListAsync();
                CollectionAssert.AreEqual(new[] { exactSession.ID }, saved);
            }
        }

        private static async Task<Hit> CreateTestHitAsync(AnalyticsEntitiesContext db)
        {
            var token = Guid.NewGuid().ToString("N");
            var user = new User { UserPrincipalName = "testuser" + token };
            var session = new UserSession { ai_session_id = Guid.NewGuid().ToString(), user = user };
            var hit = new Hit { session = session, page_request_id = Guid.NewGuid(), hit_timestamp = DateTime.UtcNow, url = new Url { FullUrl = "http://contoso/" + token } };
            db.hits.Add(hit);
            await db.SaveChangesAsync();
            return hit;
        }

        private static ClickEventAppInsightsQueryResult TestClick(Guid pageRequestId, DateTime at, string linkText = "Contoso link", string href = null)
        {
            return new ClickEventAppInsightsQueryResult
            {
                CustomProperties = new ClickCustomProps { LinkText = linkText, HRef = href, PageRequestId = pageRequestId },
                AppInsightsTimestamp = at,
            };
        }

        private static CustomEventsResultCollection ClicksOf(params ClickEventAppInsightsQueryResult[] clicks)
        {
            var events = new CustomEventsResultCollection();
            events.Rows.AddRange(clicks);
            return events;
        }

        /// <summary>Leaves no test click behind - a future-dated one in particular - for the other click tests.</summary>
        private static async Task DeleteClicksOf(AnalyticsEntitiesContext db, params Hit[] hits)
        {
            foreach (var hit in hits)
            {
                await db.Database.ExecuteSqlCommandAsync("DELETE FROM dbo.hits_clicked_elements WHERE hit_id = @p0", hit.ID);
            }
        }

        [TestMethod]
        public async Task ClickEventEdgeCaseTests()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = new AnalyticsEntitiesContext())
            {
                var randoTestName = "TestName" + DateTime.Now.Ticks;
                db.Clicks.RemoveRange(db.Clicks.ToList());
                await db.SaveChangesAsync();

                await TestEdgeClicks(db, logger, randoTestName, null);
                await TestEdgeClicks(db, logger, randoTestName, null);     // Use existing lookup

                // Generate 4k(ish) string
                var sb = "";
                for (int i = 0; i < 4096; i++)
                {
                    sb += ("a");
                }
                await TestEdgeClicks(db, logger, randoTestName, sb);

            }
        }

        private async Task TestEdgeClicks(AnalyticsEntitiesContext db, ILogger logger, string randoTestName, string classname)
        {
            var clicksToSave = new CustomEventsResultCollection();

            // Insert required hit & session for clicks to work
            var testUser = new User { UserPrincipalName = "testuser" + DateTime.Now.Ticks };
            var testSesh = new UserSession() { ai_session_id = Guid.NewGuid().ToString(), user = testUser };
            var testHit = new Hit() { session = testSesh, page_request_id = Guid.NewGuid(), hit_timestamp = DateTime.Now, url = new Url { FullUrl = "http://whatever/" + DateTime.Now.Ticks } };
            db.hits.Add(testHit);
            await db.SaveChangesAsync();

            var lastClickBeforeTest = await db.Clicks.OrderByDescending(h => h.ID).Take(1).FirstOrDefaultAsync() ?? new Common.Entities.Entities.WebTraffic.Clicks();

            // Insert edge-case clicks (with nulls)
            var clickLinkTextOnly = new ClickEventAppInsightsQueryResult()
            {
                CustomProperties = new ClickCustomProps()
                {
                    LinkText = randoTestName,
                    ClassNames = classname,
                    PageRequestId = testHit.page_request_id
                },
                AppInsightsTimestamp = DateTime.Now,
            };
            clicksToSave.Rows.Add(clickLinkTextOnly);

            var expectedClicks = clicksToSave.Rows.Count;

            // Shave
            await clicksToSave.SaveClicksToSQL(logger, db);

            var lastClickAfterTest = await db.Clicks.OrderByDescending(h => h.ID).Take(1).FirstOrDefaultAsync() ?? new Common.Entities.Entities.WebTraffic.Clicks() { ID = int.MaxValue };

            // Load & verify
            var clicksInserted = await db.Clicks
                .Where(h => h.ID > lastClickBeforeTest.ID && h.ID <= lastClickAfterTest.ID)
                .Include(h => h.Title)
                .ToListAsync();

            Assert.IsTrue(clicksInserted.Count == expectedClicks);
        }

        [TestMethod]
        public async Task SearchGreekLetterTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Insert pre-req data
                var testUser = new User()
                {
                    UserPrincipalName = "Test user " + DateTime.Now.Ticks
                };
                db.users.Add(testUser);

                var session = new UserSession() { ai_session_id = Guid.NewGuid().ToString(), user = testUser };

                db.sessions.Add(session);
                await db.SaveChangesAsync();

                var searches = new CustomEventsResultCollection();

                var search = new SearchEventAppInsightsQueryResult();
                search.AppInsightsTimestamp = DateTime.Now;

                // Greek letters
                string SEARCH_TEXT = "ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠ " + DateTime.Now.Ticks;

                search.CustomProperties.SearchText = SEARCH_TEXT;
                search.CustomProperties.SessionId = session.ai_session_id;
                search.Username = testUser.UserPrincipalName;
                searches.Rows.Add(search);

                var preSaveSearchesCount = await db.searches.CountAsync();
                var preSaveSearchTermsCount = await db.search_terms.CountAsync();

                await searches.SaveSearchesToSQL(AnalyticsLogger.ConsoleOnlyTracer(), db);


                var postSaveSearchesCount = await db.searches.CountAsync();
                var postSaveSearchTermsCount = await db.search_terms.CountAsync();

                Assert.IsTrue(postSaveSearchesCount == preSaveSearchesCount + 1);
                Assert.IsTrue(postSaveSearchTermsCount == preSaveSearchTermsCount + 1);

                // Make sure search term is saved exactly as expected, greek chars and all
                var lastSearch = await db.searches.Include(s => s.search_term).OrderByDescending(s => s.ID).FirstOrDefaultAsync();
                Assert.IsTrue(lastSearch.search_term.search_term == SEARCH_TEXT);
            }
        }
    }
}
