extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using System.Reflection;

namespace Tests.UnitTests
{
    [TestClass]
    public class DelegatedGraphConsentPipelineTests
    {
        [TestMethod]
        public void ConnectTeams_IssuesOnlyTheMarkedOidcChallenge()
        {
            var controller = new AccountController();
            var result = controller.ConnectTeams() as ChallengeResult;

            Assert.IsNotNull(result);
            CollectionAssert.AreEqual(
                new[] { OpenIdConnectDefaults.AuthenticationScheme },
                result.AuthenticationSchemes.ToArray());
            Assert.IsTrue(DelegatedGraphConsent.IsTeamsConnect(result.Properties));
            Assert.AreEqual(DelegatedGraphConsent.TeamsPermissionsRoute, result.Properties.RedirectUri);
        }

        [TestMethod]
        public void ConnectTeams_RequiresSignInAndAdministration()
        {
            var action = typeof(AccountController).GetMethod(nameof(AccountController.ConnectTeams));

            Assert.IsNotNull(action.GetCustomAttribute<AuthorizeAttribute>());
            var permission = action.GetCustomAttributes<RequirePortalPermissionAttribute>().Single();
            Assert.AreEqual(PortalPermission.Administration, permission.Permission);
        }

        [TestMethod]
        public void SignInChallenge_IsNotATeamsConsentRequest()
        {
            var controller = new AccountController();
            var result = controller.SignIn() as ChallengeResult;

            Assert.IsNotNull(result);
            Assert.IsFalse(DelegatedGraphConsent.IsTeamsConnect(result.Properties));
            Assert.AreEqual("/", result.Properties.RedirectUri);
        }
    }
}
