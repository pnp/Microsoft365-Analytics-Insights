using Common.Entities;
using Common.Entities.Entities.Email;
using Common.Entities.LookupCaches;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;

namespace Tests.UnitTests
{
    [TestClass]
    public class SentEmailImportTests
    {
        [TestMethod]
        public void SentimentScorer_StripHtml_RemovesTagsAndDecodesEntities()
        {
            Assert.AreEqual("Hello", AzureLanguageSentEmailSentimentScorer.StripHtml("<b>Hello</b>"));
            Assert.AreEqual("a & b", AzureLanguageSentEmailSentimentScorer.StripHtml("a &amp; b"));
            // Non-Latin content (e.g. Greek) must be preserved through stripping/decoding.
            Assert.AreEqual("Καλημέρα κόσμε", AzureLanguageSentEmailSentimentScorer.StripHtml("<p>Καλημέρα κόσμε</p>"));
        }

        [TestMethod]
        public void SentimentScorer_StripHtml_NullOrEmptyPassThrough()
        {
            Assert.IsNull(AzureLanguageSentEmailSentimentScorer.StripHtml(null));
            Assert.AreEqual(string.Empty, AzureLanguageSentEmailSentimentScorer.StripHtml(string.Empty));
        }

        #region ImportTaskSettings Tests

        // NOTE: All [ImportProp] flags default to false (opt-in) so a fresh / unconfigured install
        // does not start writing data to the database unexpectedly. Each flag must be explicitly
        // enabled via the settings string or a property setter.

        [TestMethod]
        public void ImportTaskSettings_AllProps_DefaultFalse()
        {
            // Every [ImportProp] flag defaults to false (opt-in model).
            var settings = new ImportTaskSettings();
            Assert.IsFalse(settings.Calls, "Calls should default to false");
            Assert.IsFalse(settings.GraphUsersMetadata, "GraphUsersMetadata should default to false");
            Assert.IsFalse(settings.GraphUserApps, "GraphUserApps should default to false");
            Assert.IsFalse(settings.GraphUsageReports, "GraphUsageReports should default to false");
            Assert.IsFalse(settings.GraphTeams, "GraphTeams should default to false");
            Assert.IsFalse(settings.ActivityLog, "ActivityLog should default to false");
            Assert.IsFalse(settings.WebTraffic, "WebTraffic should default to false");
            Assert.IsFalse(settings.SentEmails, "SentEmails should default to false");
        }

        [TestMethod]
        public void ImportTaskSettings_SentEmails_DefaultFalse()
        {
            // Kept as a focused regression test for the SentEmails default specifically.
            var settings = new ImportTaskSettings();
            Assert.IsFalse(settings.SentEmails, "SentEmails should default to false");
        }

        [TestMethod]
        public void ImportTaskSettings_ParseFromString_MissingTokensKeepFalseDefault()
        {
            // Props not mentioned in the string remain at their default (false).
            var settings = new ImportTaskSettings("GraphTeams=True");
            Assert.IsTrue(settings.GraphTeams, "GraphTeams should be enabled by explicit =True token");
            Assert.IsFalse(settings.SentEmails, "SentEmails should remain false when not mentioned");
            Assert.IsFalse(settings.Calls, "Calls should remain false when not mentioned");
            Assert.IsFalse(settings.WebTraffic, "WebTraffic should remain false when not mentioned");
        }

        [TestMethod]
        public void ImportTaskSettings_ParseFromString_ExplicitTrueEnablesEachProp()
        {
            // Parse honours =True for every [ImportProp] (opt-in model).
            var settings = new ImportTaskSettings(
                "Calls=True;GraphUsersMetadata=True;GraphUserApps=True;GraphUsageReports=True;" +
                "GraphTeams=True;ActivityLog=True;WebTraffic=True;SentEmails=True");

            Assert.IsTrue(settings.Calls);
            Assert.IsTrue(settings.GraphUsersMetadata);
            Assert.IsTrue(settings.GraphUserApps);
            Assert.IsTrue(settings.GraphUsageReports);
            Assert.IsTrue(settings.GraphTeams);
            Assert.IsTrue(settings.ActivityLog);
            Assert.IsTrue(settings.WebTraffic);
            Assert.IsTrue(settings.SentEmails);
        }

        [TestMethod]
        public void ImportTaskSettings_ParseFromString_ExplicitFalseKeepsFalse()
        {
            // Redundant =False tokens are harmless and leave the property at false.
            var settings = new ImportTaskSettings("Calls=False;GraphTeams=False");
            Assert.IsFalse(settings.Calls);
            Assert.IsFalse(settings.GraphTeams);
        }

        [TestMethod]
        public void ImportTaskSettings_ParseFromString_FalseTokenOverridesExplicitTrue()
        {
            // Property setter enables, then a =False token in the string disables it.
            // Validates that Parse() can flip true -> false.
            var settings = new ImportTaskSettings { Calls = true, GraphTeams = true };
            // Re-parse onto a fresh instance (this is the typical load path).
            var reparsed = new ImportTaskSettings("Calls=False;GraphTeams=True");
            Assert.IsFalse(reparsed.Calls, "Calls=False in the string must produce false");
            Assert.IsTrue(reparsed.GraphTeams, "GraphTeams=True in the string must produce true");
        }

        [TestMethod]
        public void ImportTaskSettings_ParseFromString_IsCaseInsensitive()
        {
            // Parse lowercases both the token and the property name before matching.
            var settings = new ImportTaskSettings("calls=TRUE;sentemails=true;graphteams=FALSE");
            Assert.IsTrue(settings.Calls, "Lowercase token name should still enable Calls");
            Assert.IsTrue(settings.SentEmails, "Lowercase token name should still enable SentEmails");
            Assert.IsFalse(settings.GraphTeams, "Lowercase token name should still keep GraphTeams false");
        }

        [TestMethod]
        public void ImportTaskSettings_ParseFromString_NullOrEmpty_KeepsDefaults()
        {
            var fromNull = new ImportTaskSettings(null);
            var fromEmpty = new ImportTaskSettings(string.Empty);
            var defaults = new ImportTaskSettings();

            Assert.IsTrue(fromNull.Equals(defaults), "Null settings string should produce defaults (all false)");
            Assert.IsTrue(fromEmpty.Equals(defaults), "Empty settings string should produce defaults (all false)");
            Assert.IsFalse(fromNull.HaveSomethingToDo(), "Null settings string should produce nothing to do");
        }

        [TestMethod]
        public void ImportTaskSettings_SentEmails_RoundTrip()
        {
            var settings = new ImportTaskSettings();
            settings.SentEmails = true;
            var str = settings.ToSettingsString();
            Assert.IsTrue(str.Contains("SentEmails=True"), "Settings string should contain SentEmails=True");

            // Parse honours both =True and =False, so round-tripping an explicitly enabled value should preserve it.
            var reloaded = new ImportTaskSettings(str);
            Assert.IsTrue(reloaded.SentEmails, "SentEmails should round-trip back to true when explicitly set");
        }

        [TestMethod]
        public void ImportTaskSettings_FullRoundTrip_AllPropsPreserved()
        {
            // Mixed values across all opt-in props should survive ToSettingsString -> ctor.
            var original = new ImportTaskSettings
            {
                Calls = false,
                GraphUsersMetadata = true,
                GraphUserApps = false,
                GraphUsageReports = true,
                GraphTeams = false,
                ActivityLog = true,
                WebTraffic = false,
                SentEmails = true,
            };

            var reloaded = new ImportTaskSettings(original.ToSettingsString());

            Assert.IsTrue(reloaded.Equals(original), "All [ImportProp] values should round-trip exactly");
            Assert.AreEqual(original.Calls, reloaded.Calls);
            Assert.AreEqual(original.GraphUsersMetadata, reloaded.GraphUsersMetadata);
            Assert.AreEqual(original.GraphUserApps, reloaded.GraphUserApps);
            Assert.AreEqual(original.GraphUsageReports, reloaded.GraphUsageReports);
            Assert.AreEqual(original.GraphTeams, reloaded.GraphTeams);
            Assert.AreEqual(original.ActivityLog, reloaded.ActivityLog);
            Assert.AreEqual(original.WebTraffic, reloaded.WebTraffic);
            Assert.AreEqual(original.SentEmails, reloaded.SentEmails);
        }

        [TestMethod]
        public void ImportTaskSettings_ToSettingsString_ContainsEveryImportProp()
        {
            var settingsString = new ImportTaskSettings().ToSettingsString();
            foreach (var propName in new[]
            {
                "Calls", "GraphUsersMetadata", "GraphUserApps", "GraphUsageReports",
                "GraphTeams", "ActivityLog", "WebTraffic", "SentEmails", "Copilot",
            })
            {
                Assert.IsTrue(settingsString.Contains(propName + "="),
                    $"Settings string should contain '{propName}=' but was: {settingsString}");
            }
        }

        [TestMethod]
        public void ImportTaskSettings_ToSettingsString_DefaultIsAllFalse()
        {
            // Documents the wire format produced for a fresh / unconfigured instance.
            var settingsString = new ImportTaskSettings().ToSettingsString();
            foreach (var propName in new[]
            {
                "Calls", "GraphUsersMetadata", "GraphUserApps", "GraphUsageReports",
                "GraphTeams", "ActivityLog", "WebTraffic", "SentEmails", "Copilot",
            })
            {
                Assert.IsTrue(settingsString.Contains(propName + "=False"),
                    $"Settings string should contain '{propName}=False' but was: {settingsString}");
            }
        }

        [TestMethod]
        public void ImportTaskSettings_SentEmails_InSettingsString()
        {
            var settings = new ImportTaskSettings();
            settings.SentEmails = true;
            var settingsString = settings.ToSettingsString();

            Assert.IsTrue(settingsString.Contains("SentEmails=True"),
                "SentEmails=True should be in settings string after enabling it");
        }

        [TestMethod]
        public void ImportTaskSettings_Equality_WithSentEmails()
        {
            var s1 = new ImportTaskSettings();
            var s2 = new ImportTaskSettings();
            Assert.IsTrue(s1.Equals(s2), "Default settings should be equal");

            s1.SentEmails = true;
            Assert.IsFalse(s1.Equals(s2), "Settings with different SentEmails should not be equal");

            s2.SentEmails = true;
            Assert.IsTrue(s1.Equals(s2), "Settings with same SentEmails should be equal");
        }

        [TestMethod]
        public void ImportTaskSettings_HaveSomethingToDo_DefaultsReturnFalse()
        {
            // The whole point of the opt-in model: a fresh install does nothing until enabled.
            Assert.IsFalse(new ImportTaskSettings().HaveSomethingToDo(),
                "HaveSomethingToDo should be false for a fresh default instance (opt-in model)");
        }

        [TestMethod]
        public void ImportTaskSettings_HaveSomethingToDo_AnySinglePropTrueReturnsTrue()
        {
            // Enabling any single opt-in flag is enough to have work to do.
            Assert.IsTrue(new ImportTaskSettings { Calls = true }.HaveSomethingToDo());
            Assert.IsTrue(new ImportTaskSettings { GraphUsersMetadata = true }.HaveSomethingToDo());
            Assert.IsTrue(new ImportTaskSettings { GraphUserApps = true }.HaveSomethingToDo());
            Assert.IsTrue(new ImportTaskSettings { GraphUsageReports = true }.HaveSomethingToDo());
            Assert.IsTrue(new ImportTaskSettings { GraphTeams = true }.HaveSomethingToDo());
            Assert.IsTrue(new ImportTaskSettings { ActivityLog = true }.HaveSomethingToDo());
            Assert.IsTrue(new ImportTaskSettings { WebTraffic = true }.HaveSomethingToDo());
            Assert.IsTrue(new ImportTaskSettings { SentEmails = true }.HaveSomethingToDo());
            Assert.IsTrue(new ImportTaskSettings { Copilot = true }.HaveSomethingToDo());
        }

        [TestMethod]
        public void ImportTaskSettings_ToActivityApiContentTypesString_MapsAuditSources()
        {
            // SharePoint audit only -> Audit.SharePoint
            Assert.AreEqual("Audit.SharePoint",
                new ImportTaskSettings { ActivityLog = true }.ToActivityApiContentTypesString());

            // Copilot only -> Audit.General
            Assert.AreEqual("Audit.General",
                new ImportTaskSettings { Copilot = true }.ToActivityApiContentTypesString());

            // Copilot + SharePoint -> Audit.General;Audit.SharePoint
            Assert.AreEqual("Audit.General;Audit.SharePoint",
                new ImportTaskSettings { Copilot = true, ActivityLog = true }.ToActivityApiContentTypesString());

            // Neither audit source -> safe non-empty default so the runtime workload list is valid
            Assert.AreEqual("Audit.SharePoint",
                new ImportTaskSettings().ToActivityApiContentTypesString());
        }

        #endregion

        #region SentEmailImporter Tests

        [TestMethod]
        public void StripHtml_RemovesTags()
        {
            var html = "<html><body><p>Hello <b>World</b></p></body></html>";
            var result = SentEmailImporter.StripHtml(html);
            Assert.IsFalse(result.Contains("<"), "Should not contain HTML tags");
            Assert.IsTrue(result.Contains("Hello"), "Should contain text content");
            Assert.IsTrue(result.Contains("World"), "Should contain text content");
        }

        [TestMethod]
        public void StripHtml_HandlesNull()
        {
            Assert.IsNull(SentEmailImporter.StripHtml(null));
        }

        [TestMethod]
        public void StripHtml_HandlesEmpty()
        {
            Assert.AreEqual(string.Empty, SentEmailImporter.StripHtml(string.Empty));
        }

        [TestMethod]
        public void StripHtml_DecodesEntities()
        {
            var html = "Hello &amp; World";
            var result = SentEmailImporter.StripHtml(html);
            Assert.IsTrue(result.Contains("Hello & World"), "Should decode HTML entities");
        }

        #endregion

        #region Delta Token Store Tests

        [TestMethod]
        public async Task InMemoryDeltaTokenStore_SetAndGet()
        {
            var store = new InMemoryDeltaTokenStore();
            await store.SetDeltaToken("key1", "token1");
            var result = await store.GetDeltaToken("key1");
            Assert.AreEqual("token1", result);
        }

        [TestMethod]
        public async Task InMemoryDeltaTokenStore_GetMissing_ReturnsNull()
        {
            var store = new InMemoryDeltaTokenStore();
            var result = await store.GetDeltaToken("nonexistent");
            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task InMemoryDeltaTokenStore_Overwrite()
        {
            var store = new InMemoryDeltaTokenStore();
            await store.SetDeltaToken("key1", "token1");
            await store.SetDeltaToken("key1", "token2");
            var result = await store.GetDeltaToken("key1");
            Assert.AreEqual("token2", result);
        }

        #endregion

        #region Graph DTO Tests

        [TestMethod]
        public void GraphSentMessage_Deserialization()
        {
            var json = @"{
                ""id"": ""msg123"",
                ""subject"": ""Test Subject"",
                ""sentDateTime"": ""2024-01-15T10:30:00Z"",
                ""from"": {
                    ""emailAddress"": {
                        ""name"": ""Sender"",
                        ""address"": ""sender@contoso.com""
                    }
                },
                ""toRecipients"": [
                    {
                        ""emailAddress"": {
                            ""name"": ""Recipient"",
                            ""address"": ""recipient@contoso.com""
                        }
                    }
                ],
                ""body"": {
                    ""contentType"": ""html"",
                    ""content"": ""<p>Hello</p>""
                }
            }";

            var msg = Newtonsoft.Json.JsonConvert.DeserializeObject<GraphSentMessage>(json);
            Assert.AreEqual("msg123", msg.Id);
            Assert.AreEqual("Test Subject", msg.Subject);
            Assert.IsNotNull(msg.SentDateTime);
            Assert.AreEqual("sender@contoso.com", msg.From.EmailAddress.Address);
            Assert.AreEqual(1, msg.ToRecipients.Count);
            Assert.AreEqual("recipient@contoso.com", msg.ToRecipients[0].EmailAddress.Address);
            Assert.AreEqual("<p>Hello</p>", msg.Body.Content);
        }

        [TestMethod]
        public void GraphSentMessage_MultipleRecipients()
        {
            var json = @"{
                ""id"": ""msg456"",
                ""subject"": ""Multi"",
                ""toRecipients"": [
                    { ""emailAddress"": { ""address"": ""a@test.com"" } },
                    { ""emailAddress"": { ""address"": ""b@test.com"" } }
                ]
            }";

            var msg = Newtonsoft.Json.JsonConvert.DeserializeObject<GraphSentMessage>(json);
            Assert.AreEqual(2, msg.ToRecipients.Count);
        }

        #endregion

        #region Entity Tests

        [TestMethod]
        public void SentEmail_Properties()
        {
            var sentEmail = new SentEmail
            {
                Subject = "Test",
                SentDate = new DateTime(2024, 1, 15),
                GraphMessageId = "msg123",
                CognitiveScore = 0.85,
                FromAddressID = 1,
                UserID = 3
            };

            sentEmail.Recipients.Add(new SentEmailRecipient { RecipientAddressID = 2 });
            sentEmail.Recipients.Add(new SentEmailRecipient { RecipientAddressID = 4 });

            Assert.AreEqual("Test", sentEmail.Subject);
            Assert.AreEqual(new DateTime(2024, 1, 15), sentEmail.SentDate);
            Assert.AreEqual("msg123", sentEmail.GraphMessageId);
            Assert.AreEqual(0.85, sentEmail.CognitiveScore);
            Assert.AreEqual(1, sentEmail.FromAddressID);
            Assert.AreEqual(3, sentEmail.UserID);
            Assert.AreEqual(2, sentEmail.Recipients.Count);
            CollectionAssert.AreEquivalent(
                new[] { 2, 4 },
                sentEmail.Recipients.Select(r => r.RecipientAddressID).ToArray());
        }

        [TestMethod]
        public void SentEmailRecipient_Properties()
        {
            var recipient = new SentEmailRecipient
            {
                SentEmailID = 10,
                RecipientAddressID = 20
            };

            Assert.AreEqual(10, recipient.SentEmailID);
            Assert.AreEqual(20, recipient.RecipientAddressID);
        }

        [TestMethod]
        public void EmailAddress_Properties()
        {
            var email = new EmailAddress
            {
                Address = "test@contoso.com"
            };
            Assert.AreEqual("test@contoso.com", email.Address);
            Assert.IsTrue(email.ToString().Contains("test@contoso.com"));
        }

        #endregion
    }
}
