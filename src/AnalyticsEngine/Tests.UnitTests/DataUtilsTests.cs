using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.LookupCaches;
using DataUtils;
using DataUtils.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeEntities;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;

namespace Tests.UnitTests
{
    [TestClass]
    public class DataUtilsTests
    {
        [TestMethod]
        public void IsValidUrlTests()
        {
            Assert.IsFalse(StringUtils.IsValidAbsoluteUrl(null));
            Assert.IsFalse(StringUtils.IsValidAbsoluteUrl(""));
            Assert.IsFalse(StringUtils.IsValidAbsoluteUrl("asdfasdf"));
            Assert.IsFalse(StringUtils.IsValidAbsoluteUrl("http://"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("http://asdfasdf"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("http://asdfasdf.com"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("http://asdfasdf.com/"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("http://asdfasdf.com/asdfasdf"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("http://asdfasdf.com/asdfasdf/"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("https://contoso.sharepoint.com/sites/site1/_layouts/15/Doc.aspx?sourcedoc=%7BF2CB77E7-186C-4F9A-B949-FA078F48AA53%7D&file=RD%20Consejer%C3%ADa%20en%20el%20exterior%20v.1.docx&action=default&mobileredirect=true"));
        }

        [TestMethod]
        public void DatabaseUpgraderTests()
        {
            var config = new AppConfig();
            var initInfo = new DatabaseUpgradeInfo { ConnectionString = config.ConnectionStrings.DatabaseConnectionString };

            config.ConnectionStrings.TestSQLSettings(AnalyticsLogger.ConsoleOnlyTracer());
            DatabaseUpgrader.CheckDbUpgraded(initInfo, (s) => Console.WriteLine(s));
        }

        [TestMethod]
        public async Task ParallelCallsForSingleReturnListHanderTests()
        {
            var p = new ParallelCallsForSingleReturnListHander<int, string>();

            // Test we get the same number of results back as went in
            var result = await p.CallAndCompileToSingleList(new List<int> { 1, 2, 3, 4, 5 }, (chunk) =>
            {
                var l = new List<string>();
                foreach (var i in chunk)
                {
                    l.Add(i.ToString());
                }
                return Task.FromResult(l);
            }, 2);

            Assert.IsTrue(result.Count == 5);
        }

        [TestMethod]
        public void GetUrlBaseAddressIfValidUrl()
        {
            // Test GetUrlBaseAddress
            Assert.IsNull(StringUtils.GetUrlBaseAddressIfValidUrl(null));
            Assert.AreEqual(StringUtils.GetUrlBaseAddressIfValidUrl(""), string.Empty);
            Assert.AreEqual(StringUtils.GetUrlBaseAddressIfValidUrl("123"), "123");

            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com")
                == "https://contoso.sharepoint.com/");      // Will add trailing slash

            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/")
                == "https://contoso.sharepoint.com/");

            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/sites/Doc1.aspx")
                == "https://contoso.sharepoint.com/sites/Doc1.aspx");

            // Clean up querystring & bookmarks
            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/sites/Doc1.aspx?query1=val")
                == "https://contoso.sharepoint.com/sites/Doc1.aspx");
            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/sites/Doc1.aspx?")
                == "https://contoso.sharepoint.com/sites/Doc1.aspx");

            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/sites/Doc1.aspx#bookmark")
                == "https://contoso.sharepoint.com/sites/Doc1.aspx");
            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/sites/Doc1.aspx#")
                == "https://contoso.sharepoint.com/sites/Doc1.aspx");
        }

        [TestMethod]
        public void ConvertSharePointUrl()
        {
            const string expectedResult = "https://contoso.sharepoint.com/sites/site/Shared%20Documents/";

            const string input = "https://contoso.sharepoint.com/sites/site/Shared Documents/";
            Assert.IsFalse(Uri.IsWellFormedUriString(input, UriKind.Absolute));
            Assert.IsTrue(StringUtils.ConvertSharePointUrl(input) == expectedResult);
            Assert.IsTrue(Uri.IsWellFormedUriString(expectedResult, UriKind.Absolute));

        }
        [TestMethod]
        public void IUrlObjectResolver()
        {
            var urls = new List<Url>()
            {
                new Url { FullUrl = "https://server/SITE1" },
                new Url { FullUrl = "https://server/site1/sub1" },
                new Url { FullUrl = "https://server/site2" },
                null
            };

            Assert.IsTrue(urls.GetClosest("https://server/site1/doc.aspx").FullUrl.ToLower() == "https://server/site1");
            Assert.IsTrue(urls.GetClosest("https://server/site1/sub1/doc.aspx").FullUrl == "https://server/site1/sub1");
            Assert.IsNull(urls.GetClosest("https://server/site3/doc.aspx"));

        }

        [TestMethod]
        public async Task CacheEncodingTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var c = new UserJobTitleCache(db);

                const string n1 = "GEsundheits- und Krankenpfleger im Aussendienst";
                const string n2 = "Gesundheits- und Krankenpfleger im Außendienst";     // Rando German char.
                                                                                        // In SQL it's the same as above name, even though it's not in .Net normally
                Assert.AreNotEqual(n1, n2);

                var jobTitle = await c.GetOrCreateNewResource(n1, new UserJobTitle { Name = n1 });
                var jobTitle2 = await c.GetOrCreateNewResource(n2, new UserJobTitle { Name = n2 });

                await db.SaveChangesAsync();

                Assert.AreSame(jobTitle, jobTitle2);
            }
        }

        [TestMethod]
        public async Task ObjectByIdCacheTests()
        {
            var c = new TestObjectByIdCache();
            await Assert.ThrowsExceptionAsync<ArgumentException>(async () => await c.GetResource(null));

            var id1 = Guid.NewGuid().ToString();

            // Not found yet
            await Assert.ThrowsExceptionAsync<ArgumentException>(async () => await c.GetResource(id1));

            // Normie execution
            c.AddId(id1.ToUpper());
            var r = await c.GetResource(id1);
            Assert.IsNotNull(r);

            var r2 = await c.GetResource(id1);
            Assert.AreSame(r, r2);

            // Call again but override the "not found anywhere" callback
            var id2 = Guid.NewGuid().ToString();
            var r3 = await c.GetResource(id2, () => Task.FromResult(r2));
            Assert.AreSame(r2, r3);

            // Id2 should now be cached. Get with UPPER
            var r4 = await c.GetResource(id2.ToUpper());
            Assert.AreSame(r4, r3);

            var nullResult = await c.GetResourceOrNullIfNotExists(Guid.NewGuid().ToString());
            Assert.IsNull(nullResult);

            // Test concurrent access (verifies SemaphoreSlim deadlock fix)
            var concurrentCache = new TestObjectByIdCache();
            var concurrentIds = new List<string>();
            for (int i = 0; i < 50; i++)
            {
                var id = Guid.NewGuid().ToString();
                concurrentIds.Add(id);
                concurrentCache.AddId(id);
            }

            // Access cache from multiple threads simultaneously
            var tasks = new List<Task<TestObject>>();
            foreach (var id in concurrentIds)
            {
                tasks.Add(Task.Run(async () => await concurrentCache.GetResource(id)));
            }

            var results = await Task.WhenAll(tasks);
            Assert.AreEqual(concurrentIds.Count, results.Length);
            Assert.IsTrue(results.All(result => result != null));

            // Test concurrent access with callback (verifies no deadlock with async callback)
            var callbackCache = new TestObjectByIdCache();
            var callbackTasks = new List<Task<TestObject>>();
            var callbackIds = new List<string>();

            for (int i = 0; i < 20; i++)
            {
                var id = Guid.NewGuid().ToString();
                callbackIds.Add(id);

                // Simulate async load with delay
                callbackTasks.Add(Task.Run(async () =>
                    await callbackCache.GetResource(id, async () =>
                    {
                        await Task.Delay(10); // Simulate async work
                        return new TestObject();
                    })));
            }

            var callbackResults = await Task.WhenAll(callbackTasks);
            Assert.AreEqual(callbackIds.Count, callbackResults.Length);
            Assert.IsTrue(callbackResults.All(result => result != null));

            // Verify objects are properly cached after concurrent callback creation
            foreach (var id in callbackIds)
            {
                var cached = await callbackCache.GetResource(id);
                Assert.IsNotNull(cached);
            }
        }
        class TestObjectByIdCache : ObjectByIdCache<TestObject>
        {
            List<string> ids = new List<string>();
            public override Task<TestObject> Load(string id)
            {
                if (!ids.Contains(id.ToLower()))
                {
                    TestObject nullObj = null;
                    return Task.FromResult(nullObj);
                }
                return Task.FromResult(new TestObject());
            }

            public void AddId(string id)
            {
                ids.Add(id.ToLower());
            }
        }
        class TestObject
        {
        }

        [TestMethod]
        public void JsonDecodeFromPropValueString()
        {
            // Test string needing decoding
            var obj = StringUtils.JsonDecodeFromPropValueString(Properties.Resources.StringifiedJson);
            Assert.IsNotNull(obj);

            // Normal json but with some prop with stringified (no decoding needed)
            const string str = @"{""PublishingPageContent"": ""<h2>Welcome to Your Publishing Site</h2><h3 class=\""ms-rteElement-H3\"">These links will help you get started.</h3>""}";
            obj = StringUtils.JsonDecodeFromPropValueString(str);
            Assert.IsNotNull(obj);
        }

        [TestMethod]
        public async Task ListBatchProcessor()
        {
            // Test BufferSize moves
            var listBatchProcessor = new ListBatchProcessor<int>(10, (newChunk) => Task.CompletedTask);
            await listBatchProcessor.Add(1);
            Assert.IsTrue(listBatchProcessor.BufferSize > 0);

            await TestListBatch(1);
            await TestListBatch(100);
            await TestListBatch(101);
        }

        async Task TestListBatch(int size)
        {

            var resultList = new List<int>();
            var listBatchProcessor = new ListBatchProcessor<int>(10, (newChunk) =>
            {
                resultList.AddRange(newChunk);
                return Task.CompletedTask;
            });

            Assert.IsTrue(listBatchProcessor.BufferSize == 0);

            // Add direct to ListBatchProcessor, multithreaded
            var tasks = new List<Task>();
            for (int i = 0; i < size; i++)
            {
                int index = i;
                tasks.Add(Task.Run(async () => await listBatchProcessor.Add(index)));
            }
            await Task.WhenAll(tasks);

            var intList = new List<int>();
            for (int i = 0; i < size; i++)
            {
                intList.Add(i);
            }

            await listBatchProcessor.Flush();
            Assert.IsTrue(listBatchProcessor.BufferSize == 0);
            Assert.IsTrue(resultList.Count == intList.Count);
            foreach (var a in resultList)
            {
                Assert.IsTrue(intList.Contains(a));
            }

            // Reset and test again via Add(List<T>)
            resultList.Clear();
            await listBatchProcessor.AddRange(intList);
            await listBatchProcessor.Flush();
            Assert.IsTrue(resultList.Count == intList.Count);
        }

        #region InsertBatchTests

        const string TABLE_NAME = "tmp_whatever";
        const string TEMP_TABLE_NAME = "##tmp_whatever";
        [TestMethod]
        public async Task InsertBatchTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Normal table
                var batch = new EFInsertBatch<TestMultiPropTypeTempEntity>(db, AnalyticsLogger.ConsoleOnlyTracer());

                // Verify prop cache
                Assert.IsTrue(batch.PropCache.PropertyMappingInfo.Count > 0);

                // Save empty
                var emptyResult = await batch.SaveToStagingTable(string.Empty);
                Assert.IsTrue(emptyResult == 0);

                // Save with data
                batch.Rows.Add(new TestMultiPropTypeTempEntity());// Use class defaults
                batch.Rows.Add(new TestMultiPropTypeTempEntity { NullableIntProp = 1 });
                await batch.SaveToStagingTable($"select * from {TABLE_NAME}");

                // Verify saved data
                var selectCmd = db.Database.Connection.CreateCommand();
                await db.Database.Connection.OpenAsync();
                selectCmd.CommandText = $"select * from {TABLE_NAME}";
                var results = await selectCmd.ExecuteReaderAsync();

                // x2 records
                int count = 0;
                while (results.Read())
                {
                    // Recheck each property
                    foreach (var pi in batch.PropCache.PropertyMappingInfo)
                    {
                        var sqlFieldResult = results[pi.SqlInfo.FieldName];
                        Assert.IsNotNull(sqlFieldResult);

                        var objectVal = pi.Property.GetValue(batch.Rows[count]);
                        if (sqlFieldResult is double)
                        {
                            if (pi.Property.PropertyType == typeof(double))
                            {
                                Assert.AreEqual(sqlFieldResult, objectVal);
                            }
                            else if (pi.Property.PropertyType == typeof(float))
                            {
                                // SQL reader interprets SQL "float" fields as doubles. If the original prop is a float, the double read from SQL will not match unless rounded back to float
                                Assert.AreEqual(Convert.ToSingle(sqlFieldResult), objectVal);
                            }
                            else
                            {
                                throw new NotSupportedException("Unknown property type");
                            }
                        }
                        else if (sqlFieldResult is DBNull)
                        {
                            // Nulls are never read back as literal nulls...
                            Assert.IsTrue(objectVal == null);
                        }
                        else
                        {
                            Assert.AreEqual(sqlFieldResult, objectVal);
                        }
                    }
                    count++;
                }

                Assert.IsTrue(count == batch.Rows.Count);
                db.Database.Connection.Close();

                // Temp table tests
                var batchTemp = new EFInsertBatch<TestTempEntityTempTable>(db, AnalyticsLogger.ConsoleOnlyTracer());

                // Save empty
                await batchTemp.SaveToStagingTable(string.Empty);

                // Save with data over several threads
                for (int i = 0; i < 105; i++)
                {
                    batchTemp.Rows.Add(new TestTempEntityTempTable { Prop = "whatever " + i });
                }
                var tempUpdates = await batchTemp.SaveToStagingTable(10, $"delete from {TEMP_TABLE_NAME}");

                // Can't verify saved data as temp table will be closed
                Assert.IsTrue(tempUpdates == batchTemp.Rows.Count);
            }
        }
        [TempTableName(TABLE_NAME)]
        class TestMultiPropTypeTempEntity
        {
            [Column("guid")]
            public Guid Id { get; set; } = Guid.NewGuid();

            [Column("prop")]
            public string Prop { get; set; } = "Whatever";

            /// <summary>
            /// Specifically override the nullability of this property
            /// </summary>
            [Column("nullstringprop", true)]
            public string NullProp { get; set; } = null;

            [Column("intprop")]
            public int IntProp { get; set; } = 1;

            /// <summary>
            /// This should automatically be nullable without needing to specify it in the attribute
            /// </summary>
            [Column("nullable_intprop")]
            public int? NullableIntProp { get; set; } = null;

            [Column("floatprop")]
            public float FloatProp { get; set; } = 1.001f;

            [Column("doubleprop")]
            public double DoubleProp { get; set; } = -1.001f;

            [Column("boolprop")]
            public bool BoolProp { get; set; } = true;

            /// <summary>
            /// This should automatically be nullable without needing to specify it in the attribute
            /// </summary>
            [Column("nullable_boolprop")]
            public bool? NullableBoolProp { get; set; } = null;

            [Column("nullable_boolprop_with_value")]
            public bool? NullableBoolPropWithValue { get; set; } = false;

            [Column("prop2", ColationOverride = "SQL_Latin1_General_CP1_CS_AS")]
            public string CaseSensitiveProp { get; set; } = "OhHey";

            [Column("date")]
            public DateTime Timestamp { get; set; } = DateTime.Now;


            [Column("date_nullable")]
            public DateTime? NullableTimestamp { get; set; } = null;
        }

        [TempTableName(TEMP_TABLE_NAME)]
        class TestTempEntityTempTable
        {
            [Column("prop")]
            public string Prop { get; set; }
        }

        #endregion

        // Basic sanity for JobTimer
        [TestMethod]
        public void JobTimerTests()
        {
            var trace = AnalyticsLogger.ConsoleOnlyTracer();
            var t = new JobTimer(trace, "Unit test");
            Assert.IsTrue(t.Elapsed == TimeSpan.Zero);

            t.Start();
            Thread.Sleep(10);
            Assert.IsTrue(t.Elapsed > TimeSpan.Zero);

            Thread.Sleep(1000);

            var outStr = t.PrintElapsed();
            Assert.IsTrue(outStr.Contains("1 seconds"));

            Assert.IsTrue(t.Elapsed > TimeSpan.FromSeconds(1));

            outStr = t.StopAndPrintElapsed();
            Assert.IsNotNull(outStr);
            Assert.IsTrue(t.Elapsed == TimeSpan.Zero);

            t.Start();
            t.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedImportCycle);
            Assert.IsTrue(t.Elapsed == TimeSpan.Zero);
        }

        [TestMethod]
        public void TrimStringFromStart()
        {
            var s1 = "string and that";
            const string EXTRA = " plus some extra bits";
            var s2 = s1 + EXTRA;
            var trimmed = s2.TrimStringFromStart(s1);

            Assert.IsTrue(trimmed == EXTRA);

            Assert.ThrowsException<ArgumentException>(() => "randoString".TrimStringFromStart(EXTRA));
        }

        [TestMethod]
        public void IsJson()
        {
            Assert.IsFalse(StringUtils.IsJson("false"));
            Assert.IsTrue(StringUtils.IsJson("{}"));
            Assert.IsTrue(StringUtils.IsJson("{ \"prop\": \"val\", \"test\": true }"));
            Assert.IsTrue(StringUtils.IsJson("[]"));
            Assert.IsTrue(StringUtils.IsJson("[ { \"prop\": \"val\"} ]"));
        }

        [TestMethod]
        public void StringProtectTests()
        {
            var randomString = "String" + DateTime.Now.Ticks;
            var encryptedPayload = StringUtils.ProtectString(randomString);
            //Assert.AreNotEqual(randomString, encryptedPayload);
            var unencryptedString = StringUtils.UnprotectString(encryptedPayload);
            Assert.AreEqual(randomString, unencryptedString);

            // Should fail
            Assert.ThrowsException<CryptographicException>(() => StringUtils.UnprotectString(System.Text.Encoding.UTF8.GetBytes("asdfasdf")));

        }
        [TestMethod]
        public void SecureLocalPreferencesTests()
        {
            var p = new UnitTestsPreferences() { NumberVal = DateTime.Now.Ticks, StringVal = "Whatever " + DateTime.Now.Ticks };

            var savedP = p.SaveToTempFile();
            Assert.IsNotNull(savedP);
            Assert.IsTrue(System.IO.File.Exists(savedP.FullName));

            var loadedP = SecureLocalPreferences.Load<UnitTestsPreferences>();
            Assert.AreEqual(loadedP.NumberVal, p.NumberVal);
            Assert.AreEqual(loadedP.StringVal, p.StringVal);

            // Test with no local saved data. Should return new object anyway
            p.DeleteTempFile();
            SecureLocalPreferences.Load<UnitTestsPreferences>();
        }
        public class UnitTestsPreferences : SecureLocalPreferences
        {
            public string StringVal { get; set; }
            public long NumberVal { get; set; }

            protected override string FileTitle => "test.dat";
        }


        [TestMethod]
        public void UrlFilterTests()
        {
            var orgUrlConfigs = new List<FilterUrlConfig>();

            string ROOT_SITE_PREFIX1 = "https://unittesting.sharepoint.local";

            // Test empty
            Assert.IsTrue(orgUrlConfigs.UrlInScope(ROOT_SITE_PREFIX1, ROOT_SITE_PREFIX1.ToUpper() + "/file"));
            Assert.IsTrue(orgUrlConfigs.UrlInScope(String.Empty, ROOT_SITE_PREFIX1.ToUpper() + "/file"));

            // Add 1st rule
            orgUrlConfigs.Add(new FilterUrlConfig { Url = ROOT_SITE_PREFIX1 });

            // Mix case test
            Assert.IsTrue(orgUrlConfigs.UrlInScope(ROOT_SITE_PREFIX1, ROOT_SITE_PREFIX1.ToUpper() + "/file"));

            // Child site URL of a rule and exact match not specified, so should match
            Assert.IsTrue(orgUrlConfigs.UrlInScope(ROOT_SITE_PREFIX1 + "/subsite", ROOT_SITE_PREFIX1 + "/subsite/file"));

            Assert.IsFalse(orgUrlConfigs.UrlInScope("http://urlfromanothersite", "http://urlfromanothersite/page"));


            string ROOT_SITE_PREFIX2 = "https://unittesting2.sharepoint.local";
            orgUrlConfigs.Add(new FilterUrlConfig { Url = ROOT_SITE_PREFIX2, ExactSiteMatch = true });

            Assert.IsTrue(orgUrlConfigs.UrlInScope(ROOT_SITE_PREFIX2, ROOT_SITE_PREFIX2 + "/file"));

            // Child site URL of a rule but exact match specified, so should not match. Mix CASES
            Assert.IsFalse(orgUrlConfigs.UrlInScope(ROOT_SITE_PREFIX2.ToUpper() + "/subsite", ROOT_SITE_PREFIX2 + "/subsite/file"));

            // Test root SC scenario
            var rootSCUrlConfigs = new List<FilterUrlConfig>();

            rootSCUrlConfigs.Add(new FilterUrlConfig { ExactSiteMatch = true, Url = ROOT_SITE_PREFIX1 });
            rootSCUrlConfigs.Add(new FilterUrlConfig { ExactSiteMatch = false, Url = $"{ROOT_SITE_PREFIX1}/sites/site1" });

            // Subsite of root1 rule (exact). Should not be in scope.
            Assert.IsFalse(rootSCUrlConfigs.UrlInScope($"{ROOT_SITE_PREFIX1}/sites/site2", $"{ROOT_SITE_PREFIX1}/sites/site2/file"));

            Assert.IsTrue(rootSCUrlConfigs.UrlInScope(ROOT_SITE_PREFIX1, ROOT_SITE_PREFIX1 + "/file"));

            // Another site not in rules. Not in scope.
            Assert.IsFalse(rootSCUrlConfigs.UrlInScope(ROOT_SITE_PREFIX2, ROOT_SITE_PREFIX2 + "/file"));

            // Site-collection has no exact match req. Should be in scope.
            Assert.IsTrue(rootSCUrlConfigs.UrlInScope($"{ROOT_SITE_PREFIX1}/sites/site1/sub1", $"{ROOT_SITE_PREFIX1}/sites/site1/sub1/file"));
        }

        [TestMethod]
        public void ImportJobSettingsTests()
        {
            Assert.IsTrue(new ImportTaskSettings().Equals(new ImportTaskSettings()));

            // All [ImportProp] flags default to false (opt-in). Enabling a subset must not equal a fresh default.
            var someEnabled = new ImportTaskSettings() { GraphTeams = true, GraphUsageReports = true, GraphUserApps = true, GraphUsersMetadata = true };
            Assert.IsFalse(someEnabled.Equals(new ImportTaskSettings()));

            var someEnabled2 = new ImportTaskSettings(someEnabled.ToSettingsString());
            Assert.IsTrue(someEnabled2.Equals(someEnabled));

            // Check doesn't blow up
            new ImportTaskSettings(null);
        }

        [TestMethod]
        public void TimePeriodTests()
        {
            var d1 = DateTime.ParseExact("01-01-2019 09:00:00", "dd-MM-yyyy HH:mm:ss", null);
            var d2 = DateTime.ParseExact("01-01-2019 09:00:01", "dd-MM-yyyy HH:mm:ss", null);
            var bothDTsSame = new TimePeriod(d1, d1);

            Assert.IsTrue(bothDTsSame.InRange(d1));
            Assert.IsFalse(bothDTsSame.InRange(DateTime.ParseExact("01-01-2019 09:00:01", "dd-MM-yyyy HH:mm:ss", null)));

            var differentDts = new TimePeriod(d1, d2);
            Assert.IsTrue(differentDts.InRange(d1));
            Assert.IsTrue(differentDts.InRange(d2));


            var d1Plus3days = d1.AddDays(3);
            var totalTestTP = new TimePeriod(d1, d1Plus3days);

            var dayChunks = TimePeriod.GetScanningTimeChunksFrom(d1, d1Plus3days);
            foreach (var dayChunk in dayChunks)
            {
                Assert.IsTrue(totalTestTP.InRange(dayChunk.Start));
                Assert.IsTrue(totalTestTP.InRange(dayChunk.End));
            }

            // Test invalid range throws exception
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => TimePeriod.GetScanningTimeChunksFrom(d2, d1));

            // Test that chunks are 24 hours apart (no overlap)
            var dayChunksNoOverlap = TimePeriod.GetScanningTimeChunksFrom(d1, d1Plus3days, 0);
            if (dayChunksNoOverlap.Count > 1)
            {
                for (int i = 0; i < dayChunksNoOverlap.Count - 1; i++)
                {
                    var chunk1End = dayChunksNoOverlap[i].End;
                    var chunk2Start = dayChunksNoOverlap[i + 1].Start;
                    Assert.AreEqual(chunk1End, chunk2Start, "Chunks should be consecutive with no overlap");
                }
            }
        }

        [TestMethod]
        public void TimePeriodOverlapTests()
        {
            // Test with 5-minute overlap
            var startDate = DateTime.ParseExact("01-01-2019 00:00:00", "dd-MM-yyyy HH:mm:ss", null);
            var endDate = startDate.AddDays(3);
            const int OVERLAP_MINUTES = 5;

            var chunksWithOverlap = TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, OVERLAP_MINUTES);

            // Verify chunks overlap by exactly 5 minutes
            Assert.IsTrue(chunksWithOverlap.Count > 1, "Should have multiple chunks for 3-day period");

            for (int i = 0; i < chunksWithOverlap.Count - 1; i++)
            {
                var chunk1 = chunksWithOverlap[i];
                var chunk2 = chunksWithOverlap[i + 1];

                // The second chunk should start 5 minutes before the first chunk ends
                var expectedChunk2Start = chunk1.End.AddMinutes(-OVERLAP_MINUTES);
                Assert.AreEqual(expectedChunk2Start, chunk2.Start,
                    $"Chunk {i + 1} should start {OVERLAP_MINUTES} minutes before chunk {i} ends");

                // Verify there is actually an overlap window
                var overlapStart = chunk2.Start;
                var overlapEnd = chunk1.End;
                Assert.IsTrue(overlapStart < overlapEnd, "Chunks should have overlapping time period");
                Assert.AreEqual(OVERLAP_MINUTES, (overlapEnd - overlapStart).TotalMinutes,
                    $"Overlap should be exactly {OVERLAP_MINUTES} minutes");

                // Verify that a time in the overlap period is in range for both chunks
                var timeInOverlap = overlapStart.AddMinutes(2);
                Assert.IsTrue(chunk1.InRange(timeInOverlap), "Time in overlap should be in first chunk");
                Assert.IsTrue(chunk2.InRange(timeInOverlap), "Time in overlap should be in second chunk");
            }

            // Test with zero overlap (should behave like original)
            var chunksNoOverlap = TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, 0);
            var chunksDefault = TimePeriod.GetScanningTimeChunksFrom(startDate, endDate);

            Assert.AreEqual(chunksNoOverlap.Count, chunksDefault.Count, "Zero overlap should match default behavior");
            for (int i = 0; i < chunksNoOverlap.Count; i++)
            {
                Assert.AreEqual(chunksNoOverlap[i].Start, chunksDefault[i].Start);
                Assert.AreEqual(chunksNoOverlap[i].End, chunksDefault[i].End);
            }

            // Test with larger overlap (30 minutes)
            var chunksLargeOverlap = TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, 30);
            Assert.IsTrue(chunksLargeOverlap.Count > 1, "Should have multiple chunks");

            if (chunksLargeOverlap.Count > 1)
            {
                var firstChunk = chunksLargeOverlap[0];
                var secondChunk = chunksLargeOverlap[1];
                var expectedStart = firstChunk.End.AddMinutes(-30);
                Assert.AreEqual(expectedStart, secondChunk.Start, "Should have 30-minute overlap");
            }

            // Test edge case: very short time period
            var shortPeriodStart = startDate;
            var shortPeriodEnd = startDate.AddHours(12);
            var shortPeriodChunks = TimePeriod.GetScanningTimeChunksFrom(shortPeriodStart, shortPeriodEnd, 5);

            // A 12-hour period should result in 1 chunk (the cleanup only removes chunks < 1 hour)
            Assert.IsTrue(shortPeriodChunks.Count == 1, "12-hour period should result in 1 chunk");
            Assert.AreEqual(12, (shortPeriodChunks[0].End - shortPeriodChunks[0].Start).TotalHours, "Chunk should be 12 hours long");

            // Test very short period (< 1 hour) - should be removed
            var veryShortPeriodStart = startDate;
            var veryShortPeriodEnd = startDate.AddMinutes(30);
            var veryShortPeriodChunks = TimePeriod.GetScanningTimeChunksFrom(veryShortPeriodStart, veryShortPeriodEnd, 5);

            // Should be empty after removal of chunks < 1 hour
            Assert.IsTrue(veryShortPeriodChunks.Count == 0, "Period less than 1 hour should result in no chunks after last chunk removal");

            // Test with negative overlap (should behave as zero overlap - no exception)
            var chunksNegativeOverlap = TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, -10);
            Assert.IsNotNull(chunksNegativeOverlap, "Negative overlap should not throw exception");

            // Should behave same as zero overlap
            Assert.AreEqual(chunksNoOverlap.Count, chunksNegativeOverlap.Count, "Negative overlap should behave as zero overlap");
            if (chunksNegativeOverlap.Count > 1)
            {
                for (int i = 0; i < chunksNegativeOverlap.Count - 1; i++)
                {
                    var chunk1End = chunksNegativeOverlap[i].End;
                    var chunk2Start = chunksNegativeOverlap[i + 1].Start;
                    Assert.AreEqual(chunk1End, chunk2Start, "Negative overlap should behave as no overlap");
                }
            }

            // Test invalid overlap (>= 24 hours should throw)
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, 24 * 60),
                "Overlap >= 24 hours should throw ArgumentOutOfRangeException");

            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, 25 * 60),
                "Overlap > 24 hours should throw ArgumentOutOfRangeException");
        }

        // Define lists of int, and copy list into another one. Verify various lengths
        [TestMethod]
        public async Task ParallelListProcessorTests()
        {
            // Test super-quick tasks. Previously would deadlock.
            var intList = new List<int>();
            for (int i = 0; i < 50; i++)
            {
                intList.Add(i);
            }
            var superQuickListWorkload = new ParallelListProcessor<int>(1);
            await superQuickListWorkload.ProcessListInParallel(intList, (chunk, threadIndex) =>
            {
                // Some task that takes no time
                return Task.CompletedTask;
            });

            await ParallelListProcessorTest(100);   // Even number
            await ParallelListProcessorTest(0);     // Nothing to do
            await ParallelListProcessorTest(1);     // Single thing to do 
            await ParallelListProcessorTest(103);   // Odd number
            await ParallelListProcessorTest(1000);   // Big number
        }
        async Task ParallelListProcessorTest(int size)
        {
            const int MAX_SIZE = 10;
            var intListAll = new List<int>();
            for (int i = 0; i < size; i++)
            {
                intListAll.Add(i);
            }

            var concurrentIntList = new ConcurrentBag<int>();

            var l = new ParallelListProcessor<int>(MAX_SIZE);

            // Add a copy of the list into a new list, inserted in parallel
            await l.ProcessListInParallel(intListAll, async (chunk, threadIndex) =>
            {
                Console.WriteLine($"Processing on thread index {threadIndex}");

                Assert.IsTrue(chunk.Count <= MAX_SIZE);

                foreach (var i in chunk)
                {
                    concurrentIntList.Add(i);
                }

                await Task.Delay(1000);
                Console.WriteLine($"END on thread index {threadIndex}");

            });

            // Verify everything is there
            Assert.AreEqual(intListAll.Count, concurrentIntList.Count);
            foreach (var i in intListAll)
            {
                Assert.IsTrue(concurrentIntList.Contains(i));
            }
        }

        [TestMethod]
        public void ConcurrentLookupDbIdsCacheTests()
        {
            var cache = new ConcurrentLookupDbIdsCache();

            // Sanity 
            Assert.ThrowsException<ArgumentNullException>(() => cache.AddOrUpdateForName<DataUtilsTests>(null, 0));
            Assert.ThrowsException<ArgumentNullException>(() => cache.AddOrUpdateForName<DataUtilsTests>("", 0));

            const int MAX = 10000;

            // Bombard the ConcurrentDictionary with 10000 competing AddOrUpdates
            Parallel.For(0, MAX, i =>
            {
                cache.AddOrUpdateForName<DataUtilsTests>(i.ToString(), i);
            });

            // Do the same to verify all exist fine
            Parallel.For(0, MAX, i =>
            {
                Assert.IsTrue(cache.GetCachedIdForName<DataUtilsTests>(i.ToString()) == i);
            });

            // Check for some other name too
            Assert.IsNull(cache.GetCachedIdForName<AuditTests>(""));
        }

        [TestMethod]
        public void RandomStringGeneratorUniquenessTest()
        {
            // Should take about 10 seconds
            const int LOOPS = 5000000;
            Dictionary<string, object> strings = new Dictionary<string, object>(LOOPS);
            for (int i = 0; i < LOOPS; i++)
            {
                // Will blow up if key isn't unique
                strings.Add(DataGenerators.GetRandomString(20), null);
            }
        }


        //https://github.com/BcryptNet/bcrypt.net
        [TestMethod]
        public void BasicUsernameHashTests()
        {
            const string PWD = "PWD";
            var hash = StringUtils.GetHashedStringWithSalt(PWD);

            var val = StringUtils.IsHashedMatch(PWD, hash);
            Assert.IsTrue(val);
        }


        [TestMethod]
        public void EmaillAddressTests()
        {
            Assert.ThrowsException<ArgumentNullException>(() => StringUtils.IsEmail(null));
            Assert.ThrowsException<ArgumentException>(() => StringUtils.IsEmail(""));
            Assert.IsFalse(StringUtils.IsEmail("asdfasdfasdf"));
            Assert.IsFalse(StringUtils.IsEmail("$2a$11$Jufq8qMclZHKsnVfLHKWGOgx3XT3tmgji9KSZdGiAptDjaDk6b9I6"));
            Assert.IsTrue(StringUtils.IsEmail("sambetts@contoso.com"));
            Assert.IsTrue(StringUtils.IsEmail("sam.betts@b.org"));
        }


        [TestMethod]
        public void EnsureMaxLengthTests()
        {
            var shortString = "test";
            Assert.AreEqual(StringUtils.EnsureMaxLength(shortString, 10), shortString);

            var longString = "$2a$11$Jufq8qMclZHKsnVfLHKWGOgx3XT3tmgji9KSZdGiAptDjaDk6b9I6$2a$11$Jufq8qMclZHKsnVfLHKWGOgx3XT3tmgji9KSZdGiAptDjaDk6b9I6";
            var longStringResult = StringUtils.EnsureMaxLength(longString, 10);
            Assert.AreNotEqual(longStringResult, longString);

            Assert.IsTrue(longStringResult.EndsWith("..."));

            longString = "123456";
            Assert.IsTrue(StringUtils.EnsureMaxLength(longString, 4) == "1...");
        }
    }
}
