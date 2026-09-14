extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers the rule that decides whether an unauthenticated request gets a plain <c>401</c> or a
    /// sign-in redirect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is small but load-bearing, and it had no test at all. The OIDC handler's default is to
    /// turn the 401 from an <c>[Authorize]</c>'d controller into a <c>302</c> to
    /// login.microsoftonline.com. That is right for a top-level navigation and wrong for the portal's
    /// <c>fetch()</c> calls: the SPA follows the redirect cross-origin, the Entra login page carries
    /// no CORS headers, and the call rejects with an opaque <c>TypeError: Failed to fetch</c>. The
    /// user sees a broken portal rather than "your session expired".
    /// </para>
    /// <para>
    /// <c>Program.cs</c> suppresses the redirect for API requests based entirely on this predicate, so
    /// a regression here does not fail loudly - it silently restores the broken-portal behaviour. The
    /// in-memory harness in <see cref="LicenceActivityHttpHost"/> deliberately does NOT exercise it
    /// (it installs a synthetic scheme whose challenge is already a 401), which is exactly why the
    /// predicate needs covering on its own.
    /// </para>
    /// </remarks>
    [TestClass]
    public class AuthRequestRulesTests
    {
        private static HttpRequest Request(string path, params (string Name, string Value)[] headers)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            foreach (var (name, value) in headers)
            {
                context.Request.Headers[name] = value;
            }

            return context.Request;
        }

        /// <summary>
        /// The portal's own fetch wrapper always sends <c>X-Requested-With</c>; a navigation never does.
        /// </summary>
        [TestMethod]
        public void XmlHttpRequest_Header_Always_Means_Api()
        {
            Assert.IsTrue(AuthRequestRules.IsApiRequest(
                Request("/api/LicenceActivity/overview", ("X-Requested-With", "XMLHttpRequest"))));

            // Even off the /api path - the header is the unambiguous signal.
            Assert.IsTrue(AuthRequestRules.IsApiRequest(
                Request("/Home/Index", ("X-Requested-With", "XMLHttpRequest"))));

            // ...and it is matched case-insensitively, as headers are compared elsewhere.
            Assert.IsTrue(AuthRequestRules.IsApiRequest(
                Request("/api/x", ("X-Requested-With", "xmlhttprequest"))));
        }

        /// <summary>Without the header, the <c>/api</c> path is the fallback signal.</summary>
        [TestMethod]
        public void ApiPath_Without_Navigation_Headers_Is_An_Api_Request()
        {
            Assert.IsTrue(AuthRequestRules.IsApiRequest(Request("/api/LicenceActivity/overview")));
            Assert.IsTrue(AuthRequestRules.IsApiRequest(Request("/api")));
            Assert.IsFalse(AuthRequestRules.IsApiRequest(Request("/Home/Index")));

            // A path that merely starts with the letters "api" is not the /api segment.
            Assert.IsFalse(AuthRequestRules.IsApiRequest(Request("/apitest/page")));
        }

        /// <summary>
        /// A top-level navigation must still redirect, even to an <c>/api</c> URL - otherwise a user
        /// pasting one into the address bar gets a bare 401 instead of being asked to sign in.
        /// </summary>
        [TestMethod]
        public void Document_Navigation_Is_Never_An_Api_Request()
        {
            Assert.IsFalse(AuthRequestRules.IsApiRequest(
                Request("/api/LicenceActivity/overview", ("Sec-Fetch-Mode", "navigate"))));

            Assert.IsFalse(AuthRequestRules.IsApiRequest(
                Request("/api/LicenceActivity/overview", ("Sec-Fetch-Dest", "document"))));

            // But a background fetch to the same URL is an API request.
            Assert.IsTrue(AuthRequestRules.IsApiRequest(
                Request("/api/LicenceActivity/overview", ("Sec-Fetch-Mode", "cors"))));
        }

        /// <summary>
        /// The explicit header wins over the navigation hints, so the SPA can never be redirected.
        /// </summary>
        [TestMethod]
        public void XmlHttpRequest_Header_Beats_The_Navigation_Hints()
        {
            Assert.IsTrue(AuthRequestRules.IsApiRequest(Request("/api/x",
                ("X-Requested-With", "XMLHttpRequest"), ("Sec-Fetch-Mode", "navigate"))));
        }

        /// <summary>
        /// A browser too old to send Fetch Metadata falls back to the path rule rather than breaking.
        /// </summary>
        [TestMethod]
        public void Missing_Fetch_Metadata_Falls_Back_To_The_Path_Rule()
        {
            Assert.IsTrue(AuthRequestRules.IsApiRequest(Request("/api/LicenceActivity/users")));
            Assert.IsFalse(AuthRequestRules.IsApiRequest(Request("/Reports")));
        }

        [TestMethod]
        public void A_Null_Request_Is_Not_An_Api_Request()
        {
            Assert.IsFalse(AuthRequestRules.IsApiRequest(null));
        }

        /// <summary>
        /// The header name the SPA reads to tell "signed out" from "signed in but not allowed" must not
        /// drift; the portal matches it literally.
        /// </summary>
        [TestMethod]
        public void The_Session_Expired_Header_Name_Is_Stable()
        {
            Assert.AreEqual("X-Auth-Session-Expired", AuthRequestRules.SessionExpiredHeader);
        }
    }
}
