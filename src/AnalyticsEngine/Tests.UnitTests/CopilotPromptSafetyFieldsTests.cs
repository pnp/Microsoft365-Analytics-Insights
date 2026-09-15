using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for issue #570 - the two Copilot prompt-safety flags the importer never normalised, and
    /// the billing bug found while fixing them.
    ///
    ///   * Messages[].JailbreakDetected            -> copilot_event_messages.jailbreak_detected
    ///   * AccessedResources[].XPIADetected        -> copilot_event_accessed_resources.xpia_detected
    ///   * Messages[].isPrompt is now bool?        -> an omitted flag is no longer billed as a response
    ///
    /// Both flags are documented on https://learn.microsoft.com/en-us/purview/audit-copilot but absent
    /// from the older OData Management API schema the models were written against, which is how they were
    /// missed. They failed differently, and both failure modes are covered here: JailbreakDetected was
    /// dropped at typed deserialisation (not on the model at all), XPIADetected reached the staging JSON
    /// and was dropped by the merge.
    ///
    /// These exercise the real merge SQL (common_upsert_copilot_agents.sql) against the test database,
    /// because for XPIADetected the merge is where the bug actually was.
    /// </summary>
    [TestClass]
    public class CopilotPromptSafetyFieldsTests : CopilotTestBase
    {
        #region Serialisation - the JailbreakDetected failure mode

        /// <summary>
        /// The regression test for the deserialisation half of #570. JailbreakDetected was not a property
        /// on Message, so Newtonsoft silently discarded it and it never reached the staging JSON. Parsing
        /// a raw payload (rather than constructing a Message) is what makes this a real test: building the
        /// object in C# would pass even with the property missing from the JSON contract.
        /// </summary>
        [TestMethod]
        public void Copilot_JailbreakDetected_SurvivesTypedDeserialisation()
        {
            var raw = @"{
                'CopilotEventData': { 'AppHost': 'Teams' },
                'Messages': [
                    { 'Id': 'prompt-1', 'isPrompt': true, 'JailbreakDetected': true },
                    { 'Id': 'response-1', 'isPrompt': false, 'JailbreakDetected': false }
                ]
            }".Replace('\'', '"');

            var parsed = JsonConvert.DeserializeObject<CopilotAuditEvent>(raw);

            Assert.AreEqual(2, parsed.Messages.Count);
            Assert.AreEqual(true, parsed.Messages.Single(m => m.Id == "prompt-1").JailbreakDetected,
                "JailbreakDetected must bind from the payload - it used to be dropped here.");
            Assert.AreEqual(false, parsed.Messages.Single(m => m.Id == "response-1").JailbreakDetected);
        }

        /// <summary>
        /// A payload that omits the flag must give null (unknown), not false (explicitly not a jailbreak).
        /// </summary>
        [TestMethod]
        public void Copilot_JailbreakDetected_IsNullWhenAbsent()
        {
            var raw = @"{ 'Messages': [ { 'Id': 'prompt-1', 'isPrompt': true } ] }".Replace('\'', '"');

            var parsed = JsonConvert.DeserializeObject<CopilotAuditEvent>(raw);

            Assert.IsNull(parsed.Messages.Single().JailbreakDetected,
                "An absent flag is 'unknown', which must stay distinguishable from an explicit false.");
        }

        /// <summary>
        /// SerializeMessages runs off the typed model, so the flag only reaches staging if the model
        /// carries it. This is the link between the two halves of the deserialisation failure.
        /// </summary>
        [TestMethod]
        public void Copilot_SerializeMessages_CarriesJailbreakDetectedToStaging()
        {
            var manager = NewManager();

            var json = manager.SerializeMessages(new CopilotAuditLogContent
            {
                ParsedAuditEvent = new CopilotAuditEvent
                {
                    Messages = new List<Message>
                    {
                        new Message { Id = "prompt-1", IsPrompt = true, JailbreakDetected = true },
                    },
                },
            });

            StringAssert.Contains(json, "JailbreakDetected",
                "The staging JSON is what the merge reads, so the flag has to appear in it.");
            var back = JsonConvert.DeserializeObject<List<Message>>(json);
            Assert.AreEqual(true, back.Single().JailbreakDetected);
        }

        /// <summary>
        /// XPIADetected was always on the model and always reached staging - the merge just ignored it.
        /// Asserted so a future model change cannot quietly remove the half that did work.
        /// </summary>
        [TestMethod]
        public void Copilot_SerializeAccessedResources_CarriesXpiaDetectedToStaging()
        {
            var manager = NewManager();

            var json = manager.SerializeAccessedResources(new List<AccessedResource>
            {
                new AccessedResource { Id = "resource-alpha", XPIADetected = true },
            });

            StringAssert.Contains(json, "XPIADetected");
            var back = JsonConvert.DeserializeObject<List<AccessedResource>>(json);
            Assert.AreEqual(true, back.Single().XPIADetected);
        }

        #endregion

        #region IsPrompt nullability and the billing bug

        /// <summary>
        /// The live correctness bug in #570. Message.IsPrompt was a non-nullable bool, so a payload that
        /// omitted isPrompt deserialised to false - indistinguishable from an explicit "this is a
        /// response". Only responses are billable, so an absent flag was charged as a generative answer
        /// and silently over-stated estimated credit consumption.
        /// </summary>
        [TestMethod]
        public void Copilot_MessageWithNoIsPromptFlag_IsNotBilledAsAResponse()
        {
            var raw = @"{ 'Messages': [ { 'Id': 'direction-unknown' } ] }".Replace('\'', '"');
            var parsed = JsonConvert.DeserializeObject<CopilotAuditEvent>(raw);

            Assert.IsNull(parsed.Messages.Single().IsPrompt,
                "An omitted isPrompt must deserialise to null, not false.");

            // isCustomAgent: true - only custom agents are charged in Copilot Credits at all, so the
            // billing path this bug lived on is unreachable with false.
            var report = CopilotCreditEstimation.Analyze(parsed, true);

            Assert.AreEqual(0, report.GenerativeAnswers,
                "A message whose direction is unknown must not be counted as a Copilot response.");
            Assert.AreEqual(0, report.TotalCredits,
                "...and therefore must not be billed. This is the over-estimation bug in issue #570.");
        }

        /// <summary>
        /// The other side of the same change: an explicit response must still be billed exactly as before,
        /// so the fix cannot be "stop billing everything".
        /// </summary>
        [TestMethod]
        public void Copilot_ExplicitResponse_IsStillBilled()
        {
            var raw = @"{ 'Messages': [
                { 'Id': 'prompt-1', 'isPrompt': true },
                { 'Id': 'response-1', 'isPrompt': false }
            ] }".Replace('\'', '"');
            var parsed = JsonConvert.DeserializeObject<CopilotAuditEvent>(raw);

            var report = CopilotCreditEstimation.Analyze(parsed, true);

            Assert.AreEqual(1, report.GenerativeAnswers,
                "Exactly the one explicit response is billable; the prompt is not.");
            Assert.IsTrue(report.TotalCredits > 0, "An explicit response must still cost credits.");
        }

        #endregion

        #region End-to-end persistence through the merge

        /// <summary>
        /// The headline case: both flags travel all the way from the audit payload into their columns via
        /// the real merge SQL. For XPIADetected this is the only test that would have caught the original
        /// bug, because the value was already correct everywhere up to the merge.
        /// </summary>
        [TestMethod]
        public async Task Copilot_PromptSafetyFlags_ArePersisted()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (!await SafetyColumnsExist(db)) { Assert.Inconclusive("CopilotPromptSafetyFields migration not applied."); return; }

                await ClearEvents(db);
                await ClearAccessedResources(db);
                await ClearMessages(db);

                var manager = NewManager();
                var commonEvent = await AddCommonEvent(db, "Prompt Safety Flags");

                await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource
                            {
                                Id = "resource-poisoned",
                                Name = "poisoned.docx",
                                Type = "docx",
                                SiteUrl = "https://contoso.sharepoint.com/sites/example",
                                Action = "Read",
                                XPIADetected = true,
                            },
                        },
                    },
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message>
                        {
                            new Message { Id = "prompt-1", IsPrompt = true, JailbreakDetected = true },
                            new Message { Id = "response-1", IsPrompt = false, JailbreakDetected = false },
                        },
                    },
                }, commonEvent);

                await manager.CommitAllChanges();

                var resource = await db.CopilotEventAccessedResources
                    .SingleAsync(r => r.ChatId == commonEvent.Id);
                Assert.AreEqual(true, resource.XpiaDetected,
                    "XPIADetected reached staging before this fix but the merge never extracted it.");

                var messages = await db.CopilotMessages.Where(m => m.ChatId == commonEvent.Id).ToListAsync();
                Assert.AreEqual(2, messages.Count);
                Assert.AreEqual(true, messages.Single(m => m.MessageId == "prompt-1").JailbreakDetected);
                Assert.AreEqual(false, messages.Single(m => m.MessageId == "response-1").JailbreakDetected);
            }
        }

        /// <summary>
        /// An older-shaped payload carrying neither flag must import exactly as before, leaving NULL
        /// rather than inventing a false. "No jailbreak was detected" and "this build never looked" are
        /// different facts, and a security signal must not blur them.
        /// </summary>
        [TestMethod]
        public async Task Copilot_MissingPromptSafetyFlags_ImportCleanlyAsNulls()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (!await SafetyColumnsExist(db)) { Assert.Inconclusive("CopilotPromptSafetyFields migration not applied."); return; }

                await ClearEvents(db);
                await ClearAccessedResources(db);
                await ClearMessages(db);

                var manager = NewManager();
                var commonEvent = await AddCommonEvent(db, "No Safety Flags");

                await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource { Id = "resource-plain", Name = "plain.docx", Type = "docx" },
                        },
                    },
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message> { new Message { Id = "response-1", IsPrompt = false } },
                    },
                }, commonEvent);

                await manager.CommitAllChanges();

                var resource = await db.CopilotEventAccessedResources.SingleAsync(r => r.ChatId == commonEvent.Id);
                Assert.IsNull(resource.XpiaDetected, "Absent XPIADetected must stay NULL, not become false.");

                var message = await db.CopilotMessages.SingleAsync(m => m.ChatId == commonEvent.Id);
                Assert.IsNull(message.JailbreakDetected, "Absent JailbreakDetected must stay NULL, not become false.");
            }
        }

        /// <summary>
        /// The de-duplication invariant the merge change had to preserve.
        ///
        /// xpia_detected is aggregated (MAX) over the resolved tuple rather than added to it, specifically
        /// so IX_copilot_event_accessed_resources_dedup did not need widening. That only holds if the same
        /// resource appearing twice in one interaction still collapses to one row. If a later change moved
        /// the flag into the de-dup identity this test fails, which is the intended alarm: that would be a
        /// performance-motivated index change needing its own before/after measurement.
        /// </summary>
        [TestMethod]
        public async Task Copilot_XpiaDetected_DoesNotSplitTheAccessedResourceDedupTuple()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (!await SafetyColumnsExist(db)) { Assert.Inconclusive("CopilotPromptSafetyFields migration not applied."); return; }

                await ClearEvents(db);
                await ClearAccessedResources(db);
                await ClearMessages(db);

                var manager = NewManager();
                var commonEvent = await AddCommonEvent(db, "Xpia Dedup");

                // The SAME resource twice in one interaction, flagged on one occurrence only.
                await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource { Id = "resource-dup", Name = "dup.docx", Type = "docx", Action = "Read", XPIADetected = false },
                            new AccessedResource { Id = "resource-dup", Name = "dup.docx", Type = "docx", Action = "Read", XPIADetected = true },
                        },
                    },
                }, commonEvent);

                await manager.CommitAllChanges();

                var resources = await db.CopilotEventAccessedResources
                    .Where(r => r.ChatId == commonEvent.Id).ToListAsync();

                Assert.AreEqual(1, resources.Count,
                    "The flag is payload, not identity - the same resource tuple must still collapse to one row.");
                Assert.AreEqual(true, resources.Single().XpiaDetected,
                    "Flagged on any occurrence means flagged: MAX, so a detection is never lost to de-duplication.");
            }
        }

        #endregion

        #region Manual upgrade script

        /// <summary>
        /// Migration convention rule 7: every schema migration ships a runnable by-hand upgrade script.
        /// This migration uses the EF DSL rather than raw SQL so there is no Up_Sql constant to compare
        /// against; what matters instead is that the script adds both columns, refuses to stamp out of
        /// order, and stamps its own id.
        /// </summary>
        [TestMethod]
        public void ManualScriptAddsBothColumnsAndStampsInOrder()
        {
            var manual = ReadManualScript("202609151440027_CopilotPromptSafetyFields");

            StringAssert.Contains(manual, "[xpia_detected] [bit] NULL",
                "The script must add the accessed-resource flag column.");
            StringAssert.Contains(manual, "[jailbreak_detected] [bit] NULL",
                "The script must add the message flag column.");

            StringAssert.Contains(manual, "202609131940001_CopilotReclaimEligibilityInputs",
                "The stamp must be conditional on the predecessor, so the scripts cannot be applied out of order.");

            StringAssert.Contains(manual, "INSERT INTO dbo.__MigrationHistory",
                "The script must stamp __MigrationHistory or EF will treat the migration as pending.");

            StringAssert.Contains(manual, "COL_LENGTH(N'dbo.copilot_event_accessed_resources', N'xpia_detected') IS NULL",
                "The pre-stamp guard must check schema. A guard on row data has broken a customer upgrade before.");
        }

        #endregion

        #region Helpers

        private CopilotAuditEventManager NewManager()
            => new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

        private static async Task<CommonAuditEvent> AddCommonEvent(AnalyticsEntitiesContext db, string label)
        {
            var commonEvent = new CommonAuditEvent
            {
                TimeStamp = DateTime.Now,
                Operation = new EventOperation { Name = label + DateTime.Now.Ticks },
                User = new User { AzureAdId = "test", UserPrincipalName = $"{label}@contoso.com{DateTime.Now.Ticks}" },
                Id = Guid.NewGuid(),
            };
            db.AuditEventsCommon.Add(commonEvent);
            await db.SaveChangesAsync();
            return commonEvent;
        }

        private static async Task<bool> SafetyColumnsExist(AnalyticsEntitiesContext db)
        {
            // CAST to int deliberately: COL_LENGTH returns smallint, and EF refuses to materialise an
            // Int16 into an int?. OBJECT_ID (used by the sibling test class) happens to return int, which
            // is why that helper needs no cast.
            var found = await db.Database
                .SqlQuery<int?>("SELECT CAST(COL_LENGTH('dbo.copilot_event_messages', 'jailbreak_detected') AS int)")
                .FirstOrDefaultAsync();
            return found.HasValue;
        }

        private static async Task ClearMessages(AnalyticsEntitiesContext db)
        {
            db.CopilotMessages.RemoveRange(db.CopilotMessages);
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Reads a <c>&lt;migrationid&gt;.manual.sql</c> from the repository. Walks up from the test
        /// binaries rather than assuming a working directory, so it works under both vstest and the IDE.
        /// </summary>
        private static string ReadManualScript(string migrationId)
        {
            var dir = new System.IO.DirectoryInfo(
                System.IO.Path.GetDirectoryName(typeof(CopilotPromptSafetyFieldsTests).Assembly.Location));

            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(
                    dir.FullName, "Common", "Entities", "Migrations", migrationId + ".manual.sql");
                if (System.IO.File.Exists(candidate))
                {
                    return System.IO.File.ReadAllText(candidate);
                }
                dir = dir.Parent;
            }

            Assert.Fail($"Could not find {migrationId}.manual.sql by walking up from the test assembly.");
            return null;
        }

        #endregion
    }
}
