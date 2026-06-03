using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression coverage for the schema fields added to CopilotAuditLogContent in PR #95
    /// (ClientRegion, CopilotLogVersion, ThreadId, MessageIds, AISystemPlugin,
    /// Context.ContainerId, AccessedResource.ListItemUniqueId, AccessedResource.Action).
    /// These map directly to the official Copilot interaction audit schema documented at
    /// https://learn.microsoft.com/office/office-365-management-api/copilot-schema.
    /// </summary>
    [TestClass]
    public class CopilotAuditLogContentSchemaTests
    {
        [TestMethod]
        public void FromJson_ClientRegionAndCopilotLogVersion_ArePopulated()
        {
            var json = @"{
                ""ClientRegion"": ""WEU"",
                ""CopilotLogVersion"": ""2024-11-30"",
                ""AppIdentity"": ""Copilot.Office.Word"",
                ""CopilotEventData"": { ""AppHost"": ""Word"", ""AccessedResources"": [], ""Contexts"": [] }
            }";

            var result = CopilotAuditLogContent.FromJson(json);

            Assert.AreEqual("WEU", result.ClientRegion);
            Assert.AreEqual("2024-11-30", result.CopilotLogVersion);
        }

        [TestMethod]
        public void FromJson_ThreadIdAndMessageIds_ArePopulated()
        {
            var json = @"{
                ""AppIdentity"": ""Copilot.Office.Excel"",
                ""CopilotEventData"": {
                    ""AppHost"": ""Excel"",
                    ""ThreadId"": ""thread-1234"",
                    ""MessageIds"": [ ""msg-a"", ""msg-b"" ],
                    ""AccessedResources"": [],
                    ""Contexts"": []
                }
            }";

            var result = CopilotAuditLogContent.FromJson(json);

            Assert.IsNotNull(result.CopilotEventData);
            Assert.AreEqual("thread-1234", result.CopilotEventData.ThreadId);
            CollectionAssert.AreEqual(new[] { "msg-a", "msg-b" }, result.CopilotEventData.MessageIds);
        }

        [TestMethod]
        public void FromJson_AISystemPlugin_IsPopulated()
        {
            var json = @"{
                ""AppIdentity"": ""Copilot.Office.Word"",
                ""CopilotEventData"": {
                    ""AppHost"": ""Word"",
                    ""AISystemPlugin"": [ { ""Id"": ""BingWebSearch"", ""Name"": ""BuiltIn"" } ],
                    ""AccessedResources"": [],
                    ""Contexts"": []
                }
            }";

            var result = CopilotAuditLogContent.FromJson(json);

            Assert.IsNotNull(result.CopilotEventData?.AISystemPlugin);
            Assert.AreEqual(1, result.CopilotEventData.AISystemPlugin.Count);
            Assert.AreEqual("BingWebSearch", result.CopilotEventData.AISystemPlugin[0].Id);
            Assert.AreEqual("BuiltIn", result.CopilotEventData.AISystemPlugin[0].Name);
        }

        [TestMethod]
        public void FromJson_Context_ContainerId_IsPopulated()
        {
            var json = @"{
                ""AppIdentity"": ""Copilot.Teams"",
                ""CopilotEventData"": {
                    ""AppHost"": ""Teams"",
                    ""AccessedResources"": [],
                    ""Contexts"": [
                        { ""Id"": ""ctx-1"", ""Type"": ""TeamsChannel"", ""ContainerId"": ""team-9999"" }
                    ]
                }
            }";

            var result = CopilotAuditLogContent.FromJson(json);

            var ctx = result.CopilotEventData?.Contexts?.SingleOrDefault();
            Assert.IsNotNull(ctx);
            Assert.AreEqual("ctx-1", ctx.Id);
            Assert.AreEqual("TeamsChannel", ctx.Type);
            Assert.AreEqual("team-9999", ctx.ContainerId,
                "ContainerId is a new schema field and must round-trip from JSON.");
        }

        [TestMethod]
        public void FromJson_AccessedResource_ListItemUniqueIdAndAction_ArePopulated()
        {
            var json = @"{
                ""AppIdentity"": ""Copilot.SharePoint"",
                ""CopilotEventData"": {
                    ""AppHost"": ""SharePoint"",
                    ""Contexts"": [],
                    ""AccessedResources"": [
                        {
                            ""Id"": ""res-1"",
                            ""Name"": ""Doc.docx"",
                            ""Type"": ""docx"",
                            ""SiteUrl"": ""https://contoso.sharepoint.com/sites/x"",
                            ""listItemUniqueId"": ""00000000-0000-0000-0000-000000000123"",
                            ""Action"": ""Read""
                        }
                    ]
                }
            }";

            var result = CopilotAuditLogContent.FromJson(json);

            var res = result.CopilotEventData?.AccessedResources?.SingleOrDefault();
            Assert.IsNotNull(res);
            Assert.AreEqual("00000000-0000-0000-0000-000000000123", res.ListItemUniqueId,
                "ListItemUniqueId is mapped from the lower-camelCase 'listItemUniqueId' JSON property.");
            Assert.AreEqual("Read", res.Action);
        }

        [TestMethod]
        public void FromJson_MissingNewFields_DoNotThrowAndStayDefault()
        {
            // Older payloads that don't include any of the new fields must still parse cleanly.
            var json = @"{
                ""AppIdentity"": ""Copilot.Office.Word"",
                ""CopilotEventData"": {
                    ""AppHost"": ""Word"",
                    ""AccessedResources"": [],
                    ""Contexts"": []
                }
            }";

            var result = CopilotAuditLogContent.FromJson(json);

            Assert.IsNull(result.ClientRegion);
            Assert.IsNull(result.CopilotLogVersion);
            Assert.IsNull(result.CopilotEventData.ThreadId);
            Assert.IsNotNull(result.CopilotEventData.MessageIds, "MessageIds initialiser should produce an empty list, not null.");
            Assert.AreEqual(0, result.CopilotEventData.MessageIds.Count);
            Assert.IsNotNull(result.CopilotEventData.AISystemPlugin, "AISystemPlugin initialiser should produce an empty list, not null.");
            Assert.AreEqual(0, result.CopilotEventData.AISystemPlugin.Count);
        }
    }
}
