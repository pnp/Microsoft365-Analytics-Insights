using ActivityImporter.Engine.ActivityAPI.Copilot;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    /// <summary>
    /// Unit tests for the Copilot file/meeting resolution *logic*, exercised against a fake ISpoGraphClient
    /// (no live Graph). These cover the early-reject, negative caching and driveItemId-vs-URL branches added
    /// for import performance.
    /// </summary>
    [TestClass]
    public class GraphFileMetadataLoaderTests
    {
        private static GraphFileMetadataLoader NewLoader(FakeSpoGraphClient fake)
            => new GraphFileMetadataLoader(fake, AnalyticsLogger.ConsoleOnlyTracer());

        [TestMethod]
        public async Task GetSpoFileInfo_NonSharePointHost_SkipsGraphEntirely()
        {
            var fake = new FakeSpoGraphClient();
            var loader = NewLoader(fake);

            var result = await loader.GetSpoFileInfo("https://securitycopilot.microsoft.com", "user@contoso.com");

            Assert.IsNull(result);
            Assert.AreEqual(0, fake.TotalCalls, "A non-SharePoint context must not trigger any Graph call");
        }

        [TestMethod]
        public async Task GetSpoFileInfo_LocalFilePath_SkipsGraphEntirely()
        {
            var fake = new FakeSpoGraphClient();
            var loader = NewLoader(fake);

            var result = await loader.GetSpoFileInfo(@"C:\Users\x\AppData\Local\Microsoft\Olk\Attachments\file.pdf", "user@contoso.com");

            Assert.IsNull(result);
            Assert.AreEqual(0, fake.TotalCalls, "A local file path must not trigger any Graph call");
        }

        [TestMethod]
        public async Task GetSpoFileInfo_WithDriveItemId_ResolvesViaListItemByIdNotEnumeration()
        {
            var fake = new FakeSpoGraphClient
            {
                OnGetListItemById = (s, l, i) => new ListItem { WebUrl = "https://contoso.sharepoint.com/sites/x/Shared Documents/Report.docx" }
            };
            var loader = NewLoader(fake);

            var ctx = "https://contoso.sharepoint.com/sites/x/_layouts/15/Doc.aspx?sourcedoc=%7B0D86F64F-8435-430C-8979-FF46C00F7ACB%7D&file=Report.docx";
            var result = await loader.GetSpoFileInfo(ctx, "user@contoso.com");

            Assert.IsNotNull(result);
            Assert.AreEqual("Report.docx", result.Filename);
            Assert.AreEqual("docx", result.Extension);
            Assert.AreEqual(1, fake.GetListItemByIdCalls);
            Assert.AreEqual(0, fake.GetDriveItemByUrlCalls, "With a driveItemId we resolve by id, not via the /shares URL path");
        }

        [TestMethod]
        public async Task GetSpoFileInfo_WithoutDriveItemId_ResolvesViaSharesApiNotEnumeration()
        {
            var ctx = "https://contoso.sharepoint.com/sites/x/Shared Documents/General/Notes.xlsx";
            var fake = new FakeSpoGraphClient
            {
                OnGetDriveItemByUrl = url => new DriveItem { WebUrl = url }
            };
            var loader = NewLoader(fake);

            var result = await loader.GetSpoFileInfo(ctx, "user@contoso.com");

            Assert.IsNotNull(result);
            Assert.AreEqual("xlsx", result.Extension);
            Assert.AreEqual(1, fake.GetDriveItemByUrlCalls, "A direct-URL context resolves in one /shares call");
            Assert.AreEqual(0, fake.GetListItemByIdCalls);
        }

        [TestMethod]
        public async Task GetSpoFileInfo_MySiteUrl_ResolvesThroughOwnerSiteNotCopilotUserDrive()
        {
            var ctx = "https://contoso-my.sharepoint.com/personal/alex_contoso_com/Documents/Καλημέρα κόσμε.docx";
            var fake = new FakeSpoGraphClient
            {
                OnGetDriveItemByUrl = url => new DriveItem { WebUrl = url }
            };
            var loader = NewLoader(fake);

            var result = await loader.GetSpoFileInfo(ctx, "viewer@contoso.com");

            Assert.IsNotNull(result);
            Assert.AreEqual("Καλημέρα κόσμε.docx", result.Filename);
            Assert.AreEqual(0, fake.GetUserDriveCalls, "OneDrive (-my) contexts must not resolve through the Copilot user's own drive");
            Assert.AreEqual(0, fake.GetSiteDriveCalls, "Plain OneDrive URLs resolve through /shares without first loading a drive");
        }

        [TestMethod]
        public async Task GetSpoFileInfo_MySiteUrl_DoesNotRequireCopilotUserOneDrive()
        {
            var ctx = "https://contoso-my.sharepoint.com/personal/alex_contoso_com/Documents/Καλημέρα κόσμε.docx";
            var fake = new FakeSpoGraphClient
            {
                OnGetUserDrive = upn => throw NotFoundError(),
                OnGetDriveItemByUrl = url => new DriveItem { WebUrl = url }
            };
            var loader = NewLoader(fake);

            var result = await loader.GetSpoFileInfo(ctx, "viewer-without-drive@contoso.com");

            Assert.IsNotNull(result);
            Assert.AreEqual(0, fake.GetUserDriveCalls, "The Copilot user's missing OneDrive must not affect another owner's file lookup");
            Assert.AreEqual(1, fake.GetDriveItemByUrlCalls);
        }

        [TestMethod]
        public async Task GetSpoFileInfo_UnresolvableContext_IsCachedAndNotRetried()
        {
            var ctx = "https://contoso.sharepoint.com/sites/x/Shared Documents/Missing.docx";
            var fake = new FakeSpoGraphClient
            {
                OnGetDriveItemByUrl = url => null   // never resolves
            };
            var loader = NewLoader(fake);

            var first = await loader.GetSpoFileInfo(ctx, "user@contoso.com");
            var second = await loader.GetSpoFileInfo(ctx, "user@contoso.com");

            Assert.IsNull(first);
            Assert.IsNull(second);
            Assert.AreEqual(1, fake.GetDriveItemByUrlCalls, "A context that failed to resolve must not be re-resolved within the run");
        }

        [TestMethod]
        public async Task GetSpoFileInfo_ResolvedContext_IsPositivelyCachedAcrossCalls()
        {
            var ctx = "https://contoso.sharepoint.com/sites/x/Shared Documents/General/Report.docx";
            var fake = new FakeSpoGraphClient
            {
                OnGetDriveItemByUrl = url => new DriveItem { WebUrl = url }
            };
            var loader = NewLoader(fake);

            var first = await loader.GetSpoFileInfo(ctx, "user@contoso.com");
            var second = await loader.GetSpoFileInfo(ctx, "user@contoso.com");

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreEqual("Report.docx", second.Filename);
            // Same run-scoped loader: the second call is a pure cache hit (no repeat Graph resolution).
            Assert.AreEqual(1, fake.GetDriveItemByUrlCalls, "A resolved context must be cached and not re-resolved");
            Assert.AreEqual(1, fake.GetSiteCalls, "Site resolution should also happen only once");
        }

        [TestMethod]
        public async Task GetSpoFileInfo_MySiteContext_CachedByContextAcrossUsers()
        {
            var ctx = "https://contoso-my.sharepoint.com/personal/alex_contoso_com/Documents/Καλημέρα κόσμε.docx";
            var fake = new FakeSpoGraphClient
            {
                OnGetDriveItemByUrl = url => new DriveItem { WebUrl = url }
            };
            var loader = NewLoader(fake);

            await loader.GetSpoFileInfo(ctx, "alice@contoso.com");
            await loader.GetSpoFileInfo(ctx, "alice@contoso.com");   // same user -> cache hit
            await loader.GetSpoFileInfo(ctx, "bob@contoso.com");     // different user -> same context cache hit

            Assert.AreEqual(0, fake.GetUserDriveCalls);
            Assert.AreEqual(1, fake.GetDriveItemByUrlCalls, "My-site contexts are owner-site based and must cache by context across Copilot users");
        }

        [TestMethod]
        public async Task GetSpoFileInfo_TeamSiteSourcedocInNonDefaultLibrary_ResolvesFromBoundedLibraryFallback()
        {
            var ctx = "https://contoso.sharepoint.com/sites/project/_layouts/15/Doc.aspx?sourcedoc=%7B00000000-0000-0000-0000-000000000000%7D&file=%CE%9A%CE%B1%CE%BB%CE%B7%CE%BC%CE%AD%CF%81%CE%B1%20%CE%BA%CF%8C%CF%83%CE%BC%CE%B5.docx";
            var fake = new FakeSpoGraphClient
            {
                OnGetSite = siteIdentifier => new Site { Id = "site-guid", WebUrl = "https://contoso.sharepoint.com/sites/project" },
                OnGetSiteDrive = siteIdentifier => DriveWithIds("site-guid", "default-list"),
                OnGetDriveItemByUrl = url => throw NotFoundError(),
                OnGetSiteDocumentLibraryDrives = (siteIdentifier, maxDrives) => new[]
                {
                    DriveWithIds("site-guid", "default-list"),
                    DriveWithIds("site-guid", "archive-list")
                },
                OnGetListItemById = (siteId, listId, itemId) =>
                {
                    if (listId == "archive-list")
                    {
                        return new ListItem { WebUrl = "https://contoso.sharepoint.com/sites/project/Archive/Καλημέρα κόσμε.docx" };
                    }
                    throw NotFoundError();
                }
            };
            var loader = NewLoader(fake);

            var result = await loader.GetSpoFileInfo(ctx, "viewer@contoso.com");

            Assert.IsNotNull(result);
            Assert.AreEqual("Καλημέρα κόσμε.docx", result.Filename);
            Assert.AreEqual(2, fake.GetListItemByIdCalls, "Default library is tried first, then the bounded document-library fallback");
            Assert.AreEqual(1, fake.GetSiteDocumentLibraryDrivesCalls);
        }

        [TestMethod]
        public async Task GetSpoFileInfo_DeletedFile_IsCachedAndLoggedWithoutException()
        {
            var ctx = "https://contoso.sharepoint.com/sites/project/Shared Documents/Καλημέρα κόσμε.docx";
            var fake = new FakeSpoGraphClient
            {
                OnGetDriveItemByUrl = url => throw NotFoundError()
            };
            var log = new CapturingLogger();
            var loader = new GraphFileMetadataLoader(fake, log);

            Assert.IsNull(await loader.GetSpoFileInfo(ctx, "alice@contoso.com"));
            Assert.IsNull(await loader.GetSpoFileInfo(ctx, "bob@contoso.com"));

            Assert.AreEqual(1, fake.GetDriveItemByUrlCalls, "Deleted files are negatively cached by context for this run");
            Assert.IsTrue(log.Entries.Exists(e => e.Level == LogLevel.Debug));
            Assert.IsFalse(log.Entries.Exists(e => e.Exception != null), "Expected not-found outcomes must be logged without exception telemetry");
        }

        [TestMethod]
        public async Task GetSpoFileInfo_SharedFileUsedByManyUsers_ResolvesOncePerRun()
        {
            var ctx = "https://contoso.sharepoint.com/sites/project/Shared Documents/Καλημέρα κόσμε.docx";
            var fake = new FakeSpoGraphClient
            {
                OnGetDriveItemByUrl = url => new DriveItem { WebUrl = url }
            };
            var loader = NewLoader(fake);

            await loader.GetSpoFileInfo(ctx, "alice@contoso.com");
            await loader.GetSpoFileInfo(ctx, "bob@contoso.com");
            await loader.GetSpoFileInfo(ctx, "charlie@contoso.com");

            Assert.AreEqual(1, fake.GetDriveItemByUrlCalls, "The same shared file context must resolve at most once, regardless of Copilot user count");
        }

        [TestMethod]
        public async Task GetMeetingInfo_ResolvesViaGraph()
        {
            var fake = new FakeSpoGraphClient();
            var loader = NewLoader(fake);

            var result = await loader.GetMeetingInfo("19:meeting_abc@thread.v2", "user-guid");

            Assert.IsNotNull(result);
            Assert.AreEqual("unit test meeting", result.Subject);
            Assert.AreEqual(1, fake.GetOnlineMeetingCalls);
        }

        /// <summary>
        /// Issue #215: a tenant without a Teams application access policy gets a 403 on *every* meeting-context
        /// Copilot event. That's a tenant-configuration prerequisite, not a product fault, so it must be logged
        /// once per run with actionable guidance (and without an exception object) instead of flooding telemetry.
        /// </summary>
        [TestMethod]
        public async Task GetMeetingInfo_NoApplicationAccessPolicy_LogsOnceWithoutExceptionAndSkipsRepeatGraphCalls()
        {
            var fake = new FakeSpoGraphClient
            {
                OnGetOnlineMeeting = (userId, meetingId) => throw NoApplicationAccessPolicyError()
            };
            var log = new CapturingLogger();
            var loader = new GraphFileMetadataLoader(fake, log);

            Assert.IsNull(await loader.GetMeetingInfo("19:meeting_a@thread.v2", "user-guid"));
            Assert.IsNull(await loader.GetMeetingInfo("19:meeting_b@thread.v2", "user-guid"));
            Assert.IsNull(await loader.GetMeetingInfo("19:meeting_c@thread.v2", "USER-GUID"));

            Assert.AreEqual(1, fake.GetOnlineMeetingCalls, "Graph must not be re-called for a user we know has no access policy");

            var warnings = log.Entries.FindAll(e => e.Level == LogLevel.Warning);
            Assert.AreEqual(1, warnings.Count, "The missing-policy condition must be logged exactly once per run");
            StringAssert.Contains(warnings[0].Message, "New-CsApplicationAccessPolicy");
            Assert.IsNull(warnings[0].Exception, "The warning must not carry the exception, so it isn't counted as exception telemetry");
        }

        [TestMethod]
        public async Task GetMeetingInfo_OtherGraphError_StillLoggedWithException()
        {
            var otherError = new ODataError
            {
                ResponseStatusCode = (int)HttpStatusCode.InternalServerError,
                Error = new MainError { Code = "ServiceError", Message = "Graph service unavailable" }
            };
            var fake = new FakeSpoGraphClient { OnGetOnlineMeeting = (userId, meetingId) => throw otherError };
            var log = new CapturingLogger();
            var loader = new GraphFileMetadataLoader(fake, log);

            Assert.IsNull(await loader.GetMeetingInfo("19:meeting_abc@thread.v2", "user-guid"));
            Assert.IsNull(await loader.GetMeetingInfo("19:meeting_abc@thread.v2", "user-guid"));

            Assert.AreEqual(2, fake.GetOnlineMeetingCalls, "Unrelated errors must not disable meeting lookups for the user");
            var warnings = log.Entries.FindAll(e => e.Level == LogLevel.Warning);
            Assert.AreEqual(2, warnings.Count);
            Assert.IsNotNull(warnings[0].Exception);
        }

        [TestMethod]
        public async Task GetMeetingInfo_ForbiddenMeetingApiError_LogsOnceWithoutExceptionAndSkipsSameMeeting()
        {
            var insufficientPermissions = new ODataError
            {
                ResponseStatusCode = (int)HttpStatusCode.Forbidden,
                Error = new MainError { Code = "Forbidden", Message = "Insufficient permissions to complete the operation." }
            };
            var fake = new FakeSpoGraphClient { OnGetOnlineMeeting = (userId, meetingId) => throw insufficientPermissions };
            var log = new CapturingLogger();
            var loader = new GraphFileMetadataLoader(fake, log);
            const string sameMeeting = "00000000-0000-0000-0000-000000000000_19:meeting_same@thread.v2";
            const string otherMeeting = "00000000-0000-0000-0000-000000000000_19:meeting_other@thread.v2";

            Assert.IsNull(await loader.GetMeetingInfo(sameMeeting, "00000000-0000-0000-0000-000000000000"));
            Assert.IsNull(await loader.GetMeetingInfo(sameMeeting, "00000000-0000-0000-0000-000000000000"));
            Assert.IsNull(await loader.GetMeetingInfo(otherMeeting, "00000000-0000-0000-0000-000000000000"));

            Assert.AreEqual(2, fake.GetOnlineMeetingCalls, "The same failed (user, meeting) id must not be requested again");

            var warnings = log.Entries.FindAll(e => e.Level == LogLevel.Warning);
            Assert.AreEqual(1, warnings.Count, "Generic meeting API 403s should produce one warning per run");
            StringAssert.Contains(warnings[0].Message, "OnlineMeetings.Read.All");
            StringAssert.Contains(warnings[0].Message, "application access policy");
            Assert.IsNull(warnings[0].Exception, "The warning must not carry the exception, so it isn't counted as exception telemetry");

            var debug = log.Entries.FindAll(e => e.Level == LogLevel.Debug);
            Assert.IsTrue(debug.Count >= 2, "Repeat failures and cached skips should be logged at debug level");
        }

        [TestMethod]
        public async Task GetMeetingInfo_NotFoundMeetingApiError_LogsOnceWithoutExceptionAndSkipsSameMeeting()
        {
            var notFound = new ODataError
            {
                ResponseStatusCode = (int)HttpStatusCode.NotFound,
                Error = new MainError { Code = "NotFound", Message = "Meeting not found" }
            };
            var fake = new FakeSpoGraphClient { OnGetOnlineMeeting = (userId, meetingId) => throw notFound };
            var log = new CapturingLogger();
            var loader = new GraphFileMetadataLoader(fake, log);
            const string sameMeeting = "00000000-0000-0000-0000-000000000000_19:meeting_missing@thread.v2";
            const string otherMeeting = "00000000-0000-0000-0000-000000000000_19:meeting_missing2@thread.v2";

            Assert.IsNull(await loader.GetMeetingInfo(sameMeeting, "00000000-0000-0000-0000-000000000000"));
            Assert.IsNull(await loader.GetMeetingInfo(sameMeeting, "00000000-0000-0000-0000-000000000000"));
            Assert.IsNull(await loader.GetMeetingInfo(otherMeeting, "00000000-0000-0000-0000-000000000000"));

            Assert.AreEqual(2, fake.GetOnlineMeetingCalls, "The same failed (user, meeting) id must not be requested again");

            var warnings = log.Entries.FindAll(e => e.Level == LogLevel.Warning);
            Assert.AreEqual(1, warnings.Count, "Meeting API 404s should produce one warning per run");
            StringAssert.Contains(warnings[0].Message, "did not organise");
            Assert.IsNull(warnings[0].Exception, "The warning must not carry the exception, so it isn't counted as exception telemetry");
        }

        [TestMethod]
        public void IsMissingApplicationAccessPolicy_OnlyMatchesTheAccessPolicyForbidden()
        {
            Assert.IsTrue(GraphFileMetadataLoader.IsMissingApplicationAccessPolicy(NoApplicationAccessPolicyError()));

            Assert.IsFalse(GraphFileMetadataLoader.IsMissingApplicationAccessPolicy(new ODataError
            {
                ResponseStatusCode = (int)HttpStatusCode.Forbidden,
                Error = new MainError { Code = "Forbidden", Message = "Insufficient privileges to complete the operation." }
            }), "A generic 403 is handled separately from the application-access-policy warning");

            Assert.IsFalse(GraphFileMetadataLoader.IsMissingApplicationAccessPolicy(new ODataError
            {
                ResponseStatusCode = (int)HttpStatusCode.InternalServerError,
                Error = new MainError { Code = "ServiceError", Message = "No application access policy found for this app" }
            }), "Only forbidden (or status-less) errors count");

            Assert.IsFalse(GraphFileMetadataLoader.IsMissingApplicationAccessPolicy(null));
        }

        private static ODataError NoApplicationAccessPolicyError() => new ODataError
        {
            ResponseStatusCode = (int)HttpStatusCode.Forbidden,
            Error = new MainError
            {
                Code = "Forbidden",
                Message = "No application access policy found for this app 00000000-0000-0000-0000-000000000000 on the user."
            }
        };

        private static ODataError NotFoundError() => new ODataError
        {
            ResponseStatusCode = (int)HttpStatusCode.NotFound,
            Error = new MainError { Code = "itemNotFound", Message = "Item not found" }
        };

        private static Drive DriveWithIds(string siteId, string listId) => new Drive
        {
            SharePointIds = new SharepointIds { SiteId = siteId, ListId = listId }
        };

        /// <summary>Minimal <see cref="ILogger"/> that records level, message and exception for assertions.</summary>
        private class CapturingLogger : ILogger
        {
            public class Entry
            {
                public LogLevel Level;
                public string Message;
                public Exception Exception;
            }

            public readonly List<Entry> Entries = new List<Entry>();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                Entries.Add(new Entry { Level = logLevel, Message = formatter(state, exception), Exception = exception });
            }

            private class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();
                public void Dispose() { }
            }
        }
    }
}
