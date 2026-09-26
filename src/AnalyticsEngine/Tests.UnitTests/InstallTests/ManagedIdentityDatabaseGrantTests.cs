using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using Azure.Core;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// How the installer learns a managed identity's application (client) ID before giving it a contained
    /// database user.
    /// </summary>
    /// <remarks>
    /// The regression these guard: on a tenant where neither installer app registration held
    /// <c>Application.Read.All</c> and the SQL Server had no identity of its own, the App Service's identity
    /// got no database user at all, and every web-job start failed with
    /// <c>Login failed for user '&lt;token-identified principal&gt;'</c>. The client ID was available from
    /// Azure Resource Manager all along, with no directory permission.
    /// </remarks>
    [TestClass]
    public class ManagedIdentityDatabaseGrantTests
    {
        // Synthetic throughout - no real subscription, tenant or identity. Deliberately non-palindromic GUIDs,
        // because the SID conversion is endian-sensitive.
        const string SiteId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/contoso/providers/Microsoft.Web/sites/contosoanalytics";
        static readonly Guid ObjectId = new Guid("5a6b7c8d-9e0f-4a1b-8c2d-3e4f5a6b7c8d");
        static readonly Guid ClientId = new Guid("1f2e3d4c-5b6a-4978-8695-a4b3c2d1e0f9");
        static readonly Guid SomeOtherPrincipal = new Guid("22222222-2222-2222-2222-222222222222");

        #region Where the application ID comes from

        /// <summary>
        /// The incident's exact conditions - Graph refuses, and SQL Server could not resolve the name
        /// itself - must now produce a <c>WITH SID</c> user from the ARM answer, never the doomed
        /// <c>FROM EXTERNAL PROVIDER</c> fallback. Graph is not even asked once ARM has answered.
        /// </summary>
        [TestMethod]
        public async Task ApplicationId_ComesFromArm_WhenGraphIsDenied()
        {
            var arm = new StubIdentitySource { Answer = new SystemAssignedIdentityIds(ObjectId, ClientId) };
            var graph = new StubApplicationIdResolver { Failure = new InvalidOperationException("Insufficient privileges to complete the operation.") };
            var task = new SqlIdentityAccessTask(NullLogger.Instance, graph, arm);

            var applicationId = await task.ResolveApplicationIdAsync("contosoanalytics", ObjectId, SiteId);
            var script = SqlIdentityAccessTask.BuildGrantScript("contosoanalytics", applicationId, SqlContainedUserScript.AppServiceRoles);

            Assert.AreEqual(ClientId, applicationId);
            Assert.AreEqual(SiteId, arm.RequestedResourceId);
            Assert.AreEqual(0, graph.Calls, "Graph must not be consulted once ARM has answered.");
            StringAssert.Contains(script, $"WITH SID = {SqlContainedUserScript.ToSqlSid(ClientId)}, TYPE = E");
            Assert.IsFalse(script.Contains("FROM EXTERNAL PROVIDER"));
            Assert.IsFalse(script.Contains(SqlContainedUserScript.ToSqlSid(ObjectId)),
                "The object ID must never be written as a service principal's SID.");
        }

        /// <summary>An ARM failure is reported, and Graph - which may still be permitted - is tried next.</summary>
        [TestMethod]
        public async Task ApplicationId_ArmFailure_FallsBackToGraph()
        {
            var fromGraph = new Guid("0f1e2d3c-4b5a-4697-8877-665544332211");
            var arm = new StubIdentitySource { Failure = new InvalidOperationException("Azure Resource Manager returned 403 (Forbidden)") };
            var graph = new StubApplicationIdResolver { Answer = fromGraph };
            var logger = new RecordingLogger();

            var applicationId = await new SqlIdentityAccessTask(logger, graph, arm)
                .ResolveApplicationIdAsync("contosoanalytics", ObjectId, SiteId);

            Assert.AreEqual(fromGraph, applicationId);
            Assert.IsTrue(logger.Entries.Any(e => e.Message.Contains("Azure Resource Manager returned 403")),
                "The ARM failure must be reported, or nobody can fix it.");
        }

        /// <summary>
        /// An ARM answer for a different principal than the one being granted is not used: another
        /// identity's client ID as the SID gives a user that can never sign in.
        /// </summary>
        [TestMethod]
        public async Task ApplicationId_ArmAnswerForAnotherPrincipal_IsIgnored()
        {
            var arm = new StubIdentitySource { Answer = new SystemAssignedIdentityIds(SomeOtherPrincipal, ClientId) };
            var graph = new StubApplicationIdResolver { Answer = null };
            var logger = new RecordingLogger();

            var applicationId = await new SqlIdentityAccessTask(logger, graph, arm)
                .ResolveApplicationIdAsync("contosoanalytics", ObjectId, SiteId);

            Assert.IsNull(applicationId);
            Assert.AreEqual(1, graph.Calls, "Graph is still worth asking.");
            Assert.IsTrue(logger.Entries.Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                && e.Message.Contains(SomeOtherPrincipal.ToString())));
        }

        /// <summary>
        /// When nothing can say, the answer is "unknown" - which becomes <c>FROM EXTERNAL PROVIDER</c> -
        /// never a guess.
        /// </summary>
        [TestMethod]
        public async Task ApplicationId_NoSourceCanSay_IsNull()
        {
            var noIdentity = new StubIdentitySource { Answer = null };
            var denied = new StubApplicationIdResolver { Failure = new InvalidOperationException("Insufficient privileges to complete the operation.") };

            Assert.IsNull(await new SqlIdentityAccessTask(NullLogger.Instance, denied, noIdentity)
                .ResolveApplicationIdAsync("contosoanalytics", ObjectId, SiteId));
            Assert.IsNull(await new SqlIdentityAccessTask(NullLogger.Instance)
                .ResolveApplicationIdAsync("contosoanalytics", ObjectId, SiteId));
        }

        /// <summary>Without a resource ID there is nothing to ask ARM about, so it is skipped rather than called.</summary>
        [TestMethod]
        public async Task ApplicationId_WithoutAResourceId_SkipsArm()
        {
            var arm = new StubIdentitySource { Answer = new SystemAssignedIdentityIds(ObjectId, ClientId) };
            var graph = new StubApplicationIdResolver { Answer = ClientId };

            var applicationId = await new SqlIdentityAccessTask(NullLogger.Instance, graph, arm)
                .ResolveApplicationIdAsync("contosoanalytics", ObjectId, "  ");

            Assert.AreEqual(ClientId, applicationId);
            Assert.AreEqual(0, arm.Calls);
            Assert.AreEqual(1, graph.Calls);
        }

        #endregion

        #region Messages

        /// <summary>
        /// The Automation account's grant used to be reported as "the App Service managed identity", with the
        /// web-jobs as the consequence, which sends an operator to the wrong resource.
        /// </summary>
        [TestMethod]
        public async Task Grant_NamesTheResourceTheIdentityBelongsTo()
        {
            Assert.AreEqual("App Service", SqlIdentityAccessTask.DescribeOwner(ManagedIdentityOwner.AppService));
            Assert.AreEqual("Automation account", SqlIdentityAccessTask.DescribeOwner(ManagedIdentityOwner.AutomationAccount));
            StringAssert.Contains(SqlIdentityAccessTask.DescribeImpact(ManagedIdentityOwner.AppService), "web-jobs");
            StringAssert.Contains(SqlIdentityAccessTask.DescribeImpact(ManagedIdentityOwner.AutomationAccount), "runbooks");
            Assert.IsFalse(SqlIdentityAccessTask.DescribeImpact(ManagedIdentityOwner.AutomationAccount).Contains("web-jobs"));

            // The no-identity path returns before any connection is opened, so it shows the label end to end.
            var logger = new RecordingLogger();
            var granted = await new SqlIdentityAccessTask(logger).GrantDatabaseAccessAsync(
                "Data Source=contoso-sql.database.windows.net;Initial Catalog=analytics;Encrypt=True",
                ManagedIdentityOwner.AutomationAccount, "contosoautomation", Guid.Empty, null, new[] { "db_owner" });

            Assert.IsFalse(granted);
            var skipped = logger.Entries.Single();
            StringAssert.Contains(skipped.Message, "Automation account");
            Assert.IsFalse(skipped.Message.Contains("App Service"));
        }

        #endregion

        #region Azure Resource Manager reader

        /// <summary>
        /// The client ID is read from the identity's extension resource with a bearer token for ARM - the
        /// real request shape, answered with a synthetic body.
        /// </summary>
        [TestMethod]
        public async Task ArmReader_ReadsTheClientIdFromTheIdentityExtensionResource()
        {
            var handler = new ArmHandler(HttpStatusCode.OK,
                $"{{\"name\":\"contosoanalytics\",\"type\":\"Microsoft.Web/sites\",\"properties\":{{\"tenantId\":\"{Guid.Empty}\"," +
                $"\"principalId\":\"{ObjectId}\",\"clientId\":\"{ClientId}\"}}}}");
            var credential = new ArmTokenCredential();

            var identity = await new ArmManagedIdentityApplicationIdSource(credential, handler)
                .GetSystemAssignedIdentityAsync(SiteId, CancellationToken.None);

            Assert.AreEqual(ObjectId, identity.PrincipalId);
            Assert.AreEqual(ClientId, identity.ClientId);
            Assert.AreEqual(1, handler.Requests);
            Assert.AreEqual(HttpMethod.Get, handler.Method, "Reading an identity must never change it.");
            Assert.AreEqual(
                $"https://management.azure.com{SiteId}/providers/Microsoft.ManagedIdentity/identities/default?api-version={ArmManagedIdentityApplicationIdSource.ApiVersion}",
                handler.Url);
            Assert.AreEqual($"Bearer {ArmTokenCredential.Token}", handler.Authorization);
            CollectionAssert.AreEqual(new[] { "https://management.azure.com/.default" }, credential.RequestedScopes);
        }

        /// <summary>ARM answers 404 for a resource with no system-assigned identity: that means none, not a failure.</summary>
        [TestMethod]
        public async Task ArmReader_NotFound_MeansNoIdentity()
        {
            var handler = new ArmHandler(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"NotFound\",\"message\":\"Synthetic ARM error\"}}");

            Assert.IsNull(await new ArmManagedIdentityApplicationIdSource(new ArmTokenCredential(), handler)
                .GetSystemAssignedIdentityAsync(SiteId, CancellationToken.None));
        }

        /// <summary>Any other refusal is reported with its status and code, so a missing role is not mistaken for "no identity".</summary>
        [TestMethod]
        public async Task ArmReader_Refusal_ThrowsWithTheStatus()
        {
            var handler = new ArmHandler(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"Synthetic ARM error\"}}");
            var reader = new ArmManagedIdentityApplicationIdSource(new ArmTokenCredential(), handler);

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => reader.GetSystemAssignedIdentityAsync(SiteId, CancellationToken.None));

            StringAssert.Contains(ex.Message, "403");
            StringAssert.Contains(ex.Message, "AuthorizationFailed");
        }

        /// <summary>
        /// A body without both IDs must not produce a usable-looking answer, and something that is not a
        /// resource ID must never be sent to ARM.
        /// </summary>
        [TestMethod]
        public void ArmReader_RejectsIncompleteAnswersAndNonResourceIds()
        {
            Assert.ThrowsException<InvalidOperationException>(() => ArmManagedIdentityApplicationIdSource.ParseResponse(
                $"{{\"properties\":{{\"principalId\":\"{ObjectId}\"}}}}"));
            Assert.ThrowsException<InvalidOperationException>(() => ArmManagedIdentityApplicationIdSource.ParseResponse(
                $"{{\"properties\":{{\"principalId\":\"{ObjectId}\",\"clientId\":\"{Guid.Empty}\"}}}}"));
            Assert.ThrowsException<InvalidOperationException>(() => ArmManagedIdentityApplicationIdSource.ParseResponse(string.Empty));

            Assert.ThrowsException<ArgumentException>(() => ArmManagedIdentityApplicationIdSource.BuildRequestUrl("contosoanalytics"));
            Assert.ThrowsException<ArgumentException>(() => ArmManagedIdentityApplicationIdSource.BuildRequestUrl(null));
            StringAssert.StartsWith(ArmManagedIdentityApplicationIdSource.BuildRequestUrl(SiteId + "/"),
                $"https://management.azure.com{SiteId}/providers/Microsoft.ManagedIdentity/");
        }

        #endregion

        class StubIdentitySource : IManagedIdentityApplicationIdSource
        {
            public SystemAssignedIdentityIds Answer { get; set; }
            public Exception Failure { get; set; }
            public int Calls { get; private set; }
            public string RequestedResourceId { get; private set; }

            public Task<SystemAssignedIdentityIds> GetSystemAssignedIdentityAsync(string resourceId, CancellationToken cancellationToken)
            {
                Calls++;
                RequestedResourceId = resourceId;
                if (Failure != null) throw Failure;
                return Task.FromResult(Answer);
            }
        }

        class StubApplicationIdResolver : IEntraPrincipalResolver
        {
            public Guid? Answer { get; set; }
            public Exception Failure { get; set; }
            public int Calls { get; private set; }

            public Task<Guid?> ResolveObjectIdAsync(SqlDatabaseUser user, CancellationToken cancellationToken)
            {
                throw new NotSupportedException("Only application ID lookups are exercised here.");
            }

            public Task<Guid?> ResolveApplicationIdAsync(Guid servicePrincipalObjectId, CancellationToken cancellationToken)
            {
                Calls++;
                if (Failure != null) throw Failure;
                return Task.FromResult(Answer);
            }
        }

        /// <summary>Records the request and returns a canned ARM response, so nothing reaches Azure.</summary>
        sealed class ArmHandler : HttpMessageHandler
        {
            readonly HttpStatusCode _status;
            readonly string _body;

            public ArmHandler(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            public int Requests { get; private set; }
            public HttpMethod Method { get; private set; }
            public string Url { get; private set; }
            public string Authorization { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                Method = request.Method;
                Url = request.RequestUri.ToString();
                Authorization = request.Headers.Authorization?.ToString();

                return Task.FromResult(new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json")
                });
            }
        }

        sealed class ArmTokenCredential : TokenCredential
        {
            public const string Token = "synthetic-arm-token";

            public List<string> RequestedScopes { get; } = new List<string>();

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                RequestedScopes.AddRange(requestContext.Scopes);
                return new AccessToken(Token, DateTimeOffset.UtcNow.AddHours(1));
            }

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                return new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
            }
        }
    }
}
