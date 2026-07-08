using ActivityImporter.Engine.ActivityAPI.Copilot;
using DataUtils;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
            Assert.AreEqual(0, fake.FindListItemByWebUrlCalls, "With a driveItemId we must NOT enumerate the whole list");
        }

        [TestMethod]
        public async Task GetSpoFileInfo_WithoutDriveItemId_ResolvesViaWebUrlSearch()
        {
            var ctx = "https://contoso.sharepoint.com/sites/x/Shared Documents/General/Notes.xlsx";
            var fake = new FakeSpoGraphClient
            {
                OnFindListItemByWebUrl = (s, l, url) => new ListItem { WebUrl = url }
            };
            var loader = NewLoader(fake);

            var result = await loader.GetSpoFileInfo(ctx, "user@contoso.com");

            Assert.IsNotNull(result);
            Assert.AreEqual("xlsx", result.Extension);
            Assert.AreEqual(1, fake.FindListItemByWebUrlCalls);
            Assert.AreEqual(0, fake.GetListItemByIdCalls);
        }

        [TestMethod]
        public async Task GetSpoFileInfo_MySiteUrl_ResolvesViaUserDrive()
        {
            var ctx = "https://contoso-my.sharepoint.com/personal/user_contoso_com/Documents/Plan.docx";
            var fake = new FakeSpoGraphClient
            {
                OnFindListItemByWebUrl = (s, l, url) => new ListItem { WebUrl = url }
            };
            var loader = NewLoader(fake);

            var result = await loader.GetSpoFileInfo(ctx, "user@contoso.com");

            Assert.IsNotNull(result);
            Assert.AreEqual(1, fake.GetUserDriveCalls, "OneDrive (-my) contexts resolve via the user's drive");
            Assert.AreEqual(0, fake.GetSiteDriveCalls, "OneDrive contexts must not use the site drive path");
        }

        [TestMethod]
        public async Task GetSpoFileInfo_UnresolvableContext_IsCachedAndNotRetried()
        {
            var ctx = "https://contoso.sharepoint.com/sites/x/Shared Documents/Missing.docx";
            var fake = new FakeSpoGraphClient
            {
                OnFindListItemByWebUrl = (s, l, url) => null   // never matches
            };
            var loader = NewLoader(fake);

            var first = await loader.GetSpoFileInfo(ctx, "user@contoso.com");
            var second = await loader.GetSpoFileInfo(ctx, "user@contoso.com");

            Assert.IsNull(first);
            Assert.IsNull(second);
            Assert.AreEqual(1, fake.FindListItemByWebUrlCalls, "A context that failed to resolve must not be re-resolved within the session");
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
    }
}
