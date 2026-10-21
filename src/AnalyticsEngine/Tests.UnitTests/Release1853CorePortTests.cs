extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class Release1853CorePortTests
    {
        [TestMethod]
        public async Task InvalidSettingsModel_ReturnsJsonErrorWithoutOpeningStorage()
        {
            var controller = new CopilotAdoptionSettingsAPIController(
                () => throw new InvalidOperationException("Invalid models must not open storage."));
            controller.ModelState.AddModelError("values", "Synthetic invalid value.");
            var result = await controller.Get();
            Assert.IsInstanceOfType<BadRequestObjectResult>(result);
            var error = ((BadRequestObjectResult)result).Value as CopilotAdoptionSettingsError;
            Assert.IsNotNull(error);
            Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.InvalidRequest, error.Code);
        }

        [TestMethod]
        public void DefaultHealthChecks_ReadTheNet10ConfigurationSource()
        {
            var previous = AnalyticsConfig.AppSettings["EnableDefaultHealthChecks"];
            try
            {
                foreach (var enabled in new[] { false, true })
                {
                    AnalyticsConfig.AppSettings["EnableDefaultHealthChecks"] = enabled.ToString();
                    Assert.AreEqual(enabled, new AppConfig().EnableDefaultHealthChecks);
                }
            }
            finally { AnalyticsConfig.AppSettings["EnableDefaultHealthChecks"] = previous; }
        }
    }
}
