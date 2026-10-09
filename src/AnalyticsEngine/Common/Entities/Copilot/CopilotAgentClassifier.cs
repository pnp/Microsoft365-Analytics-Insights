using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Common.Entities.Copilot
{
    /// <summary>
    /// What kind of agent handled a Copilot interaction. This is the billing question: the Copilot Credits
    /// estimate prices a custom-engine agent's responses and nothing else.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="CopilotAgentOrigin"/> on purpose. An Agent Builder agent is declarative and
    /// customer-built; Cowork is Microsoft's and not declarative. One flag cannot answer both questions, which
    /// is why <c>copilot_agents.is_custom_agent</c> stopped being set at all between Stable builds 1552 and
    /// the fix for #639.
    /// </remarks>
    public enum CopilotAgentKind
    {
        /// <summary>
        /// Not known. The default, so an agent this product does not recognise is visibly unclassified rather
        /// than quietly priced as one kind or the other.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// A declarative agent, which runs on Microsoft 365 Copilot's own orchestrator and models: for example
        /// a Copilot Studio declarative agent or a SharePoint agent.
        /// </summary>
        Declarative = 1,

        /// <summary>A custom-engine agent, which brings its own orchestration, as a Copilot Studio agent does.</summary>
        CustomEngine = 2,
    }

    /// <summary>
    /// Who made an agent. This is the inventory question: the Copilot Adoption Agents tab's Type column, its
    /// "customer-built only" filter and the <c>customAgents</c> count.
    /// </summary>
    public enum CopilotAgentOrigin
    {
        /// <summary>
        /// Not known. The default, and counted as such (<c>unknownOriginAgents</c>) rather than guessed, so the
        /// size of the gap is visible.
        /// </summary>
        Unknown = 0,

        /// <summary>Built by the customer's own people, in Copilot Studio for example.</summary>
        CustomerBuilt = 1,

        /// <summary>One Microsoft ships, such as Copilot Cowork.</summary>
        Microsoft = 2,
    }

    /// <summary>The two independent answers <see cref="CopilotAgentClassifier"/> gives for one agent.</summary>
    public struct CopilotAgentClassification : IEquatable<CopilotAgentClassification>
    {
        /// <summary>Neither answer is known. Also what <c>default(CopilotAgentClassification)</c> is.</summary>
        public static readonly CopilotAgentClassification Unclassified = default(CopilotAgentClassification);

        public CopilotAgentClassification(CopilotAgentKind kind, CopilotAgentOrigin origin)
        {
            Kind = kind;
            Origin = origin;
        }

        public CopilotAgentKind Kind { get; }

        public CopilotAgentOrigin Origin { get; }

        /// <summary>True when the agent is customer-built.</summary>
        public bool IsCustomerBuilt => Origin == CopilotAgentOrigin.CustomerBuilt;

        /// <summary>
        /// What <c>copilot_agents.is_custom_agent</c> stores for this agent: true when it is customer-built, false
        /// when it is Microsoft's, and null when its origin is not known. The agents upsert keeps a stored value
        /// when a batch carries NULL, so an unrecognised record never overwrites what an earlier one established.
        /// </summary>
        public bool? IsCustomAgentFlag
        {
            get
            {
                switch (Origin)
                {
                    case CopilotAgentOrigin.CustomerBuilt: return true;
                    case CopilotAgentOrigin.Microsoft: return false;
                    default: return null;
                }
            }
        }

        public bool Equals(CopilotAgentClassification other) => Kind == other.Kind && Origin == other.Origin;

        public override bool Equals(object obj) => obj is CopilotAgentClassification other && Equals(other);

        public override int GetHashCode() => ((int)Kind * 397) ^ (int)Origin;

        public static bool operator ==(CopilotAgentClassification left, CopilotAgentClassification right) => left.Equals(right);

        public static bool operator !=(CopilotAgentClassification left, CopilotAgentClassification right) => !left.Equals(right);

        public override string ToString() => Kind + "/" + Origin;
    }

    /// <summary>
    /// The stable keys the Copilot Adoption API sends for <see cref="CopilotAgentOrigin"/> (<c>origin</c> on an
    /// agent row). The portal maps each to translated text, so these are a contract with the SPA and with any
    /// script that reads the API: never rename one.
    /// </summary>
    public static class CopilotAgentOriginKeys
    {
        public const string CustomerBuilt = "customerBuilt";

        public const string Microsoft = "microsoft";

        public const string Unknown = "unknown";

        public static string For(CopilotAgentOrigin origin)
        {
            switch (origin)
            {
                case CopilotAgentOrigin.CustomerBuilt: return CustomerBuilt;
                case CopilotAgentOrigin.Microsoft: return Microsoft;
                default: return Unknown;
            }
        }
    }

    /// <summary>
    /// Says what kind of agent a Copilot audit record names (<see cref="CopilotAgentKind"/>) and who made it
    /// (<see cref="CopilotAgentOrigin"/>), from the shape of its identifiers. Issue #639.
    /// </summary>
    /// <remarks>
    /// <para>Pure and dependency-free, like <c>CopilotStudioHarnessClassifier</c>, so every rule can be unit tested
    /// and so the importer (which sets <c>copilot_agents.is_custom_agent</c> and chooses whether to estimate
    /// credits) and the Copilot Adoption analysis (which labels the inventory) cannot drift apart.</para>
    ///
    /// <para><b>It never guesses.</b> Anything it does not recognise is <see cref="CopilotAgentKind.Unknown"/>
    /// and <see cref="CopilotAgentOrigin.Unknown"/>, so a new shape from Microsoft shows up as an unknown count
    /// rather than as agents quietly filed under the wrong heading. The rules, first match wins:</para>
    /// <list type="number">
    /// <item><c>CopilotStudio.Declarative.*</c> agent id: declarative, customer-built. Documented by Purview.</item>
    /// <item><c>CopilotStudio.CustomEngine.*</c> agent id: custom-engine, customer-built. Documented by Purview.</item>
    /// <item>An agent id or AppIdentity on the vetted first-party list (<c>Copilot.M365Copilot.*</c>, e.g.
    /// Cowork): kind unknown, Microsoft's.</item>
    /// <item><c>SPO_*</c> or <c>SharePointAgents.Declarative.SPO_*</c>: declarative, origin unknown. A user's
    /// SharePoint agent and a site's ready-made agent may share the prefix, and no payload has been checked.</item>
    /// <item><c>AgentPlatform = "CopilotStudio"</c> (Copilot Studio's runtime record): custom-engine,
    /// customer-built. Observed in #699.</item>
    /// <item>A Copilot Studio runtime AppIdentity, <c>Copilot.Studio.{environment}-{schema}</c>, in the default
    /// environment or any other: custom-engine, customer-built. Also matched as an agent id, because the
    /// importer keys an agent on its AppIdentity when the record carries no id.</item>
    /// <item>The AppIdentity Microsoft 365 Copilot logs for a Copilot Studio agent,
    /// <c>Copilot.Studio.CustomEngine.T_*</c>: custom-engine, customer-built. Observed in #699.</item>
    /// <item>A <c>TargetAgentName</c>: custom-engine, customer-built - unless the agent id has one of the shapes
    /// below that it has also been seen with on Microsoft's own agents.</item>
    /// <item>Any other <c>Copilot.Studio.*</c> AppIdentity or id: customer-built, kind unknown. Purview documents
    /// <c>Copilot.Studio.*</c> as the AppIdentity of "custom-built Copilots created through Copilot Studio".</item>
    /// <item>Any other <c>CopilotStudio.*</c> agent id: customer-built, kind unknown. Purview documents the prefix
    /// for agents "created through Microsoft Copilot Studio".</item>
    /// <item>Anything else, including <c>BuiltIn_*</c>, <c>T_*</c>, <c>P_*</c>, a single word such as
    /// <c>OutlookDraft</c>, a bare GUID, or another first-party <c>Copilot.*</c> AppIdentity: unknown.</item>
    /// </list>
    /// <para>Rule 8 does not apply to <c>BuiltIn_*</c>, <c>T_*</c>, <c>P_*</c>, single-word ids, bare GUIDs or
    /// first-party <c>Copilot.*</c> AppIdentities, because the importer has seen <c>TargetAgentName</c> on
    /// records whose target id is <c>BuiltIn_*</c> or a short first-party id (see
    /// <c>CopilotEventData.TargetPlatformAgentId</c>). Those are Microsoft's agents, and calling them
    /// customer-built would be a guess.</para>
    /// <para>References: https://learn.microsoft.com/en-us/purview/audit-copilot (AgentId and AppIdentity),
    /// issue #699 (the Copilot Studio runtime and Microsoft 365 Copilot record shapes).</para>
    /// </remarks>
    public static class CopilotAgentClassifier
    {
        /// <summary>The AgentPlatform Copilot Studio's runtime writes on its own records (#699).</summary>
        public const string CopilotStudioAgentPlatform = "CopilotStudio";

        private const string DeclarativeCopilotStudioPrefix = "CopilotStudio.Declarative.";
        private const string CustomEngineCopilotStudioPrefix = "CopilotStudio.CustomEngine.";
        private const string CopilotStudioAgentIdPrefix = "CopilotStudio.";
        private const string CopilotStudioAppIdentityPrefix = "Copilot.Studio.";
        private const string CopilotStudioCustomEngineAppIdentityPrefix = "Copilot.Studio.CustomEngine.T_";
        private const string FirstPartyAppIdentityPrefix = "Copilot.";
        private const string SharePointAgentIdPrefix = "SPO_";
        private const string SharePointDeclarativeAgentIdPrefix = "SharePointAgents.Declarative.SPO_";

        private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

        /// <summary>
        /// The AppIdentity of Copilot Studio's runtime records: "Copilot.Studio.{environmentId}-{schemaName}".
        /// The environment id is "Default-{tenantId}" for the default environment and a bare GUID for any other.
        /// The schema name starts with the solution publisher's prefix, which varies (e.g. "new_" or "cr123_").
        /// </summary>
        private static readonly Regex CopilotStudioRuntimeAppIdentityPattern = new Regex(
            "^Copilot\\.Studio\\.(?:Default-)?" + GuidPattern + "-(?<schemaName>.+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>A single word with no separator at all, such as "OutlookDraft".</summary>
        private static readonly Regex SingleWordPattern = new Regex(
            "^[A-Za-z0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string[] UnattributablePrefixes = { "BuiltIn_", "T_", "P_" };

        /// <summary>
        /// First-party AppIdentity prefixes known to identify one agent of Microsoft's own, rather than an app
        /// that hosts many. Only these are promoted to an agent id when a named agent arrives without one, and
        /// they are what makes an agent <see cref="CopilotAgentOrigin.Microsoft"/>. Keep the list conservative:
        /// add a prefix only after confirming from real payloads that it is a stable per-agent key.
        /// </summary>
        public static IReadOnlyList<string> VettedFirstPartyAgentPrefixes { get; } = Array.AsReadOnly(new[]
        {
            "Copilot.M365Copilot.",   // e.g. "Copilot.M365Copilot.CoworkChat" (Copilot Cowork)
        });

        /// <summary>True when <paramref name="value"/> starts with one of <see cref="VettedFirstPartyAgentPrefixes"/>.</summary>
        public static bool IsVettedFirstPartyAgentIdentity(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (var prefix in VettedFirstPartyAgentPrefixes)
            {
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the agent's schema name from a Copilot Studio runtime AppIdentity,
        /// "Copilot.Studio.{environmentId}-{schemaName}". False for any other shape, including the
        /// "Copilot.Studio.CustomEngine.T_{titleId}" AppIdentity on Microsoft 365 Copilot's records.
        /// </summary>
        public static bool TryGetCopilotStudioSchemaName(string appIdentity, out string schemaName)
        {
            schemaName = null;
            if (string.IsNullOrEmpty(appIdentity))
            {
                return false;
            }

            var match = CopilotStudioRuntimeAppIdentityPattern.Match(appIdentity);
            if (!match.Success)
            {
                return false;
            }

            schemaName = match.Groups["schemaName"].Value;
            return true;
        }

        /// <summary>
        /// Classifies the agent an audit record names, at import time, when the whole record is to hand.
        /// Never throws.
        /// </summary>
        /// <param name="agentId">The agent id the importer resolved, before <c>NormalizeAgentId</c>.</param>
        /// <param name="normalisedAgentId">The same id after normalisation: the key <c>copilot_agents</c> stores.</param>
        /// <param name="targetAgentName"><c>CopilotEventData.TargetAgentName</c>.</param>
        /// <param name="agentPlatform">The record's top-level <c>AgentPlatform</c>.</param>
        /// <param name="appIdentity">The record's top-level <c>AppIdentity</c>.</param>
        public static CopilotAgentClassification Classify(
            string agentId,
            string normalisedAgentId,
            string targetAgentName,
            string agentPlatform,
            string appIdentity)
        {
            var ids = new[] { Clean(agentId), Clean(normalisedAgentId) };
            var app = Clean(appIdentity);

            if (AnyStartsWith(ids, DeclarativeCopilotStudioPrefix))
            {
                return Of(CopilotAgentKind.Declarative, CopilotAgentOrigin.CustomerBuilt);
            }

            if (AnyStartsWith(ids, CustomEngineCopilotStudioPrefix))
            {
                return Of(CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt);
            }

            if (IsVettedFirstPartyAgentIdentity(ids[0]) || IsVettedFirstPartyAgentIdentity(ids[1]) || IsVettedFirstPartyAgentIdentity(app))
            {
                return Of(CopilotAgentKind.Unknown, CopilotAgentOrigin.Microsoft);
            }

            if (AnyStartsWith(ids, SharePointAgentIdPrefix) || AnyStartsWith(ids, SharePointDeclarativeAgentIdPrefix))
            {
                return Of(CopilotAgentKind.Declarative, CopilotAgentOrigin.Unknown);
            }

            if (string.Equals(Clean(agentPlatform), CopilotStudioAgentPlatform, StringComparison.OrdinalIgnoreCase))
            {
                return Of(CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt);
            }

            if (IsCopilotStudioRuntimeAppIdentity(app) || IsCopilotStudioRuntimeAppIdentity(ids[0]) || IsCopilotStudioRuntimeAppIdentity(ids[1]))
            {
                return Of(CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt);
            }

            if (StartsWith(app, CopilotStudioCustomEngineAppIdentityPrefix) || AnyStartsWith(ids, CopilotStudioCustomEngineAppIdentityPrefix))
            {
                return Of(CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt);
            }

            if (Clean(targetAgentName) != null && !IsUnattributable(ids[0]) && !IsUnattributable(ids[1]))
            {
                return Of(CopilotAgentKind.CustomEngine, CopilotAgentOrigin.CustomerBuilt);
            }

            if (StartsWith(app, CopilotStudioAppIdentityPrefix) || AnyStartsWith(ids, CopilotStudioAppIdentityPrefix)
                || AnyStartsWith(ids, CopilotStudioAgentIdPrefix))
            {
                return Of(CopilotAgentKind.Unknown, CopilotAgentOrigin.CustomerBuilt);
            }

            return CopilotAgentClassification.Unclassified;
        }

        /// <summary>
        /// Classifies a stored <c>copilot_agents.agent_id</c>, the only input there is at read time. An id
        /// that was a record's AppIdentity (the importer keys an agent on it when the record carries no id) is
        /// recognised as such.
        /// </summary>
        public static CopilotAgentClassification ClassifyStoredAgentId(string agentId)
        {
            return Classify(agentId, agentId, null, null, null);
        }

        /// <summary>
        /// The origin to report for a stored agent: <see cref="ClassifyStoredAgentId"/>, except that an id it
        /// cannot place counts as customer-built when the importer stored <c>is_custom_agent = 1</c>, because the
        /// importer saw the whole record and this sees only the id. A stored 0 is ignored: builds before the fix
        /// for #639 wrote it inconsistently, so it is not evidence that Microsoft made the agent.
        /// </summary>
        public static CopilotAgentOrigin ResolveStoredOrigin(string agentId, bool? storedIsCustomAgent)
        {
            var origin = ClassifyStoredAgentId(agentId).Origin;
            return origin == CopilotAgentOrigin.Unknown && storedIsCustomAgent == true
                ? CopilotAgentOrigin.CustomerBuilt
                : origin;
        }

        /// <summary>
        /// "Is this agent customer-built?" for a stored agent - <see cref="ResolveStoredOrigin"/> as a yes or no,
        /// for analysis that reads <c>copilot_agents</c> itself.
        /// </summary>
        public static bool IsCustomerBuilt(string agentId, bool? storedIsCustomAgent)
        {
            return ResolveStoredOrigin(agentId, storedIsCustomAgent) == CopilotAgentOrigin.CustomerBuilt;
        }

        private static CopilotAgentClassification Of(CopilotAgentKind kind, CopilotAgentOrigin origin)
        {
            return new CopilotAgentClassification(kind, origin);
        }

        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool StartsWith(string value, string prefix)
        {
            return value != null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool AnyStartsWith(string[] values, string prefix)
        {
            foreach (var value in values)
            {
                if (StartsWith(value, prefix))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsCopilotStudioRuntimeAppIdentity(string value)
        {
            return value != null && CopilotStudioRuntimeAppIdentityPattern.IsMatch(value);
        }

        /// <summary>
        /// Id shapes that a <c>TargetAgentName</c> does not make customer-built: <c>BuiltIn_*</c>, <c>T_*</c>,
        /// <c>P_*</c>, a single word, a bare GUID, or a first-party <c>Copilot.*</c> AppIdentity other than a
        /// Copilot Studio one. See the remarks on <see cref="CopilotAgentClassifier"/>.
        /// </summary>
        private static bool IsUnattributable(string id)
        {
            if (id == null)
            {
                return false;
            }

            foreach (var prefix in UnattributablePrefixes)
            {
                if (StartsWith(id, prefix))
                {
                    return true;
                }
            }

            if (StartsWith(id, FirstPartyAppIdentityPrefix) && !StartsWith(id, CopilotStudioAppIdentityPrefix))
            {
                return true;
            }

            return SingleWordPattern.IsMatch(id) || Guid.TryParse(id, out _);
        }
    }
}
