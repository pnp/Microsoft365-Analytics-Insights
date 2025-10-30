using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphStringUtilsTests
    {
        protected ILogger _logger;
        protected TestsAppConfig _config;

        public GraphStringUtilsTests()
        {
            _logger = new LoggerFactory().CreateLogger("CopilotTests");
            _config = new TestsAppConfig();
        }


        [TestMethod]
        public void GetMeetingIdFragmentFromMeetingThreadUrl()
        {
            Assert.AreEqual("19:meeting_NDQ4MGRhYjgtMzc5MS00ZWMxLWJiZjEtOTIxZmM5Mzg3ZGFi@thread.v2",
                StringUtils.GetMeetingIdFragmentFromMeetingThreadUrl("https://microsoft.teams.com/threads/19:meeting_NDQ4MGRhYjgtMzc5MS00ZWMxLWJiZjEtOTIxZmM5Mzg3ZGFi@thread.v2"));
            Assert.IsNull(StringUtils.GetMeetingIdFragmentFromMeetingThreadUrl("https://microsoft.teams.com/"));
        }

        [TestMethod]
        public void GetSiteUrl()
        {
            // My Site
            Assert.AreEqual("https://test.sharepoint.com/sites/test",
                StringUtils.GetSiteUrl("https://test.sharepoint.com/sites/test/Shared%20Documents/General/test.docx"));

            Assert.AreEqual("https://test.sharepoint.com/sites/test",
                StringUtils.GetSiteUrl("https://test.sharepoint.com/sites/test"));

            // If we're not passing a doc in the root-site, we should get the root site back
            Assert.IsNull(StringUtils.GetSiteUrl("https://test.sharepoint.com"));
            Assert.IsNull(StringUtils.GetSiteUrl("https://test.sharepoint.com/"));

            Assert.AreEqual("https://m365cp123890-my.sharepoint.com/personal/sambetts_m365cp123890_onmicrosoft_com",
                StringUtils.GetSiteUrl(
                "https://m365cp123890-my.sharepoint.com/personal/sambetts_m365cp123890_onmicrosoft_com/_layouts/15/Doc.aspx?sourcedoc=%7B0D86F64F-8435-430C-8979-FF46C00F7ACB%7D&file=Presentation.pptx&action=edit&mobileredirect=true")
                );

            // Root site doc
            Assert.AreEqual("https://m365cp123890.sharepoint.com",
                StringUtils.GetSiteUrl(
                "https://m365cp123890.sharepoint.com/_layouts/15/Doc.aspx?sourcedoc=%7B0D86F64F-8435-430C-8979-FF46C00F7ACB%7D&file=Presentation.pptx&action=edit&mobileredirect=true")
                );
            Assert.AreEqual("https://m365cp123890.sharepoint.com",
                StringUtils.GetSiteUrl("https://m365cp123890.sharepoint.com/Doc.docx"));
        }

        [TestMethod]
        public void GetHostAndSiteRelativeUrl()
        {
            var subSiteResult = StringUtils.GetHostAndSiteRelativeUrl("https://test.sharepoint.com/sites/test");
            Assert.AreEqual("test.sharepoint.com:/sites/test", subSiteResult);

            var rootSiteResult = StringUtils.GetHostAndSiteRelativeUrl("https://test.sharepoint.com/");
            Assert.AreEqual("root", rootSiteResult);

            Assert.IsNull(StringUtils.GetHostAndSiteRelativeUrl("https://test.com/"));
        }

        [TestMethod]
        public void GetDriveItemId()
        {
            Assert.IsNull(StringUtils.GetDriveItemId("https://test.sharepoint.com/sites/test"));
            Assert.AreEqual(StringUtils.GetDriveItemId(
                "https://m365cp123890-my.sharepoint.com/personal/sambetts_m365cp123890_onmicrosoft_com/_layouts/15/Doc.aspx?sourcedoc=%7B0D86F64F-8435-430C-8979-FF46C00F7ACB%7D&file=Presentation.pptx&action=edit&mobileredirect=true"),
                "0D86F64F-8435-430C-8979-FF46C00F7ACB");
        }

    }
}
