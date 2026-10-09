using Common.Entities.Copilot;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// <see cref="CopilotAgentClassifier"/> (#639): every row of the mapping, the order the rows are tried in,
    /// and the read-time rule the Copilot Adoption inventory uses. Synthetic ids only: zeroed or repeated-digit
    /// GUIDs, "Contoso" names and the publisher prefix "cr123_".
    /// </summary>
    [TestClass]
    public class CopilotAgentClassifierTests
    {
        private const string TenantGuid = "00000000-0000-0000-0000-000000000000";
        private const string AgentGuid = "11111111-1111-1111-1111-111111111111";
        private const string TitleId = "T_22222222-2222-2222-2222-222222222222";
        private const string OtherEnvironment = "55555555-5555-5555-5555-555555555555";
        private const string DefaultEnvironmentAppIdentity = "Copilot.Studio.Default-" + TenantGuid + "-cr123_contosoHelpdesk";
        private const string OtherEnvironmentAppIdentity = "Copilot.Studio." + OtherEnvironment + "-cr123_contosoHelpdesk";
        private const string BizChatAppIdentity = "Copilot.Studio.CustomEngine." + TitleId;
        private const string CoworkAppIdentity = "Copilot.M365Copilot.CoworkChat";
        private const string SpoId = "SPO_ContosoExampleItemId_01";

        /// <summary>
        /// Import time: the id as the importer resolved it, the same id after normalisation, TargetAgentName,
        /// AgentPlatform and AppIdentity.
        /// </summary>
        [DataTestMethod]
        // Documented by Purview (https://learn.microsoft.com/en-us/purview/audit-copilot, AgentId).
        [DataRow("CopilotStudio.Declarative." + TenantGuid, "CopilotStudio.Declarative." + TenantGuid, null, null, null,
            CopilotAgentKind.Declarative, CopilotAgentOrigin.CustomerBuilt, DisplayName = "CopilotStudio.Declarative.*")]
        [DataRow("CopilotStudio.CustomEngine." + TenantGuid, "CopilotStudio.CustomEngine." + TenantGuid, null, null, null,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "CopilotStudio.CustomEngine.*")]
        [DataRow("copilotstudio.customengine." + TenantGuid, "copilotstudio.customengine." + TenantGuid, null, null, null,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Prefixes match whatever their case")]
        // The AppIdentity fallback, in the default environment and in any other; the importer keys the agent on it.
        [DataRow(DefaultEnvironmentAppIdentity, DefaultEnvironmentAppIdentity, null, null, DefaultEnvironmentAppIdentity,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "AppIdentity fallback, default environment")]
        [DataRow(OtherEnvironmentAppIdentity, OtherEnvironmentAppIdentity, null, null, OtherEnvironmentAppIdentity,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "AppIdentity fallback, non-default environment")]
        // Observed in #699: Microsoft 365 Copilot's record for a Copilot Studio agent, and the runtime's own record.
        [DataRow(TitleId + "." + AgentGuid, AgentGuid, "Contoso Helpdesk", null, BizChatAppIdentity,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "BizChat record")]
        [DataRow(AgentGuid, AgentGuid, null, "CopilotStudio", DefaultEnvironmentAppIdentity,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Runtime record")]
        [DataRow(AgentGuid, AgentGuid, null, "copilotstudio", null,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Runtime record, AgentPlatform alone")]
        [DataRow(AgentGuid, AgentGuid, null, null, OtherEnvironmentAppIdentity,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Runtime AppIdentity alone, non-default environment")]
        // TargetAgentName (the existing code comment: it names a custom-engine agent).
        [DataRow("contoso-helpdesk-agent", "contoso-helpdesk-agent", "Contoso Helpdesk", null, null,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "TargetAgentName on an unrecognised id")]
        [DataRow("Copilot.Studio.Default-contoso-helpdesk", "Copilot.Studio.Default-contoso-helpdesk", "Contoso Helpdesk", null, null,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "TargetAgentName on a Copilot Studio key")]
        [DataRow(null, null, "Contoso Helpdesk", null, null,
            CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt, DisplayName = "TargetAgentName with no id at all")]
        // SharePoint agents: declarative, but a user's agent and a site's ready-made one may share the prefix.
        [DataRow(SpoId, SpoId, null, null, null,
            CopilotAgentKind.Declarative, CopilotAgentOrigin.Unknown, DisplayName = "SPO_*")]
        [DataRow("SharePointAgents.Declarative." + SpoId, SpoId, null, null, null,
            CopilotAgentKind.Declarative, CopilotAgentOrigin.Unknown, DisplayName = "SharePointAgents.Declarative.SPO_*")]
        // Microsoft's own: the vetted first-party list.
        [DataRow(CoworkAppIdentity, CoworkAppIdentity, null, null, CoworkAppIdentity,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.Microsoft, DisplayName = "Copilot.M365Copilot.* (Cowork)")]
        [DataRow("Contoso.Unrecognised.Agent", "Contoso.Unrecognised.Agent", null, null, CoworkAppIdentity,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.Microsoft, DisplayName = "Vetted first-party AppIdentity, other id")]
        // Never guessed.
        [DataRow("BuiltIn_ContosoResearcher", "BuiltIn_ContosoResearcher", null, null, null,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.Unknown, DisplayName = "BuiltIn_*")]
        [DataRow(TitleId, TitleId, null, null, null,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.Unknown, DisplayName = "T_*")]
        [DataRow("P_22222222-2222-2222-2222-222222222222", "P_22222222-2222-2222-2222-222222222222", null, null, null,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.Unknown, DisplayName = "P_*")]
        [DataRow("ContosoDraft", "ContosoDraft", null, null, null,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.Unknown, DisplayName = "Short id")]
        [DataRow(AgentGuid, AgentGuid, null, null, null,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.Unknown, DisplayName = "Bare GUID with no other signal")]
        [DataRow(TitleId + "." + AgentGuid, AgentGuid, null, null, null,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.Unknown, DisplayName = "T_{titleId}.{agentId} with no other signal")]
        [DataRow("Contoso.Unrecognised.Agent", "Contoso.Unrecognised.Agent", null, null, null,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.Unknown, DisplayName = "Unrecognised dotted id")]
        [DataRow(null, null, null, null, null,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.Unknown, DisplayName = "Nothing at all")]
        // Refinements, documented by Purview: a Copilot Studio AppIdentity or agent id says who built the agent
        // even when its shape does not say what kind it is.
        [DataRow(AgentGuid, AgentGuid, null, null, "Copilot.Studio.Declarative." + TitleId,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Other Copilot.Studio.* AppIdentity")]
        [DataRow("Copilot.Studio.Default-contoso-helpdesk", "Copilot.Studio.Default-contoso-helpdesk", null, null, null,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Other Copilot.Studio.* id")]
        [DataRow("CopilotStudio.Template." + TenantGuid, "CopilotStudio.Template." + TenantGuid, null, null, null,
            CopilotAgentKind.Unknown, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Other CopilotStudio.* id")]
        public void Classify_AppliesTheMapping(
            string agentId,
            string normalisedAgentId,
            string targetAgentName,
            string agentPlatform,
            string appIdentity,
            CopilotAgentKind expectedKind,
            CopilotAgentOrigin expectedOrigin)
        {
            var result = CopilotAgentClassifier.Classify(agentId, normalisedAgentId, targetAgentName, agentPlatform, appIdentity);

            Assert.AreEqual(new CopilotAgentClassification(expectedKind, expectedOrigin), result);
        }

        /// <summary>
        /// A TargetAgentName does not make an agent customer-built when its id has a shape it has also been seen
        /// with on Microsoft's own agents (the importer's comment on TargetPlatformAgentId lists BuiltIn_*, T_*,
        /// P_* and short first-party ids), or when the id is a Microsoft first-party AppIdentity.
        /// </summary>
        [DataTestMethod]
        [DataRow("BuiltIn_ContosoResearcher")]
        [DataRow(TitleId)]
        [DataRow("P_22222222-2222-2222-2222-222222222222")]
        [DataRow("ContosoDraft")]
        [DataRow(AgentGuid)]
        [DataRow("Copilot.MicrosoftCopilot.Microsoft365Copilot")]
        public void Classify_TargetAgentNameDoesNotClaimAnAgentWithAFirstPartyShape(string agentId)
        {
            var result = CopilotAgentClassifier.Classify(agentId, agentId, "Contoso Proofreader", null, null);

            Assert.AreEqual(CopilotAgentClassification.Unclassified, result);
        }

        [TestMethod]
        public void Classify_DocumentedKindBeatsTargetAgentName()
        {
            var declarative = "CopilotStudio.Declarative." + TitleId + "." + AgentGuid;

            var result = CopilotAgentClassifier.Classify(declarative, declarative, "Contoso Proofreader", null, null);

            Assert.AreEqual(CopilotAgentKind.Declarative, result.Kind,
                "Purview documents the AgentId prefix; the TargetAgentName rule is only an inference.");
        }

        [TestMethod]
        public void Classify_FirstPartyAgentStaysMicrosoftsWhateverElseTheRecordCarries()
        {
            var result = CopilotAgentClassifier.Classify(CoworkAppIdentity, CoworkAppIdentity, "Copilot Cowork", null, CoworkAppIdentity);

            Assert.AreEqual(CopilotAgentOrigin.Microsoft, result.Origin);
            Assert.IsFalse(result.IsCustomerBuilt);
        }

        [TestMethod]
        public void Classify_SharePointAgentWithATargetAgentNameIsStillDeclarativeOfUnknownOrigin()
        {
            var result = CopilotAgentClassifier.Classify(SpoId, SpoId, "Contoso Proposals Agent", null, null);

            Assert.AreEqual(new CopilotAgentClassification(CopilotAgentKind.Declarative, CopilotAgentOrigin.Unknown), result);
        }

        [TestMethod]
        public void IsCustomAgentFlag_IsTrueForCustomerBuiltFalseForMicrosoftAndNullOtherwise()
        {
            Assert.AreEqual(true, new CopilotAgentClassification(CopilotAgentKind.Declarative, CopilotAgentOrigin.CustomerBuilt).IsCustomAgentFlag);
            Assert.AreEqual(false, new CopilotAgentClassification(CopilotAgentKind.Unknown, CopilotAgentOrigin.Microsoft).IsCustomAgentFlag);
            Assert.IsNull(new CopilotAgentClassification(CopilotAgentKind.Declarative, CopilotAgentOrigin.Unknown).IsCustomAgentFlag);
            Assert.IsNull(CopilotAgentClassification.Unclassified.IsCustomAgentFlag);
        }

        [TestMethod]
        public void OriginKeys_AreTheStableApiValues()
        {
            Assert.AreEqual("customerBuilt", CopilotAgentOriginKeys.For(CopilotAgentOrigin.CustomerBuilt));
            Assert.AreEqual("microsoft", CopilotAgentOriginKeys.For(CopilotAgentOrigin.Microsoft));
            Assert.AreEqual("unknown", CopilotAgentOriginKeys.For(CopilotAgentOrigin.Unknown));
        }

        #region Read time

        /// <summary>
        /// The Copilot Adoption inventory has only the stored agent_id and is_custom_agent. The id decides; a
        /// stored 1 settles an id the classifier cannot place; a stored 0 is never trusted.
        /// </summary>
        [DataTestMethod]
        [DataRow("CopilotStudio.Declarative." + TenantGuid, null, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Documented id, nothing stored (agents first seen since build 1552)")]
        [DataRow("CopilotStudio.CustomEngine." + TenantGuid, false, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Documented id beats a stored 0")]
        [DataRow(DefaultEnvironmentAppIdentity, null, CopilotAgentOrigin.CustomerBuilt, DisplayName = "AppIdentity-keyed agent")]
        [DataRow("Copilot.Studio.Default-contoso-helpdesk", null, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Other Copilot.Studio.* key")]
        [DataRow(CoworkAppIdentity, true, CopilotAgentOrigin.Microsoft, DisplayName = "Vetted first-party id beats a stored 1")]
        [DataRow(CoworkAppIdentity, null, CopilotAgentOrigin.Microsoft, DisplayName = "Vetted first-party id, nothing stored")]
        [DataRow(AgentGuid, true, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Bare GUID the importer flagged (a Copilot Studio runtime record)")]
        [DataRow(AgentGuid, false, CopilotAgentOrigin.Unknown, DisplayName = "A stored 0 is not trusted")]
        [DataRow(AgentGuid, null, CopilotAgentOrigin.Unknown, DisplayName = "Bare GUID, nothing stored")]
        [DataRow("Contoso.Agent.1", true, CopilotAgentOrigin.CustomerBuilt, DisplayName = "Unrecognised id the importer flagged")]
        [DataRow(SpoId, null, CopilotAgentOrigin.Unknown, DisplayName = "SharePoint agent")]
        [DataRow(null, null, CopilotAgentOrigin.Unknown, DisplayName = "No id")]
        public void ResolveStoredOrigin_PrefersTheIdAndTrustsOnlyAStoredOne(string agentId, bool? storedIsCustomAgent, CopilotAgentOrigin expected)
        {
            Assert.AreEqual(expected, CopilotAgentClassifier.ResolveStoredOrigin(agentId, storedIsCustomAgent));
            Assert.AreEqual(expected == CopilotAgentOrigin.CustomerBuilt, CopilotAgentClassifier.IsCustomerBuilt(agentId, storedIsCustomAgent));
        }

        #endregion
    }
}
