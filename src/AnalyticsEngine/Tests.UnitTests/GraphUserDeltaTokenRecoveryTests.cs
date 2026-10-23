using Common.Entities;
using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.UserOrgs;
using DataUtils;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #664: an expired <c>/users/delta</c> token was kept forever.
    ///
    /// Graph answered the stored token with <c>400 Request_UnsupportedQuery</c> ("DeltaLink older than 30 days
    /// is not supported."). Lenient paging turned that into an empty page 1, the loader could not tell "the read
    /// failed" from "nothing changed", there was no new token to commit, and the section reported success - so
    /// every later run replayed the same dead token, read 0 users, and new Entra users and changes to existing
    /// ones stopped arriving until somebody deleted the stored token by hand.
    ///
    /// These tests pin the fix: a token Graph rejects is discarded and the full user list is read again in the
    /// same run, once; every other failure keeps the token; and a read that never reached its
    /// <c>@odata.deltaLink</c> is reported as incomplete instead of as a finished import.
    /// </summary>
    [TestClass]
    public class GraphUserDeltaTokenRecoveryTests
    {
        private const string StoredToken = "stored-token-0001";
        private const string FreshToken = "fresh-token-0002";
        private const string GreekDepartment = "Πωλήσεις";

        // Graph error payloads. Identifiers are zeroed; the first is the production response, with its date,
        // request ids and token replaced.
        private const string UnsupportedQueryBody =
            "{\"error\":{\"code\":\"Request_UnsupportedQuery\",\"message\":\"DeltaLink older than 30 days is not supported.\"," +
            "\"innerError\":{\"date\":\"2026-01-01T00:00:00\",\"request-id\":\"00000000-0000-0000-0000-000000000000\",\"client-request-id\":\"00000000-0000-0000-0000-000000000000\"}}}";
        private const string SyncStateNotFoundBody =
            "{\"error\":{\"code\":\"syncStateNotFound\",\"message\":\"The sync state generation is not found.\"}}";
        private const string GoneBody =
            "{\"error\":{\"code\":\"resyncRequired\",\"message\":\"Resync is required.\"}}";
        private const string UnauthorizedBody =
            "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"Access token has expired or is not yet valid.\"}}";
        private const string ForbiddenBody =
            "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges to complete the operation.\"}}";
        private const string ServerErrorBody =
            "{\"error\":{\"code\":\"UnknownError\",\"message\":\"An internal server error occurred.\"}}";
        private const string OtherBadRequestBody =
            "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"Invalid request.\"}}";
        private const string ThrottledBody =
            "{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"Too many requests.\"}}";

        #region The rule: which failures reject a token

        [DataTestMethod]
        [DataRow(400, "Request_UnsupportedQuery", true)]
        [DataRow(410, "resyncRequired", true)]
        [DataRow(410, null, true)]
        [DataRow(400, "syncStateNotFound", true)]
        [DataRow(404, "syncStateNotFound", true)]
        [DataRow(400, "request_unsupportedquery", true)]
        [DataRow(400, "Request_BadRequest", false)]
        [DataRow(404, "Request_ResourceNotFound", false)]
        [DataRow(401, "InvalidAuthenticationToken", false)]
        [DataRow(403, "Authorization_RequestDenied", false)]
        [DataRow(403, "syncStateNotFound", false)]
        [DataRow(429, "TooManyRequests", false)]
        [DataRow(500, "UnknownError", false)]
        [DataRow(503, "syncStateNotFound", false)]
        public void Rejection_IsDecidedByTheStatusAndGraphErrorCode(int status, string errorCode, bool expected)
        {
            var body = errorCode == null ? string.Empty : "{\"error\":{\"code\":\"" + errorCode + "\",\"message\":\"synthetic\"}}";
            var failure = new GraphHttpException((HttpStatusCode)status, "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=synthetic", body, null);

            Assert.AreEqual(expected, GraphDeltaTokenRejection.IsRejectedDeltaToken(failure, requestCarriedDeltaToken: true),
                $"HTTP {status} with error code '{errorCode ?? "(none)"}'. A 401, 403, 429 or 5xx must never discard the token, whatever code it carries.");
        }

        [TestMethod]
        public void Rejection_OnlyARequestThatCarriedATokenCanRejectIt()
        {
            var failure = new GraphHttpException(HttpStatusCode.BadRequest, "https://graph.microsoft.com/v1.0/users/delta", UnsupportedQueryBody, null);

            Assert.IsTrue(GraphDeltaTokenRejection.IsRejectedDeltaToken(failure, requestCarriedDeltaToken: true));
            Assert.IsFalse(GraphDeltaTokenRejection.IsRejectedDeltaToken(failure, requestCarriedDeltaToken: false),
                "Without a token, Request_UnsupportedQuery means the query itself is wrong - discarding a token cannot fix it.");
        }

        [TestMethod]
        public void Rejection_NeverDependsOnTheMessageText()
        {
            // Graph says not to depend on 'message'. The production wording, on an error code that does not
            // qualify, must not be enough.
            var body = "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"DeltaLink older than 30 days is not supported.\"}}";
            var failure = new GraphHttpException(HttpStatusCode.BadRequest, "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=synthetic", body, null);

            Assert.IsFalse(GraphDeltaTokenRejection.IsRejectedDeltaToken(failure, requestCarriedDeltaToken: true));
        }

        [TestMethod]
        public void Rejection_FailuresWithoutAGraphErrorPayload_NeverQualify()
        {
            // What AutoThrottleHttpClient throws once its 429 / 503 retries run out, a timeout, and nothing at all.
            Assert.IsFalse(GraphDeltaTokenRejection.IsRejectedDeltaToken(
                new HttpRequestException("Response status code does not indicate success: 429 (Too Many Requests)."), requestCarriedDeltaToken: true));
            Assert.IsFalse(GraphDeltaTokenRejection.IsRejectedDeltaToken(new TaskCanceledException(), requestCarriedDeltaToken: true));
            Assert.IsFalse(GraphDeltaTokenRejection.IsRejectedDeltaToken(null, requestCarriedDeltaToken: true));
        }

        #endregion

        #region GraphUserLoader: recovering from a rejected token

        [DataTestMethod]
        [DataRow(400, UnsupportedQueryBody, "Request_UnsupportedQuery")]
        [DataRow(410, GoneBody, "resyncRequired")]
        [DataRow(400, SyncStateNotFoundBody, "syncStateNotFound")]
        [DataRow(404, SyncStateNotFoundBody, "syncStateNotFound")]
        public async Task RejectedToken_IsDiscarded_AndTheFullUserListIsReadOnceWithoutIt(int status, string rejection, string errorCode)
        {
            var store = new RecordingDeltaValueProvider(StoredToken);
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler()
                .Then((HttpStatusCode)status, rejection)
                .Then(HttpStatusCode.OK, FinalPage(FreshToken, UserJson(1), UserJson(2)));

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, store, logger, null);

                var users = await loader.LoadAllActiveUsers();

                Assert.AreEqual(2, handler.Requests.Count, "Exactly one follow-up request: no loop, and no second attempt with the dead token.");
                StringAssert.Contains(handler.Requests[0], "$deltatoken=" + StoredToken);
                Assert.IsFalse(handler.Requests[1].Contains("$deltatoken"),
                    "The follow-up must carry no token at all. $deltatoken=latest would mean 'sync from now' and skip every change made while the import was stuck.");
                StringAssert.Contains(handler.Requests[1], "/users/delta?$select=", "It must be the same query, only without the token.");

                Assert.AreEqual(1, store.ClearCalls, "The dead token must be discarded with ClearDeltaToken.");
                Assert.AreEqual(2, users.Count, "The full user list is returned in the same run.");
                Assert.IsTrue(users.Any(u => u.Department == GreekDepartment), "The recovery read goes through the same UTF-8 decoding as any other.");
                Assert.IsTrue(loader.LastLoadReachedDeltaLink);
                Assert.AreEqual(0, store.SetCalls, "The new checkpoint is only buffered: it is saved when the import commits, after it has succeeded.");

                var warning = logger.Messages(LogLevel.Warning).Single(m => m.Contains("rejected the stored /users/delta token"));
                StringAssert.Contains(warning, "HTTP " + status);
                StringAssert.Contains(warning, errorCode);
                StringAssert.Contains(warning, "Discarding the token");
                Assert.IsFalse(warning.Contains(StoredToken), "The token itself does not belong in the warning.");

                await loader.CommitDeltaTokenAsync();
                Assert.AreEqual(FreshToken, store.Token);
            }
        }

        [DataTestMethod]
        [DataRow(401, UnauthorizedBody, "InvalidAuthenticationToken")]
        [DataRow(403, ForbiddenBody, "Authorization_RequestDenied")]
        [DataRow(500, ServerErrorBody, "UnknownError")]
        [DataRow(400, OtherBadRequestBody, "Request_BadRequest")]
        public async Task AnyOtherFailure_KeepsTheToken_AndTheReadIsReportedIncomplete(int status, string body, string errorCode)
        {
            var store = new RecordingDeltaValueProvider(StoredToken);
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler().Then((HttpStatusCode)status, body);

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, store, logger, null);

                var users = await loader.LoadAllActiveUsers();

                Assert.AreEqual(1, handler.Requests.Count, "Nothing may be retried without the token.");
                StringAssert.Contains(handler.Requests[0], "$deltatoken=" + StoredToken);
                Assert.AreEqual(0, store.ClearCalls, $"HTTP {status} says nothing about the token; discarding it would force a full re-read of the tenant.");
                Assert.AreEqual(0, users.Count);
                Assert.IsFalse(loader.LastLoadReachedDeltaLink, "An empty result from a failed read must not look like 'nothing changed'.");

                var warning = logger.Messages(LogLevel.Warning).Single(m => m.Contains("/users/delta read stopped"));
                StringAssert.Contains(warning, "HTTP " + status);
                StringAssert.Contains(warning, errorCode);
                StringAssert.Contains(warning, "token was kept");

                await loader.CommitDeltaTokenAsync();
                Assert.AreEqual(0, store.SetCalls, "There is no new checkpoint to save.");
                Assert.AreEqual(StoredToken, store.Token);
            }
        }

        [DataTestMethod]
        [DataRow(429)]
        [DataRow(503)]
        public async Task ThrottlingOrAnOutageThatOutlastsItsRetries_KeepsTheToken(int status)
        {
            var store = new RecordingDeltaValueProvider(StoredToken);
            var logger = new CapturingLogger();
            var body = status == 429 ? ThrottledBody : ServerErrorBody;
            var handler = new ScriptedGraphHandler()
                .Then((HttpStatusCode)status, body, retryAfterSeconds: "0")
                .Then((HttpStatusCode)status, body, retryAfterSeconds: "0");

            using (var client = new ManualGraphCallClient(handler, logger) { MaxRetries = 2 })
            {
                var loader = new GraphUserLoader(client, store, logger, null);

                var users = await loader.LoadAllActiveUsers();

                Assert.AreEqual(2, handler.Requests.Count, "The throttling client retries once and then gives up.");
                Assert.IsTrue(handler.Requests.All(r => r.Contains("$deltatoken=" + StoredToken)), "Every attempt carried the stored token; none was made without it.");
                Assert.AreEqual(0, store.ClearCalls);
                Assert.AreEqual(StoredToken, store.Token);
                Assert.AreEqual(0, users.Count);
                Assert.IsFalse(loader.LastLoadReachedDeltaLink);

                var warning = logger.Messages(LogLevel.Warning).Single(m => m.Contains("/users/delta read stopped"));
                StringAssert.Contains(warning, status.ToString());
                StringAssert.Contains(warning, "token was kept");
            }
        }

        [TestMethod]
        public async Task AFailureAfterThePageThatCarriedTheToken_IsNeverTakenForARejection()
        {
            var store = new RecordingDeltaValueProvider(StoredToken);
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, PageWithNextLink(UserJson(1)))
                .Then(HttpStatusCode.BadRequest, UnsupportedQueryBody);

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, store, logger, null);

                var users = await loader.LoadAllActiveUsers();

                Assert.AreEqual(2, handler.Requests.Count);
                StringAssert.Contains(handler.Requests[1], "$skiptoken=", "Page 2 follows the paging link, not the stored token.");
                Assert.AreEqual(0, store.ClearCalls, "Only the request that carried the token can reject it.");
                Assert.AreEqual(StoredToken, store.Token);
                Assert.IsFalse(loader.LastLoadReachedDeltaLink);
                Assert.AreEqual(1, users.Count, "The users read before the failure are still returned, exactly as before.");
            }
        }

        [TestMethod]
        public async Task WithNoStoredToken_UnsupportedQuery_IsNotAnExpiredToken_AndIsNotRetried()
        {
            var store = new RecordingDeltaValueProvider();
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler().Then(HttpStatusCode.BadRequest, UnsupportedQueryBody);

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, store, logger, null);

                var users = await loader.LoadAllActiveUsers();

                Assert.AreEqual(1, handler.Requests.Count, "No token was sent, so there is nothing to discard and nothing to retry.");
                Assert.IsFalse(handler.Requests[0].Contains("$deltatoken"));
                Assert.AreEqual(0, store.ClearCalls);
                Assert.AreEqual(0, users.Count);
                Assert.IsFalse(loader.LastLoadReachedDeltaLink, "The run must be reported incomplete.");
                StringAssert.Contains(logger.Messages(LogLevel.Warning).Single(m => m.Contains("/users/delta read stopped")), "No delta token was in use");
            }
        }

        [DataTestMethod]
        [DataRow(500, ServerErrorBody)]
        [DataRow(400, UnsupportedQueryBody)]
        [DataRow(410, GoneBody)]
        public async Task RejectedToken_ThenTheFullReadAlsoFails_MakesExactlyTwoRequests_AndSavesNothing(int status, string body)
        {
            var store = new RecordingDeltaValueProvider(StoredToken);
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.BadRequest, UnsupportedQueryBody)
                .Then((HttpStatusCode)status, body);

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, store, logger, null);

                var users = await loader.LoadAllActiveUsers();

                Assert.AreEqual(2, handler.Requests.Count, "One recovery attempt only - even a second rejection-shaped error must not start a loop.");
                Assert.AreEqual(1, store.ClearCalls);
                Assert.AreEqual(0, users.Count);
                Assert.IsFalse(loader.LastLoadReachedDeltaLink);

                await loader.CommitDeltaTokenAsync();
                Assert.AreEqual(0, store.SetCalls, "Nothing is saved.");
                Assert.IsNull(store.Token, "The dead token stays discarded, so the next run starts with a full read instead of replaying it.");
            }
        }

        [TestMethod]
        public async Task RejectedToken_ThatCannotBeDeleted_EndsTheRunIncomplete_WithoutAFullRead()
        {
            var stateStore = new FakeStringValueStore();
            var provider = NewPersistedProvider(stateStore);
            await provider.SetDeltaToken(StoredToken);
            stateStore.ThrowOnDelete = true;
            var writesBefore = stateStore.SetCalls;

            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler().Then(HttpStatusCode.BadRequest, UnsupportedQueryBody);

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, provider, logger, null);

                var users = await loader.LoadAllActiveUsers();

                Assert.AreEqual(1, handler.Requests.Count, "No full read when the dead token could not be discarded.");
                Assert.AreEqual(3, stateStore.DeleteAttempts, "The store's own bounded retries are used first.");
                Assert.AreEqual(0, users.Count);
                Assert.IsFalse(loader.LastLoadReachedDeltaLink, "The run must be reported incomplete.");
                Assert.IsTrue(logger.Messages(LogLevel.Error).Any(m => m.Contains("couldn't discard the rejected /users/delta token")));

                await loader.CommitDeltaTokenAsync();
                Assert.AreEqual(writesBefore, stateStore.SetCalls, "Nothing is saved.");
            }
        }

        [TestMethod]
        public async Task Recovery_AlsoForgetsThePersistedStoresInProcessCopy_SoTheDeadTokenCannotComeBack()
        {
            // #494 keeps the last committed token in memory for when a state-store read fails. Had the recovery only
            // overwritten the key, that copy would hand the rejected token straight back on the next storage blip.
            var stateStore = new FakeStringValueStore();
            var provider = NewPersistedProvider(stateStore);
            await provider.SetDeltaToken(StoredToken);

            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.BadRequest, UnsupportedQueryBody)
                // The full read fails too, so no new token replaces the discarded one.
                .Then(HttpStatusCode.InternalServerError, ServerErrorBody);

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                await new GraphUserLoader(client, provider, logger, null).LoadAllActiveUsers();
            }

            Assert.AreEqual(0, stateStore.Count, "The rejected token was deleted from the state store.");

            stateStore.ThrowOnGet = true;
            await Assert.ThrowsExceptionAsync<DeltaTokenUnavailableException>(() => provider.GetDeltaToken(),
                "With the key deleted and the state store unreadable, the provider must defer the import - not return the token Graph rejected.");
        }

        #endregion

        #region A checkpoint cleared while an import runs stays cleared

        [TestMethod]
        public async Task ANormalDeltaRun_ChecksTheCheckpointIsStillStored_ThenSavesTheNewOne()
        {
            var store = new RecordingDeltaValueProvider(StoredToken);
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler().Then(HttpStatusCode.OK, FinalPage(FreshToken, UserJson(1)));

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, store, logger, null);
                await loader.LoadAllActiveUsers();

                Assert.IsTrue(await loader.CommitDeltaTokenAsync());
                Assert.AreEqual(FreshToken, store.Token);
                CollectionAssert.AreEqual(new[] { "Get", "Get", "Set:" + FreshToken }, store.Calls.ToArray(),
                    "A run that continued from a stored checkpoint reads it again just before replacing it.");
            }
        }

        [TestMethod]
        public async Task ACheckpointClearedWhileTheImportRuns_IsNotSavedBackByThatRun()
        {
            var store = new RecordingDeltaValueProvider(StoredToken);
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler().Then(HttpStatusCode.OK, FinalPage(FreshToken, UserJson(1)));

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, store, logger, null);
                await loader.LoadAllActiveUsers();

                // An admin clears the checkpoint from the portal while the run is still processing users.
                await store.ClearDeltaToken();

                Assert.IsFalse(await loader.CommitDeltaTokenAsync(), "The run must report that it did not save its checkpoint.");
                Assert.IsNull(store.Token, "Saving this run's token would silently undo the clear, and the full re-read would never happen.");
                Assert.AreEqual(0, store.SetCalls);
                Assert.IsTrue(logger.Messages(LogLevel.Warning).Any(m => m.Contains("cleared while this import was running")));
            }
        }

        [TestMethod]
        public async Task AFullRead_SavesItsCheckpoint_EvenIfAClearArrivesMeanwhile()
        {
            var store = new RecordingDeltaValueProvider();
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler().Then(HttpStatusCode.OK, FinalPage(FreshToken, UserJson(1), UserJson(2)));

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, store, logger, null);
                await loader.LoadAllActiveUsers();
                await store.ClearDeltaToken();

                Assert.IsTrue(await loader.CommitDeltaTokenAsync(), "A full read has already done what a clear asks for.");
                Assert.AreEqual(FreshToken, store.Token);
                Assert.AreEqual(1, store.Calls.Count(c => c == "Get"), "A full read has no earlier checkpoint to check for.");
            }
        }

        [TestMethod]
        public async Task AStorageBlipAtCommitTime_StillSavesTheNewCheckpoint()
        {
            var stateStore = new FakeStringValueStore();
            var provider = NewPersistedProvider(stateStore);
            await provider.SetDeltaToken(StoredToken);

            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler().Then(HttpStatusCode.OK, FinalPage(FreshToken, UserJson(1)));

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, provider, logger, null);
                await loader.LoadAllActiveUsers();

                stateStore.ThrowOnGet = true;
                Assert.IsTrue(await loader.CommitDeltaTokenAsync(),
                    "An unreadable store is not a cleared one: the read falls back to the checkpoint this run started from, so it saves as it always did.");
            }

            stateStore.ThrowOnGet = false;
            Assert.AreEqual(FreshToken, await provider.GetDeltaToken());
        }

        [TestMethod]
        public async Task TheKeyTheWebPortalDeletes_IsTheOneTheImporterChecksBeforeSaving()
        {
            var stateStore = new FakeStringValueStore();
            var provider = NewPersistedProvider(stateStore);
            await provider.SetDeltaToken(StoredToken);

            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler().Then(HttpStatusCode.OK, FinalPage(FreshToken, UserJson(1)));

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, provider, logger, null);
                await loader.LoadAllActiveUsers();

                // Exactly the key the Administration > User import page deletes.
                await stateStore.DeleteAsync(UserImportCheckpointKeys.DeltaToken(Guid.Empty));

                Assert.IsFalse(await loader.CommitDeltaTokenAsync());
            }

            Assert.AreEqual(0, stateStore.Count, "The portal's clear must survive a run that was already in progress.");
        }

        #endregion

        #region Disabled and deleted accounts

        /// <summary>
        /// The real JSON, through the real loader: a disabled account and an <c>@removed</c> entry (which carries nothing
        /// but the object id) are not imported, and are not silently dropped either - the delta token moves past them.
        /// </summary>
        [TestMethod]
        public async Task DisabledAndDeletedAccounts_AreLeftOutOfTheActiveUsers_ButReported()
        {
            const string disabledId = "00000000-0000-0000-0000-0000000000d1";
            const string deletedId = "00000000-0000-0000-0000-0000000000d2";
            var disabledJson = "{\"id\":\"" + disabledId + "\",\"userPrincipalName\":\"leaver@contoso.com\",\"accountEnabled\":false,\"department\":\"" + GreekDepartment + "\"}";
            var deletedJson = "{\"@odata.type\":\"#microsoft.graph.user\",\"id\":\"" + deletedId + "\",\"@removed\":{\"reason\":\"changed\"}}";

            var store = new RecordingDeltaValueProvider(StoredToken);
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, FinalPage(FreshToken, UserJson(1), disabledJson, deletedJson));

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, store, logger, null);

                var users = await loader.LoadAllActiveUsers();

                Assert.AreEqual("user1@contoso.com", users.Single().UserPrincipalName, "Only the active user is returned for inserting and updating.");
                var deactivations = loader.LastLoadDeactivations;
                Assert.AreEqual("leaver@contoso.com", deactivations.DisabledUsers.Single().UserPrincipalName);
                Assert.AreEqual(GreekDepartment, deactivations.DisabledUsers.Single().Department, "A disabled user keeps the rest of their profile, for the update of their row.");
                CollectionAssert.AreEqual(new[] { disabledId }, deactivations.DisabledObjectIds.ToArray());
                CollectionAssert.AreEqual(new[] { deletedId }, deactivations.RemovedObjectIds.ToArray());
                Assert.IsTrue(loader.LastLoadReachedDeltaLink);
                Assert.IsFalse(deactivations.IsCompleteDirectoryRead, "An incremental read lists only what changed, so absence from it means nothing.");
                Assert.IsTrue(logger.Messages(LogLevel.Information).Any(m => m.Contains("1 disabled and 1 deleted account(s)")));
            }
        }

        [TestMethod]
        public async Task OnlyACompleteReadFromNoToken_IsTheWholeDirectory()
        {
            var logger = new CapturingLogger();

            // From no token, over every page, to the deltaLink.
            var complete = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, PageWithNextLink(UserJson(1)))
                .Then(HttpStatusCode.OK, FinalPage(FreshToken, UserJson(2)));
            using (var client = new ManualGraphCallClient(complete, logger))
            {
                var loader = new GraphUserLoader(client, new RecordingDeltaValueProvider(), logger, null);
                await loader.LoadAllActiveUsers();

                Assert.IsTrue(loader.LastLoadDeactivations.IsCompleteDirectoryRead);
                Assert.AreEqual(2, loader.LastLoadDeactivations.CompleteReadCount);
                Assert.IsTrue(loader.LastLoadDeactivations.CompleteReadIncludes("00000000-0000-0000-0000-000000000002"));
            }

            // Stopped part-way: only what was read before the failure, so nobody can be judged absent.
            var partial = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, PageWithNextLink(UserJson(1)))
                .Then(HttpStatusCode.InternalServerError, ServerErrorBody);
            using (var client = new ManualGraphCallClient(partial, logger))
            {
                var loader = new GraphUserLoader(client, new RecordingDeltaValueProvider(), logger, null);
                await loader.LoadAllActiveUsers();

                Assert.IsFalse(loader.LastLoadReachedDeltaLink);
                Assert.IsFalse(loader.LastLoadDeactivations.IsCompleteDirectoryRead);
            }
        }

        /// <summary>
        /// Graph's own count of the directory's users - what a complete read is checked against before absence from it
        /// counts as deletion. <c>$count</c> on users is an advanced query, and its answer is plain text.
        /// </summary>
        [TestMethod]
        public async Task DirectoryUserCount_IsAnAdvancedQuery_AndAnyFailureIsJustUnavailable()
        {
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, "\uFEFF30150")
                .Then(HttpStatusCode.Forbidden, ForbiddenBody)
                .Then(HttpStatusCode.OK, "not a number");

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, new RecordingDeltaValueProvider(), logger, null);

                Assert.AreEqual(30150, await loader.CountDirectoryUsersAsync());
                StringAssert.EndsWith(handler.Requests[0], "/v1.0/users/$count");
                Assert.AreEqual("eventual", handler.ConsistencyLevels[0], "Graph answers $count on users only with ConsistencyLevel: eventual.");

                Assert.IsNull(await loader.CountDirectoryUsersAsync(), "A refused count is unavailable: the import falls back, it does not fail.");
                Assert.IsNull(await loader.CountDirectoryUsersAsync(), "So is an answer that is not a number.");
                Assert.AreEqual(3, handler.Requests.Count);
            }
        }

        [TestMethod]
        public void Classify_AnObjectBothDeletedAndLiveInOneRead_IsWhicheverItWasLast()
        {
            var deletedThenRestored = UserDeltaReadRules.Classify(new List<GraphUser> { Tombstone("a"), Live("a", "a@contoso.com", true) });
            Assert.AreEqual(1, deletedThenRestored.ActiveUsers.Count, "Restored after the delete: imported as usual.");
            Assert.AreEqual(0, deletedThenRestored.Deactivations.RemovedObjectIds.Count);

            var changedThenDeleted = UserDeltaReadRules.Classify(new List<GraphUser> { Live("a", "a@contoso.com", true), Tombstone("a") });
            Assert.AreEqual(0, changedThenDeleted.ActiveUsers.Count, "Deleted after the change: not imported.");
            CollectionAssert.AreEqual(new[] { "a" }, changedThenDeleted.Deactivations.RemovedObjectIds.ToArray());
        }

        /// <summary>
        /// Graph lists an object once per change, in the order the changes happened, so its last entry is its current
        /// state - and that entry can leave out what did not change, the sign-in name included.
        /// </summary>
        [DataTestMethod]
        [DataRow(true, DisplayName = "Re-enabling entry carries the sign-in name")]
        [DataRow(false, DisplayName = "Re-enabling entry has no sign-in name")]
        public void Classify_DisabledThenReEnabledInOneRead_IsImportedAsEnabled(bool laterEntryHasUpn)
        {
            var read = UserDeltaReadRules.Classify(new List<GraphUser>
            {
                Live("a", "x@contoso.com", false),
                Live("a", laterEntryHasUpn ? "x@contoso.com" : null, true),
            });

            var user = read.ActiveUsers.Single();
            Assert.AreEqual("x@contoso.com", user.UserPrincipalName);
            Assert.AreEqual(true, user.AccountEnabled, "Written as it is now.");
            Assert.AreEqual(0, read.Deactivations.DisabledUsers.Count);
            Assert.AreEqual(0, read.Deactivations.DisabledObjectIds.Count);
        }

        [DataTestMethod]
        [DataRow(true, DisplayName = "Disabling entry carries the sign-in name")]
        [DataRow(false, DisplayName = "Disabling entry has no sign-in name")]
        public void Classify_EnabledThenDisabledInOneRead_IsDisabled(bool laterEntryHasUpn)
        {
            var read = UserDeltaReadRules.Classify(new List<GraphUser>
            {
                Live("a", "x@contoso.com", true),
                Live("a", laterEntryHasUpn ? "x@contoso.com" : null, false),
            });

            Assert.AreEqual(0, read.ActiveUsers.Count);
            var user = read.Deactivations.DisabledUsers.Single();
            Assert.AreEqual("x@contoso.com", user.UserPrincipalName, "The row is still found by sign-in name, from the earlier entry.");
            Assert.AreEqual(false, user.AccountEnabled, "Written as it is now.");
            CollectionAssert.AreEqual(new[] { "a" }, read.Deactivations.DisabledObjectIds.ToArray());
        }

        [TestMethod]
        public void Classify_AnEntryWithoutAccountStatus_LeavesTheStatus_AndBringsTheNewestProfile()
        {
            var read = UserDeltaReadRules.Classify(new List<GraphUser>
            {
                // A change reported with only the properties that changed: no sign-in name, but plainly disabled.
                new GraphUser { Id = "no-upn", AccountEnabled = false },
                // A later entry without accountEnabled leaves the account disabled, and carries the newer profile.
                Live("b", "b@contoso.com", false),
                new GraphUser { Id = "b", UserPrincipalName = "b.renamed@contoso.com", Department = "Πωλήσεις" },
                // No accountEnabled at all: neither imported nor disabled, as before.
                Live("unknown", "unknown@contoso.com", null),
            });

            Assert.AreEqual(0, read.ActiveUsers.Count);
            CollectionAssert.AreEqual(new[] { "no-upn", "b" }, read.Deactivations.DisabledObjectIds.ToArray());
            var b = read.Deactivations.DisabledUsers.Single();
            Assert.AreEqual("b.renamed@contoso.com", b.UserPrincipalName);
            Assert.AreEqual("Πωλήσεις", b.Department);
            Assert.AreEqual(false, b.AccountEnabled);
        }

        private static GraphUser Tombstone(string id)
            => new GraphUser { Id = id, Removed = Newtonsoft.Json.Linq.JObject.Parse("{\"reason\":\"deleted\"}") };

        private static GraphUser Live(string id, string upn, bool? enabled)
            => new GraphUser { Id = id, UserPrincipalName = upn, AccountEnabled = enabled };

        #endregion

        #region A page that comes back without its value collection

        /// <summary>
        /// A 200 page with no <c>value</c> collection is a page whose users are unknown, not an empty page. Stepping over
        /// it to the deltaLink made the read look complete - and a complete read is now what decides who has been
        /// deleted. It must stop the read exactly as an HTTP failure on that page would, and it is not Graph rejecting
        /// the configured organisation attributes.
        /// </summary>
        [TestMethod]
        public async Task APageWithoutItsValueCollection_StopsTheRead_LikeAnyFailedPage()
        {
            var store = new RecordingDeltaValueProvider();
            var logger = new CapturingLogger();
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, PageWithNextLink(UserJson(1)))
                .Then(HttpStatusCode.OK, PageWithoutValue("page-3"))
                .Then(HttpStatusCode.OK, FinalPage(FreshToken, UserJson(3)));

            using (var client = new ManualGraphCallClient(handler, logger))
            {
                var loader = new GraphUserLoader(client, store, logger, null);
                loader.SetOrgSelection(GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute1" }));

                var users = await loader.LoadAllActiveUsers();

                Assert.AreEqual(2, handler.Requests.Count, "The read stops at that page, and is not read again without the organisation attributes.");
                Assert.AreEqual(1, users.Count, "The users read before it are still returned, as for any failed page.");
                Assert.IsFalse(loader.LastLoadReachedDeltaLink, "A read that skipped a page is not complete.");
                Assert.IsFalse(loader.LastLoadDeactivations.IsCompleteDirectoryRead, "So nobody can be judged deleted by their absence from it.");
                Assert.IsFalse(loader.OrgSelectionWasRejected, "Graph did not reject the organisation attributes.");
                Assert.IsFalse(await loader.CommitDeltaTokenAsync(), "There is no new checkpoint to save.");
                Assert.AreEqual(0, store.SetCalls);
                StringAssert.Contains(logger.Messages(LogLevel.Warning).Single(m => m.Contains("/users/delta read stopped")), "page 2");
            }
        }

        /// <summary>
        /// The same through the whole import: the full read after a cleared checkpoint loses the page that held one
        /// user. Before, that read counted as complete, the user was absent from it, and their row was marked disabled.
        /// </summary>
        [TestMethod]
        public async Task FullReadWithAPageMissingItsValue_DisablesNobodyForBeingAbsent_AndSavesNoCheckpoint()
        {
            var ticks = DateTime.UtcNow.Ticks;
            var firstId = Guid.NewGuid().ToString();
            var lostId = Guid.NewGuid().ToString();
            var lastId = Guid.NewGuid().ToString();
            var firstUpn = $"page-first-{ticks}@contoso.com";
            var lostUpn = $"page-lost-{ticks}@contoso.com";
            var lastUpn = $"page-last-{ticks}@contoso.com";
            await DeleteTestUsers(firstUpn, lostUpn, lastUpn);

            // Everyone else in the shared test database is in this directory too, as entries that change nothing.
            var everyoneElse = await EveryoneElseInTheUsersTableAsJsonAsync();
            var store = new RecordingDeltaValueProvider();
            var channel = new RecordingTelemetryChannel();
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, FinalPage("checkpoint-1", everyoneElse.Concat(new[] { UserJson(firstId, firstUpn, "10001"), UserJson(lostId, lostUpn, "10001"), UserJson(lastId, lastUpn, "10001") }).ToArray()))
                .Then(HttpStatusCode.OK, PageWithNextLink(everyoneElse.Concat(new[] { UserJson(firstId, firstUpn, "10001") }).ToArray()))
                .Then(HttpStatusCode.OK, PageWithoutValue("page-3"))
                .Then(HttpStatusCode.OK, FinalPage("checkpoint-2", UserJson(lastId, lastUpn, "10001")));

            try
            {
                using (var configuration = NewTelemetryConfiguration(channel))
                {
                    var logger = new AnalyticsLogger(new TelemetryClient(configuration), "UserImportTest");
                    using (var client = new ManualGraphCallClient(handler, logger))
                    {
                        var loader = new GraphDeltaReadWithoutLicenceCalls(new GraphUserLoader(client, store, logger, null));
                        Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(), "Run 1");

                        // The checkpoint is cleared - an admin's full re-read, or a UserGroupsFilter catch-up - so run 2 reads everyone.
                        await store.ClearDeltaToken();
                        Assert.IsFalse(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(),
                            "A read that skipped a page is not a finished user import.");
                        Assert.IsNull(store.Token, "Nor is its checkpoint saved: the next run reads everyone again.");
                    }
                }

                Assert.AreEqual(3, handler.Requests.Count, "Run 2 stops at the page without its value collection.");
                using (var db = new AnalyticsEntitiesContext())
                {
                    Assert.AreEqual(true, (await db.users.SingleAsync(u => u.UserPrincipalName == lostUpn)).AccountEnabled,
                        "The user on the lost page was not shown to be deleted, so must not be disabled.");
                }
                Assert.IsTrue(Warnings(channel).Any(w => w.Contains("NOT moving the user checkpoint forward")));
            }
            finally
            {
                await DeleteTestUsers(firstUpn, lostUpn, lastUpn);
            }
        }

        private static string PageWithoutValue(string nextSkipToken)
            => "{\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=" + nextSkipToken + "\"}";

        /// <summary>Every users row with an object id, as a directory entry with nothing else - so it changes nothing.</summary>
        private static async Task<string[]> EveryoneElseInTheUsersTableAsJsonAsync()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var ids = await db.users.Where(u => u.AzureAdId != null && u.AzureAdId != "").Select(u => u.AzureAdId).ToListAsync();
                return ids.Select(id => "{\"id\":" + Newtonsoft.Json.JsonConvert.ToString(id) + "}").ToArray();
            }
        }

        #endregion

        #region UserMetadataUpdater: an incomplete read is not a finished import

        [TestMethod]
        public async Task UserMetadataUpdater_IncompleteDeltaRead_IsReportedIncomplete_AndDoesNotMoveTheCheckpoint()
        {
            var ticks = DateTime.UtcNow.Ticks;
            var firstUpn = $"delta-complete-{ticks}@contoso.com";
            var secondUpn = $"delta-partial-{ticks}@contoso.com";
            await DeleteTestUsers(firstUpn, secondUpn);

            var channel = new RecordingTelemetryChannel();
            try
            {
                using (var configuration = NewTelemetryConfiguration(channel))
                {
                    var logger = new AnalyticsLogger(new TelemetryClient(configuration), "UserImportTest");
                    var loader = new FakeUserMetadataLoader(new List<GraphUser> { NewGraphUser(firstUpn) })
                    {
                        SimulatedNewDeltaToken = "checkpoint-from-a-complete-read",
                    };

                    Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(),
                        "A read that reached its deltaLink, with every phase done, is a completed run.");
                    Assert.AreEqual("checkpoint-from-a-complete-read", await loader.DeltaValueProvider.GetDeltaToken());

                    loader.SetFakeState(new List<GraphUser> { NewGraphUser(secondUpn) }, null, null);
                    loader.SimulateIncompleteDeltaRead = true;
                    loader.SimulatedNewDeltaToken = "must-never-be-saved";

                    Assert.IsFalse(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(),
                        "A read that stopped early must not be reported as a finished user import: the section would stamp its cadence gate and send a 'finished section' event.");
                    Assert.AreEqual("checkpoint-from-a-complete-read", await loader.DeltaValueProvider.GetDeltaToken(),
                        "The checkpoint must not move.");
                    Assert.IsTrue(Warnings(channel).Any(w => w.Contains("NOT moving the user checkpoint forward")),
                        "The log must say the checkpoint was not moved forward, rather than the commit quietly doing nothing.");
                }

                using (var db = new AnalyticsEntitiesContext())
                {
                    Assert.AreEqual(1, await db.users.CountAsync(u => u.UserPrincipalName == secondUpn),
                        "The users that WERE read are still saved, as they were before this change.");
                }
            }
            finally
            {
                await DeleteTestUsers(firstUpn, secondUpn);
            }
        }

        /// <summary>
        /// The whole story in the real loader: a clean first run, a token that Graph later refuses, recovery in
        /// the same run with no admin action, and then a transient failure that must not undo the recovery. That
        /// the section returning false still lets the following sections run is pinned by
        /// <c>GraphImporter_SectionReturnsFalse_StillRunsSubsequentSections</c>.
        /// </summary>
        [TestMethod]
        public async Task ExpiredToken_EndToEnd_TheImportRecoversWithoutAdminAction_AndALaterFailureKeepsTheNewCheckpoint()
        {
            var upn = $"delta-expiry-{DateTime.UtcNow.Ticks}@contoso.com";
            var aadId = Guid.NewGuid().ToString();
            await DeleteTestUsers(upn);

            var channel = new RecordingTelemetryChannel();
            var store = new RecordingDeltaValueProvider();
            var handler = new ScriptedGraphHandler()
                // Run 1: no token yet, so a full read, ending in the first checkpoint.
                .Then(HttpStatusCode.OK, FinalPage("checkpoint-1", UserJson(aadId, upn, "10001")))
                // Run 2: weeks later Graph refuses that checkpoint as too old...
                .Then(HttpStatusCode.BadRequest, UnsupportedQueryBody)
                // ...so the importer reads everyone again, and catches up on the change made in the meantime.
                .Then(HttpStatusCode.OK, FinalPage("checkpoint-2", UserJson(aadId, upn, "20002")))
                // Run 3: a permissions fault is not an expired token.
                .Then(HttpStatusCode.Forbidden, ForbiddenBody);

            try
            {
                using (var configuration = NewTelemetryConfiguration(channel))
                {
                    var logger = new AnalyticsLogger(new TelemetryClient(configuration), "UserImportTest");
                    using (var client = new ManualGraphCallClient(handler, logger))
                    {
                        var loader = new GraphDeltaReadWithoutLicenceCalls(new GraphUserLoader(client, store, logger, null));

                        Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(), "Run 1");
                        Assert.AreEqual("checkpoint-1", store.Token);

                        Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(),
                            "Run 2 must recover within the run, with no admin action.");
                        Assert.AreEqual("checkpoint-2", store.Token, "The checkpoint from the recovery read is saved once the import has succeeded.");
                        CollectionAssert.AreEqual(new[] { "Get", "Clear", "Set:checkpoint-2" }, store.CallsAfter("Set:checkpoint-1").ToArray(),
                            "Run 2 reads the stored token, discards it, and saves the new one only at the end.");

                        Assert.IsFalse(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(),
                            "Run 3's 403 is an incomplete run, not a finished one.");
                        Assert.AreEqual("checkpoint-2", store.Token, "A 403 must never discard the checkpoint.");
                    }
                }

                Assert.AreEqual(4, handler.Requests.Count);
                Assert.IsFalse(handler.Requests[0].Contains("$deltatoken"));
                StringAssert.Contains(handler.Requests[1], "$deltatoken=checkpoint-1");
                Assert.IsFalse(handler.Requests[2].Contains("$deltatoken"));
                StringAssert.Contains(handler.Requests[3], "$deltatoken=checkpoint-2");

                using (var db = new AnalyticsEntitiesContext())
                {
                    var dbUser = await db.users.SingleAsync(u => u.UserPrincipalName == upn);
                    Assert.AreEqual("20002", dbUser.PostalCode, "The change made while the token was dead arrived through the recovery read.");
                }

                var warnings = Warnings(channel);
                Assert.IsTrue(warnings.Any(w => w.Contains("rejected the stored /users/delta token") && w.Contains("HTTP 400") && w.Contains("Request_UnsupportedQuery")));
                Assert.IsTrue(warnings.Any(w => w.Contains("/users/delta read stopped") && w.Contains("HTTP 403") && w.Contains("token was kept")));
                Assert.IsTrue(warnings.Any(w => w.Contains("NOT moving the user checkpoint forward")));
            }
            finally
            {
                await DeleteTestUsers(upn);
            }
        }

        /// <summary>
        /// The web portal's clear, arriving while a user import is running. That run must not save its checkpoint
        /// over the clear, and must not be recorded as finished - otherwise the cadence gate would hold the full
        /// re-read the admin asked for back for a whole interval.
        /// </summary>
        [TestMethod]
        public async Task ClearedWhileRunning_EndToEnd_TheRunIsNotFinished_AndTheNextRunReadsEveryone()
        {
            var upn = $"delta-cleared-{DateTime.UtcNow.Ticks}@contoso.com";
            var aadId = Guid.NewGuid().ToString();
            await DeleteTestUsers(upn);

            var channel = new RecordingTelemetryChannel();
            var store = new RecordingDeltaValueProvider();
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, FinalPage("checkpoint-1", UserJson(aadId, upn, "10001")))
                .Then(HttpStatusCode.OK, FinalPage("checkpoint-2", UserJson(aadId, upn, "20002")))
                .Then(HttpStatusCode.OK, FinalPage("checkpoint-3", UserJson(aadId, upn, "30003")));

            try
            {
                using (var configuration = NewTelemetryConfiguration(channel))
                {
                    var logger = new AnalyticsLogger(new TelemetryClient(configuration), "UserImportTest");
                    using (var client = new ManualGraphCallClient(handler, logger))
                    {
                        var loader = new GraphDeltaReadWithoutLicenceCalls(new GraphUserLoader(client, store, logger, null));

                        Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(), "Run 1");
                        Assert.AreEqual("checkpoint-1", store.Token);

                        loader.DuringRun = () => store.ClearDeltaToken().GetAwaiter().GetResult();
                        Assert.IsFalse(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(),
                            "Run 2 was cleared part-way through, so it must not be recorded as a finished user import.");
                        Assert.IsNull(store.Token, "Run 2 must not save its checkpoint over the clear.");

                        loader.DuringRun = null;
                        Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(), "Run 3");
                        Assert.AreEqual("checkpoint-3", store.Token);
                    }
                }

                Assert.AreEqual(3, handler.Requests.Count);
                StringAssert.Contains(handler.Requests[1], "$deltatoken=checkpoint-1");
                Assert.IsFalse(handler.Requests[2].Contains("$deltatoken"), "Run 3 reads every user, as the clear asked.");
                Assert.IsTrue(Warnings(channel).Any(w => w.Contains("cleared while this import was running")));

                using (var db = new AnalyticsEntitiesContext())
                {
                    Assert.AreEqual("30003", (await db.users.SingleAsync(u => u.UserPrincipalName == upn)).PostalCode);
                }
            }
            finally
            {
                await DeleteTestUsers(upn);
            }
        }

        #endregion

        #region Helpers

        private static string UserJson(int n)
            => UserJson($"00000000-0000-0000-0000-{n:D12}", $"user{n}@contoso.com", "10001");

        private static string UserJson(string id, string upn, string postalCode)
            => "{\"id\":\"" + id + "\",\"userPrincipalName\":\"" + upn + "\",\"accountEnabled\":true," +
               "\"department\":\"" + GreekDepartment + "\",\"postalCode\":\"" + postalCode + "\"}";

        private static string FinalPage(string deltaToken, params string[] users)
            => "{\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=" + deltaToken + "\"," +
               "\"value\":[" + string.Join(",", users) + "]}";

        private static string PageWithNextLink(params string[] users)
            => "{\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=page-2\"," +
               "\"value\":[" + string.Join(",", users) + "]}";

        private static GraphUser NewGraphUser(string upn)
            => new GraphUser { Id = Guid.NewGuid().ToString(), UserPrincipalName = upn, AccountEnabled = true, Mail = upn };

        private static PersistedDeltaValueProvider NewPersistedProvider(FakeStringValueStore store)
        {
            var config = (AppConfig)FormatterServices.GetUninitializedObject(typeof(AppConfig));
            config.TenantGUID = Guid.Empty;
            config.ConnectionStrings = new AppConnectionStrings();

            return new PersistedDeltaValueProvider(config, AnalyticsLogger.ConsoleOnlyTracer(), store, new DeltaTokenStoreRetryOptions(3, TimeSpan.Zero));
        }

        private static TelemetryConfiguration NewTelemetryConfiguration(RecordingTelemetryChannel channel)
            => new TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            };

        private static List<string> Warnings(RecordingTelemetryChannel channel)
            => channel.Sent.OfType<TraceTelemetry>()
                .Where(t => t.SeverityLevel == SeverityLevel.Warning)
                .Select(t => t.Message)
                .ToList();

        private static async Task DeleteTestUsers(params string[] upns)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var users = await db.users.Include(u => u.LicenseLookups).Where(u => upns.Contains(u.UserPrincipalName)).ToListAsync();
                if (users.Count == 0)
                {
                    return;
                }

                foreach (var user in users)
                {
                    db.UserLicenseTypeLookups.RemoveRange(user.LicenseLookups);
                }
                db.users.RemoveRange(users);
                await db.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Plays back scripted Graph responses in order and records every URL requested. An unscripted request
        /// fails the test rather than being answered, so "exactly N requests" can't pass by accident.
        /// </summary>
        private sealed class ScriptedGraphHandler : HttpMessageHandler
        {
            private readonly object _gate = new object();
            private readonly Queue<Func<HttpResponseMessage>> _responses = new Queue<Func<HttpResponseMessage>>();
            private readonly List<string> _requests = new List<string>();
            private readonly List<string> _consistencyLevels = new List<string>();

            public IReadOnlyList<string> Requests
            {
                get
                {
                    lock (_gate)
                    {
                        return _requests.ToList();
                    }
                }
            }

            /// <summary>Each request's <c>ConsistencyLevel</c> header, or null when it had none.</summary>
            public IReadOnlyList<string> ConsistencyLevels
            {
                get
                {
                    lock (_gate)
                    {
                        return _consistencyLevels.ToList();
                    }
                }
            }

            public ScriptedGraphHandler Then(HttpStatusCode status, string body, string retryAfterSeconds = null)
            {
                _responses.Enqueue(() =>
                {
                    var response = new HttpResponseMessage(status) { Content = new StringContent(body ?? string.Empty) };
                    if (retryAfterSeconds != null)
                    {
                        response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds);
                    }
                    return response;
                });
                return this;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Func<HttpResponseMessage> next;
                lock (_gate)
                {
                    _requests.Add(request.RequestUri.OriginalString);
                    _consistencyLevels.Add(request.Headers.TryGetValues("ConsistencyLevel", out var levels) ? string.Join(",", levels) : null);
                    next = _responses.Count > 0 ? _responses.Dequeue() : null;
                }

                if (next == null)
                {
                    return Task.FromException<HttpResponseMessage>(new InvalidOperationException($"Unscripted Graph request: {request.RequestUri.OriginalString}"));
                }

                var response = next();
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
        }

        /// <summary>A token store that records every call made to it, in order.</summary>
        private sealed class RecordingDeltaValueProvider : IDeltaValueProvider
        {
            private readonly List<string> _calls = new List<string>();

            public RecordingDeltaValueProvider(string token = null)
            {
                Token = token;
            }

            public string Token { get; private set; }
            public int ClearCalls => _calls.Count(c => c == "Clear");
            public int SetCalls => _calls.Count(c => c.StartsWith("Set:", StringComparison.Ordinal));
            public IReadOnlyList<string> Calls => _calls.ToList();

            /// <summary>The calls made after the last occurrence of <paramref name="marker"/>.</summary>
            public List<string> CallsAfter(string marker)
            {
                var index = _calls.LastIndexOf(marker);
                Assert.IsTrue(index >= 0, $"Expected a '{marker}' call. Calls: {string.Join(", ", _calls)}");
                return _calls.Skip(index + 1).ToList();
            }

            public Task<string> GetDeltaToken(CancellationToken cancellationToken = default)
            {
                _calls.Add("Get");
                return Task.FromResult(Token);
            }

            public Task SetDeltaToken(string deltaToken, CancellationToken cancellationToken = default)
            {
                _calls.Add("Set:" + deltaToken);
                Token = deltaToken;
                return Task.CompletedTask;
            }

            public Task ClearDeltaToken(CancellationToken cancellationToken = default)
            {
                _calls.Add("Clear");
                Token = null;
                return Task.CompletedTask;
            }

            /// <summary>
            /// The last qualifier the loader set. Not recorded in <see cref="Calls"/>: these tests configure no organisation
            /// attributes, so the key never moves, and the call order they pin is about the token alone.
            /// </summary>
            public string KeyQualifier { get; private set; } = string.Empty;

            public void SetKeyQualifier(string qualifier)
            {
                KeyQualifier = qualifier ?? string.Empty;
            }
        }

        /// <summary>Stands in for the runtime state table behind <see cref="PersistedDeltaValueProvider"/>.</summary>
        private sealed class FakeStringValueStore : IKeyValueStore
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

            public bool ThrowOnGet { get; set; }
            public bool ThrowOnDelete { get; set; }
            public int DeleteAttempts { get; private set; }
            public int SetCalls { get; private set; }
            public int Count => _values.Count;

            public string Description => "fake state store";

            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                if (ThrowOnGet) throw new InvalidOperationException("synthetic state store outage");
                return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
            }

            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                SetCalls++;
                _values[key] = value;
                return Task.CompletedTask;
            }

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                DeleteAttempts++;
                if (ThrowOnDelete) throw new InvalidOperationException("synthetic state store outage");
                return Task.FromResult(_values.Remove(key));
            }

            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_values.ContainsKey(key));
            }
        }

        /// <summary>
        /// The real <see cref="GraphUserLoader"/> for the <c>/users/delta</c> read and its checkpoint. The licence
        /// calls need a GraphServiceClient and are not what these tests are about, so they answer "unavailable",
        /// which leaves every existing licence as it is.
        /// </summary>
        private sealed class GraphDeltaReadWithoutLicenceCalls : IUserMetadataLoader
        {
            private readonly GraphUserLoader _graph;

            public GraphDeltaReadWithoutLicenceCalls(GraphUserLoader graph)
            {
                _graph = graph;
            }

            public IDeltaValueProvider DeltaValueProvider => _graph.DeltaValueProvider;
            public bool LastLoadReachedDeltaLink => _graph.LastLoadReachedDeltaLink;
            public void SetOrgSelection(GraphUserOrgSelection orgSelection) => _graph.SetOrgSelection(orgSelection);
            public Task ClearStoredDeltaTokensAsync() => _graph.ClearStoredDeltaTokensAsync();
            public bool OrgSelectionWasRejected => _graph.OrgSelectionWasRejected;
            public Task<List<GraphUser>> LoadAllActiveUsers() => _graph.LoadAllActiveUsers();
            public GraphUserDeactivations LastLoadDeactivations => _graph.LastLoadDeactivations;

            /// <summary>"Could not be read": these tests script every request, and the count is not what they are about.</summary>
            public Task<int?> CountDirectoryUsersAsync() => Task.FromResult<int?>(null);
            public Task<bool> CommitDeltaTokenAsync() => _graph.CommitDeltaTokenAsync();
            public Task<List<SubscribedSku>> LoadTenantSkus()
            {
                // The updater calls this part-way through a run, after the /users/delta read: the moment to
                // simulate something else happening while the import is running.
                DuringRun?.Invoke();
                return Task.FromResult<List<SubscribedSku>>(null);
            }

            /// <summary>Runs in the middle of each import, after the delta read and before the commit.</summary>
            public Action DuringRun { get; set; }
            public Task<List<Microsoft.Graph.Models.User>> LoadUsersBySku(Guid skuId) => Task.FromResult(new List<Microsoft.Graph.Models.User>());
            public Task<List<LicenseDetails>> LoadUserLicenseDetails(string userId) => Task.FromResult<List<LicenseDetails>>(null);
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly object _gate = new object();
            private readonly List<Tuple<LogLevel, string>> _entries = new List<Tuple<LogLevel, string>>();

            public List<string> Messages(LogLevel level)
            {
                lock (_gate)
                {
                    return _entries.Where(e => e.Item1 == level).Select(e => e.Item2).ToList();
                }
            }

            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (_gate)
                {
                    _entries.Add(Tuple.Create(logLevel, formatter(state, exception)));
                }
            }
        }

        private sealed class RecordingTelemetryChannel : ITelemetryChannel
        {
            private readonly object _gate = new object();
            private readonly List<ITelemetry> _sent = new List<ITelemetry>();

            public IList<ITelemetry> Sent
            {
                get
                {
                    lock (_gate)
                    {
                        return _sent.ToList();
                    }
                }
            }

            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }

            public void Send(ITelemetry item)
            {
                lock (_gate)
                {
                    _sent.Add(item);
                }
            }

            public void Flush()
            {
            }

            public void Dispose()
            {
            }
        }

        #endregion
    }
}
