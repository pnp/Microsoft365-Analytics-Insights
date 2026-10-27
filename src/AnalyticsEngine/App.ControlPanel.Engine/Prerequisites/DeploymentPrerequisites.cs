using Common.Entities;
using Common.Entities.CopilotCosts;
using Common.Entities.Installer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace App.ControlPanel.Engine.Prerequisites
{
    public enum PrerequisiteCategory
    {
        ApplicationPermissions,
        DelegatedConnections,
        AzureResources,
        RuntimeAccess,
        DeploymentAccess,
        OperationalChecks,
        OptionalEnrichment
    }

    public sealed class DeploymentRequirement
    {
        public string Id { get; internal set; }
        public PrerequisiteCategory Category { get; internal set; }
        public string Name { get; internal set; }
        public string Identity { get; internal set; }
        public string Scope { get; internal set; }
        public string Purpose { get; internal set; }
        public string AdminAction { get; internal set; }
        public string Responsibility { get; internal set; }
        public string Reference { get; internal set; }
        public IReadOnlyList<string> Workloads => _workloads;
        private readonly List<string> _workloads = new List<string>();
        private readonly Dictionary<string, string> _workloadPurposes = new Dictionary<string, string>();

        internal void AddWorkload(string name, string purpose)
        {
            if (!_workloads.Contains(name)) _workloads.Add(name);
            if (!_workloadPurposes.TryGetValue(name, out var existing)) _workloadPurposes.Add(name, purpose);
            else if (!existing.Contains(purpose)) _workloadPurposes[name] = existing + " " + purpose;
        }

        public string PurposeFor(string workload) => _workloadPurposes.TryGetValue(workload, out var purpose) ? purpose : Purpose;

        public string Details => DetailsFor(null);

        internal string DetailsFor(string workload) => Name + "\r\n\r\nCategory: " + DeploymentPrerequisiteReport.CategoryTitle(Category)
            + "\r\nIdentity / permission type: " + Identity
            + "\r\nScope / audience: " + Scope + "\r\nNeeded for: " + (workload ?? string.Join(", ", Workloads))
            + "\r\n\r\nWhy: " + (workload == null ? Purpose : PurposeFor(workload)) + "\r\n\r\nWho sets this up: " + Responsibility
            + "\r\n\r\nSetup notes and checks: " + AdminAction;
    }

    public sealed class WorkloadPermissionRow
    {
        public string Workload { get; internal set; }
        public DeploymentRequirement Requirement { get; internal set; }
        public bool NoApplicationGrant { get; internal set; }
        public string Permission => NoApplicationGrant ? "None (Graph / Activity API app-only)" : Requirement.Name;
        public string ApiAndType => NoApplicationGrant ? "No Graph / Activity API application grant" : Requirement.Identity;
        public string Why => Requirement.PurposeFor(Workload);
        public string Need => NoApplicationGrant ? "No app-only grant" :
            Requirement.Category == PrerequisiteCategory.OptionalEnrichment ? "Optional extra" :
            Requirement.Category == PrerequisiteCategory.DelegatedConnections ? "Delegated connection" : "Required application permission";
        public string Details => NoApplicationGrant
            ? Workload + "\r\n\r\n" + Permission + "\r\nWhy: " + Why + "\r\n\r\nWorkload setup:\r\n" + Requirement.DetailsFor(Workload)
            : Requirement.DetailsFor(Workload);
    }

    public sealed class OptionalAzureComponent
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public bool CanRemoveWithoutChangingImports { get; internal set; }
        public string FunctionalityLost { get; internal set; }
        public string HowToRemove { get; internal set; }
        public string Status => CanRemoveWithoutChangingImports ? "Optional for base imports" : "Required while Teams calls import is selected";
        public string Details => Name + "\r\n\r\nCurrently selected in the installer.\r\n" + Status
            + "\r\n\r\nFunctionality lost: " + FunctionalityLost + "\r\n\r\nHow to leave it out: " + HowToRemove
            + "\r\n\r\n" + DeploymentPrerequisiteReport.RemovalNotice;
    }

    public sealed class DeploymentPrerequisiteReport
    {
        public IReadOnlyList<string> Workloads { get; internal set; }
        public IReadOnlyList<DeploymentRequirement> Requirements { get; internal set; }
        public IReadOnlyList<string> ConfigurationChoices { get; internal set; }
        public IReadOnlyList<WorkloadPermissionRow> WorkloadPermissions { get; internal set; } = Array.Empty<WorkloadPermissionRow>();
        public IReadOnlyList<OptionalAzureComponent> OptionalComponents { get; internal set; } = Array.Empty<OptionalAzureComponent>();

        public const string ScopeNotice = "Planning guidance, not an access check. Based on the current workload and infrastructure selections. "
            + "No Azure or tenant discovery is performed. Each item distinguishes installer-managed setup from approvals and checks.";
        public const string PrivacyNotice = "Shareable by design: entered names, IDs, addresses, paths and credentials are excluded. "
            + "Administrators must identify the target tenant, apps, resources and scopes separately.";
        public const string RemovalNotice = "This is a read-only review, not an automatic change. Turning provisioning off does not delete existing Azure resources, revoke existing grants or erase imported data. For an existing deployment, update the runtime configuration and have the resource owner retire unused services separately after checking dependencies.";

        public static string CategoryTitle(PrerequisiteCategory category)
        {
            switch (category)
            {
                case PrerequisiteCategory.ApplicationPermissions: return "Runtime application permissions";
                case PrerequisiteCategory.DelegatedConnections: return "Delegated connections";
                case PrerequisiteCategory.AzureResources: return "Azure resources";
                case PrerequisiteCategory.RuntimeAccess: return "Runtime RBAC & database access";
                case PrerequisiteCategory.DeploymentAccess: return "One-time deployment access";
                case PrerequisiteCategory.OperationalChecks: return "Licensing & setup checks";
                default: return "Optional Copilot file, meeting and history features";
            }
        }

        public static string CategoryDescription(PrerequisiteCategory category)
        {
            switch (category)
            {
                case PrerequisiteCategory.ApplicationPermissions:
                    return "Permissions for the runtime app to read the selected Microsoft 365 data without a signed-in user. An Entra consent administrator grants these; the installer does not grant API consent.";
                case PrerequisiteCategory.DelegatedConnections:
                    return "Features that read on behalf of a connected person. An Entra administrator approves delegated consent, then a portal operator connects the reader after deployment. Application permissions alone cannot replace that connection.";
                case PrerequisiteCategory.AzureResources:
                    return "The installer creates or reuses these services during deployment. This is not a request to create every resource by hand. The Azure owner reviews cost, policy and capacity first; exceptions such as an existing Hybrid Worker VM are called out.";
                case PrerequisiteCategory.RuntimeAccess:
                    return "The access the running solution needs after deployment. The installer configures the solution's Azure and SQL grants; external billing scopes need their own owner. These are separate from Microsoft 365 API consent.";
                case PrerequisiteCategory.DeploymentAccess:
                    return "Access used to install or upgrade, not standing runtime privileges. The deployment identity must be authorised before the run; vault/database permissions that the installer can configure are identified separately.";
                case PrerequisiteCategory.OperationalChecks:
                    return "Checks for the named service, network or subscription owner. They do not all require a configuration change: if the stated condition is already met, no action is needed. This offline document has not tested those conditions.";
                default:
                    return "Only relevant to the Microsoft 365 Copilot audit workload: extra Graph access can add readable file/site and meeting context to audit events, or recover older audit history. These are not required for raw audit ingestion. They call graph.microsoft.com over HTTPS (TCP 443). Viewing this category enables nothing; historical recovery also needs runtime configuration. Azure AI Language/OpenAI options are listed separately under Azure resources.";
            }
        }

        public string ToMarkdown()
        {
            var text = new StringBuilder("# Microsoft 365 Analytics Insights - deployment prerequisites\n\n");
            text.AppendLine("[Runtime API permissions by workload](#runtime-api-permissions-by-selected-workload) | [Selected optional Azure components](#selected-azure-components-you-could-leave-out)").AppendLine();
            text.AppendLine("## What this document is for").AppendLine();
            text.AppendLine("Microsoft 365 Analytics Insights collects selected Microsoft 365 activity and usage data into an Azure SQL database and presents it in a web portal. A Windows App Service hosts that portal and two background import jobs; Azure Storage keeps checkpoints and connection state, and Application Insights records diagnostics. Optional services are added only when selected.").AppendLine();
            text.AppendLine("Use this document to agree the deployment with the relevant Entra, Azure, network, Exchange, SharePoint and billing owners. It describes the requested end state and who supplies it. It is not a script, an instruction for the recipient to provision everything manually, or evidence that access is already working.").AppendLine();
            text.AppendLine(ScopeNotice).AppendLine().AppendLine(PrivacyNotice).AppendLine();
            text.AppendLine("## How setup is divided").AppendLine();
            text.AppendLine("| Stage | Who | What happens |");
            text.AppendLine("| --- | --- | --- |");
            text.AppendLine("| Before deployment | Tenant / subscription owners | Approve the selected data access, prepare the two app registrations and API consent, authorise the deployment identity, and confirm the network/provider/policy/capacity checks below. Existing compliant settings need no change. |");
            text.AppendLine("| During deployment | Installer operator running the installer | The installer creates or reuses the Azure services, configures supported runtime roles and database access, applies selected schema/content tasks and can deploy the SharePoint tracker. Manual resource creation is not normally required. |");
            text.AppendLine("| After deployment | Portal operator and any required reader | Connect selected Teams or billing readers, set workload-specific runtime options, and validate the first import. The installer does not sign a delegated reader in for you. |").AppendLine();
            text.AppendLine("## Identities and permission types").AppendLine();
            text.AppendLine("| Identity / term | Meaning |");
            text.AppendLine("| --- | --- |");
            text.AppendLine("| Deployment app | Separate Entra application used by the installer to create/configure Azure resources and perform upgrades. Its management privileges are not required by the runtime app. |");
            text.AppendLine("| Runtime app | Entra application used by the portal/importers to read Microsoft 365 APIs and obtain service-principal tokens. Application API permissions need tenant admin consent; Azure RBAC is a different grant. |");
            text.AppendLine("| Managed identity | Azure identity attached to App Service or Automation. The installer uses it for the documented Key Vault/SQL access; it does not replace the runtime app's Microsoft 365 permissions. |");
            text.AppendLine("| Delegated reader | A signed-in person whose source-data rights are used by Teams/billing connections. App consent does not grant that person a new directory or billing role. |");
            text.AppendLine("| Azure RBAC / SQL roles | Azure roles govern resource management or data-plane access. SQL contained-user roles govern database access. Neither is a Microsoft Graph permission. |").AppendLine();
            text.AppendLine("## Selected deployment options").AppendLine();
            foreach (var choice in ConfigurationChoices) text.Append("- ").AppendLine(choice);
            text.AppendLine().AppendLine("Host patterns below describe this build's Azure/Microsoft 365 public-cloud application traffic. Replace brace placeholders with the agreed resource names privately; they are not literal firewall destinations. Keep the underlying App Service/Hybrid Worker platform dependencies allowed according to Microsoft's network guidance as well.");
            text.AppendLine();
            text.AppendLine("## Selected workloads").AppendLine();
            if (Workloads.Count == 0) text.AppendLine("No workloads selected. Shared infrastructure below is still part of this installer's deployment.");
            foreach (var workload in Workloads) text.Append("- ").AppendLine(workload);
            text.AppendLine().AppendLine("## Selected Azure components you could leave out").AppendLine();
            text.AppendLine("These services can add cost and access requirements even when their extra functionality is not wanted. Only components currently selected in the installer are listed. Service Bus is removable only if Teams calls import is not wanted.").AppendLine();
            if (OptionalComponents.Count == 0)
                text.AppendLine("Text Analytics, Service Bus and Azure OpenAI are not selected; there are no selected components from this optional set to review.");
            else
            {
                text.AppendLine("| Selected component | Can it be left out? | Functionality lost | What to change |");
                text.AppendLine("| --- | --- | --- | --- |");
                foreach (var component in OptionalComponents)
                    text.Append("| ").Append(Escape(component.Name)).Append(" | ").Append(Escape(component.Status))
                        .Append(" | ").Append(Escape(component.FunctionalityLost)).Append(" | ")
                        .Append(Escape(component.HowToRemove)).AppendLine(" |");
            }
            text.AppendLine().AppendLine(RemovalNotice).AppendLine();
            text.AppendLine("## Runtime API permissions by selected workload").AppendLine();
            text.AppendLine("This is the runtime app registration's permission list, not the deployment account's Azure roles. Each selected import is listed with the API, permission type and its own reason. Application permissions require admin consent; delegated rows additionally need the connected reader described later. Optional Copilot context/recovery permissions are labelled as extras, not prerequisites for raw audit ingestion. A broader permission already required by another selected import is shown when it covers a narrower one.").AppendLine();
            if (Workloads.Count == 0) text.AppendLine("No workload imports are selected.");
            foreach (var workload in Workloads)
            {
                text.Append("### ").AppendLine(workload).AppendLine();
                text.AppendLine("| Runtime permission / connection | API / permission type | Required or optional? | Why this import needs it |");
                text.AppendLine("| --- | --- | --- | --- |");
                foreach (var permission in WorkloadPermissions.Where(p => p.Workload == workload))
                    text.Append("| ").Append(Escape(permission.Permission)).Append(" | ").Append(Escape(permission.ApiAndType))
                        .Append(" | ").Append(Escape(permission.Need)).Append(" | ").Append(Escape(permission.Why)).AppendLine(" |");
                text.AppendLine();
            }
            text.AppendLine().AppendLine("## Access boundaries").AppendLine();
            text.AppendLine("- Application permissions belong to the runtime app registration and require administrator consent. They are not Azure RBAC or user directory roles.");
            text.AppendLine("- Delegated connections also require a signed-in reader with access to the source. Consent does not grant that reader a new role.");
            text.AppendLine("- Keep the deployment account separate from the runtime app and managed identities. Do not assign Owner or Global Administrator to the runtime app.");
            text.AppendLine("- The current installer grants several Azure roles at resource-group scope and Service Bus Data Owner when enabled. Those are current installer behavior, not a claim of absolute least privilege; narrower manual alternatives are identified below.");
            text.AppendLine("- This report does not grant permissions, create resources or change configuration. Review optional services and broad scopes before approving.");
            foreach (PrerequisiteCategory category in Enum.GetValues(typeof(PrerequisiteCategory)))
            {
                var requirements = Requirements.Where(r => r.Category == category).ToList();
                if (requirements.Count == 0) continue;
                text.AppendLine().Append("## ").AppendLine(CategoryTitle(category)).AppendLine();
                text.AppendLine(CategoryDescription(category)).AppendLine();
                text.AppendLine("| Requirement | Identity / type | Scope / audience | Needed for |");
                text.AppendLine("| --- | --- | --- | --- |");
                foreach (var requirement in requirements)
                {
                    text.Append("| ").Append(Escape(requirement.Name)).Append(" | ")
                        .Append(Escape(requirement.Identity)).Append(" | ").Append(Escape(requirement.Scope))
                        .Append(" | ").Append(Escape(string.Join(", ", requirement.Workloads))).AppendLine(" |");
                }
                foreach (var requirement in requirements)
                {
                    text.AppendLine().Append("### ").AppendLine(requirement.Name).AppendLine()
                        .AppendLine(requirement.Purpose).AppendLine()
                        .Append("**Identity / permission type:** ").AppendLine(requirement.Identity).AppendLine()
                        .Append("**Scope / traffic path:** ").AppendLine(requirement.Scope).AppendLine()
                        .Append("**Applies to:** ").AppendLine(string.Join(", ", requirement.Workloads)).AppendLine()
                        .Append("**Who sets this up:** ").AppendLine(requirement.Responsibility).AppendLine()
                        .Append("**Setup notes and checks:** ").AppendLine(requirement.AdminAction).AppendLine()
                        .Append("[Reference](").Append(requirement.Reference).AppendLine(")");
                }
            }
            text.AppendLine().AppendLine("## Administrator handoff checklist").AppendLine();
            text.AppendLine("1. Confirm the selected workloads and optional infrastructure; remove unneeded options in the installer and regenerate this report.");
            text.AppendLine("2. Identify the runtime app, deployment app, managed identities and any delegated readers out of band. Approve only the listed audiences, permission types and scopes.");
            text.AppendLine("3. Have the relevant owners confirm consent, deployment privileges, licensing, privacy, network paths, provider registration, policy compatibility and capacity. A satisfied check requires no change; unresolved items need an owner before deployment.");
            text.AppendLine("4. Run the installer with the approved configuration. Let it create/reuse resources and configure the documented runtime grants; do not duplicate that work manually unless a pre-provisioned deployment has been agreed.");
            text.AppendLine("5. After deployment, connect any selected delegated readers and configure the workload-specific runtime settings. Confirm allowed and denied access where data is scoped.");
            text.AppendLine("6. Run Test Configuration, then validate the selected workloads in the portal and importer logs. Some delegated and billing imports are not covered by Test Configuration; a permission grant or resource existing is not proof of a successful import.");
            return text.ToString();
        }

        private static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");

        internal void BuildWorkloadPermissions()
        {
            var rows = new List<WorkloadPermissionRow>();
            foreach (var workload in Workloads)
            {
                var relevant = Requirements.Where(r => r.Workloads.Contains(workload)).ToArray();
                if (!relevant.Any(r => r.Category == PrerequisiteCategory.ApplicationPermissions))
                    rows.Add(new WorkloadPermissionRow { Workload = workload, Requirement = relevant.First(), NoApplicationGrant = true });
                foreach (var requirement in relevant.Where(r => r.Category == PrerequisiteCategory.ApplicationPermissions
                    || r.Category == PrerequisiteCategory.DelegatedConnections || r.Category == PrerequisiteCategory.OptionalEnrichment))
                    rows.Add(new WorkloadPermissionRow { Workload = workload, Requirement = requirement });
            }
            WorkloadPermissions = rows;
        }
    }

    /// <summary>
    /// One catalogue for the native page and administrator handoff. Add a workload decision here for each
    /// ImportProp; the coverage test rejects new toggles without a prerequisite decision.
    /// Only selection flags are read: never include entered environment values in this shareable report.
    /// </summary>
    public static class DeploymentPrerequisites
    {
        private const string Graph = "Microsoft Graph - application, admin consent";
        private const string GraphScope = "Runtime app / tenant";
        private const string GraphReference = "https://learn.microsoft.com/graph/permissions-reference";
        private const string RbacReference = "https://learn.microsoft.com/azure/role-based-access-control/built-in-roles";
        private const string AuditReference = "https://learn.microsoft.com/office/office-365-management-api/office-365-management-activity-api-reference";
        private const string ProjectReference = "https://github.com/pnp/Microsoft365-Analytics-Insights/wiki";
        private const string Shared = "Shared solution infrastructure";

        private sealed class WorkloadDefinition
        {
            public string Property { get; }
            public string Name { get; }
            public Action<Builder> AddRequirements { get; }
            public WorkloadDefinition(string property, string name, Action<Builder> add)
            { Property = property; Name = name; AddRequirements = add; }
        }

        private static readonly WorkloadDefinition[] Catalogue =
        {
            new WorkloadDefinition(nameof(ImportTaskSettings.GraphUsersMetadata), "User & licence inventory", b =>
            {
                b.Permission("User.Read.All", "User delta, profiles, managers and assigned licences.");
                b.Permission("Directory.Read.All", "The current user importer reads other users' directory memberships. Test Configuration explicitly checks this grant.");
                b.Permission("Organization.Read.All", "Purchased SKU inventory, including prepaid and unassigned seats. Test Configuration explicitly checks this grant even when a broader directory grant is present.");
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.GraphUsageReports), "Microsoft 365 usage reports", b =>
            {
                b.Reports("Read the Microsoft 365 activity reports for Teams, email, SharePoint, OneDrive, Viva Engage and app usage.");
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.GraphCopilotUsageReports), "Microsoft 365 Copilot usage reports", b =>
            {
                b.Reports("Read the official Microsoft 365 Copilot user-count summary/trend and per-user usage-detail reports.");
                b.Check("copilot-reports-licence", "Copilot usage report availability", "Microsoft 365 administrator",
                    "Microsoft 365 global cloud", "The official Copilot usage reports need eligible Copilot licensing and report data; report publication is not immediate.",
                    "Confirm supported cloud, licences and report availability. Enable user inventory separately for licence/organisation context.", GraphReference);
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.GraphTeams), "Teams deep analytics", b =>
            {
                b.Permission("Group.Read.All", "Enumerate groups, owners and members, and read Teams, channels and tabs. This build uses the existing Group.Read.All path; it does not install a resource-specific-consent Teams app.");
                b.Add("teams-delegated", PrerequisiteCategory.DelegatedConnections, "Team.ReadBasic.All + ChannelMessage.Read.All",
                    "Microsoft Graph - delegated, admin consent", "Runtime app + connected Teams member",
                    "Channel messages and replies are read with per-Team delegated refresh tokens, not the runtime app-only token.",
                    "Consent these delegated permissions; allow openid, email, profile and offline_access. Register the portal Web sign-in redirect URI. A Portal.Administration operator uses Connect to Microsoft Teams and authorises each Team they can access. Private/shared channels still require membership.",
                    "https://learn.microsoft.com/graph/api/channel-list-messages");
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.Calls), "Teams call records", b =>
            {
                b.Permission("CallRecords.Read.All", "Create the call-record notification subscription and read call records.");
                b.Permission("User.Read.All", "Resolve call participants and organisers to their directory user principal names.");
                b.Check("calls-webhook", "Public HTTPS call-record webhook", "Network / Teams administrator", "Portal webhook and Service Bus queue",
                    "Microsoft Graph must reach and validate the calls webhook even when the solution's data services are private.",
                    "Keep the notification endpoint reachable by Graph. Ensure Service Bus is enabled and reachable by the web app and importer. Permission consent alone does not prove subscription or queue readiness.", GraphReference);
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.SentEmails), "Sent email metadata", b =>
            {
                b.Permission("Mail.Read", "Read Sent Items message delta for the selected mailboxes. The current importer requests message properties, not just Mail.ReadBasic fields.");
                b.Check("mail-inventory", "Mailbox inventory in the analytics database", "Solution administrator", "Approved Exchange Online mailboxes",
                    "The sent-email importer scans users with a populated Mail field in SQL. Mail.Read alone does not populate that inventory.",
                    "For a new deployment, enable User & licence inventory and approve its directory permissions, or supply an already populated supported inventory. Confirm Exchange Online mailboxes and apply mailbox access controls independently.", ProjectReference);
                b.Check("mail-scope", "Mailbox access scope", "Exchange administrator", "Only the approved mailboxes",
                    "Mail.Read application consent can expose all mailboxes unless Exchange access controls restrict it. A user-group import filter is not an authorisation boundary.",
                    "The current Test Configuration checks the Entra Mail.Read token role. If retaining that grant, constrain it with a supported Application Access Policy. Exchange Application RBAC grants are additive: a scoped RBAC assignment does not restrict retained unscoped Entra consent. An RBAC-only alternative requires explicit validation of the importer and its permission checks. Prove allowed and denied mailbox access; never keep tenant-wide consent merely to silence a check.",
                    "https://learn.microsoft.com/exchange/permissions-exo/application-rbac");
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.ActivityLog), "SharePoint / OneDrive audit", b =>
                b.Audit(false, "Read SharePoint and OneDrive audit events from the Audit.SharePoint content feed.")),
            new WorkloadDefinition(nameof(ImportTaskSettings.Copilot), "Microsoft 365 Copilot audit", b =>
            {
                b.Audit(false, "Read CopilotInteraction audit events from the Audit.General content feed.");
                b.Add("copilot-file-enrichment", PrerequisiteCategory.OptionalEnrichment, "Sites.Read.All + User.Read.All",
                    Graph, GraphScope, "Resolve accessed SharePoint/OneDrive files and user context. Raw audit interaction import does not require these enrichment grants.",
                    "Request only if file-context enrichment is approved. A /shares driveItem lookup additionally documents Files.ReadWrite.All for app-only access: do not grant tenant-wide write access just to clear enrichment warnings; review that optional path separately.",
                    "https://learn.microsoft.com/graph/api/shares-get");
                b.Add("copilot-meeting-enrichment", PrerequisiteCategory.OptionalEnrichment, "OnlineMeetings.Read.All",
                    Graph, "Approved meeting organisers", "Resolve meeting context referenced by Copilot audit records.",
                    "Only if meeting enrichment is needed: consent the permission and configure a Teams application access policy for the approved organisers. The base audit feed does not require it.",
                    "https://learn.microsoft.com/graph/cloud-communication-online-meeting-application-access-policy");
                b.Add("copilot-backfill", PrerequisiteCategory.OptionalEnrichment, "AuditLogsQuery.Read.All",
                    Graph, GraphScope, "Optional historical Copilot recovery through the Graph audit search path.",
                    "Request only when enabling historical audit backfill in runtime settings. Confirm audit retention/licensing separately; the installer workload toggle alone does not enable every backfill option.", GraphReference);
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.ImportPowerPlatform), "Power Platform audit activity", b =>
                b.Audit(false, "Read Power Apps, Power Automate, Power BI and Copilot Studio activity from the Audit.General content feed.")),
            new WorkloadDefinition(nameof(ImportTaskSettings.ImportDlp), "Microsoft Purview DLP events", b =>
            {
                b.Audit(true, "Read Microsoft Purview DLP policy events from the separate DLP.All content feed.");
                b.Check("dlp-licence", "DLP policy and licence coverage", "Purview administrator", "DLP.All feed",
                    "The DLP feed is distinct from general audit and Copilot interaction blocking signals.",
                    "Confirm eligible Purview licensing, active DLP policies and supported locations. This feed is not a Copilot-agent-specific DLP feed.", AuditReference);
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.WebTraffic), "SharePoint page traffic", b =>
            {
                b.Check("tracker", "SharePoint tracker deployment", "SharePoint administrator + site-collection administrator",
                    "Tenant app catalog and each approved target site",
                    "AITracker pushes page traffic to the portal. This workload needs no runtime Graph application permission just to collect page hits.",
                    "Approve tenant-wide SPFx package deployment; tracking remains enabled per selected site collection. The installer signs into SharePoint interactively, separately from the runtime app. Classic pages must permit the script/customisation path; browser clients must reach the portal and telemetry endpoint.", ProjectReference);
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.Agent365PackageCatalog), "Agent 365 package catalog", b =>
            {
                b.Permission("CopilotPackages.Read.All", "Read the tenant's Agent 365 package catalog and documented usage metrics.");
                b.Check("agent365-licence", "Microsoft Agent 365 licence", "Microsoft 365 administrator", "Tenant package-management availability",
                    "Consent alone does not license or enable the Package Management API.",
                    "Confirm Microsoft Agent 365 licensing and API availability before expecting catalog data.", GraphReference);
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.CopilotInteractionHistory), "Copilot AI interaction history", b =>
            {
                b.Permission("AiEnterpriseInteraction.Read.All", "Read per-user enterprise interaction history; separate from the Management Activity audit feed.");
                b.Permission("User.Read.All", "Resolve users for per-user interaction-history reads.");
                b.Permission("GroupMember.Read.All", "Resolve the mandatory interaction-history pilot group's members. Group.Read.All or Directory.Read.All already required by another selected workload can cover this read.");
                b.Check("interaction-pilot", "Licensed, bounded interaction-history pilot", "Microsoft 365 administrator", "Approved licensed users",
                    "Users need the M365_COPILOT_BUSINESS_CHAT service plan. At 200,000 users, an unscoped pass can issue at least 200,000 per-user requests.",
                    "Start with an approved UserGroupsFilter pilot and runtime caps. Group filtering also needs Group.Read.All or GroupMember.Read.All; it is a processing filter, not a Graph security boundary.", GraphReference);
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.CopilotStudioCredits), "Copilot Studio billed credits", b =>
            {
                b.Add("studio-connection", PrerequisiteCategory.DelegatedConnections, "Power Platform billing reader connection",
                    "Power Platform API - delegated", "https://api.powerplatform.com/.default",
                    "The current consumption importer uses a connected Power Platform administrator. The application-only path reads capacity, not consumption. .default is an audience/scope request, not a named Licensing.Read permission.",
                    "Have a Power Platform Administrator (or an approved equivalent with proven entitlement-read access) review the API's delegated consent and connect through Administration > Copilot Studio billing connection with Portal.Administration. Register the portal Web callback /signin-agent-costs and allow offline_access. The exact licensing-route entitlement must be validated by the connection probe; no invented granular permission or role is assumed.",
                    "https://learn.microsoft.com/power-platform/admin/programmability-permission-reference");
                b.Check("studio-capacity", "Application-only capacity access - administrator review", "Power Platform administrator",
                    "Runtime service principal / Power Platform RBAC",
                    "Without a delegated connection the source only attempts the capacity route. An RBAC assignment is not proof of access to the licensing entitlement routes.",
                    "Review the supported Power Platform service-principal role assignment for this tenant and prove the capacity request succeeds. Do not assign tenant administrator roles to the runtime app, and do not expect an app-only grant to unlock consumption.", ProjectReference);
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.AzureCostManagement), "Azure cost management", b =>
            {
                b.Add("azure-cost-reader", PrerequisiteCategory.RuntimeAccess, "Cost Management Reader", "Runtime app - Azure RBAC",
                    "Each approved AzureCostScopes scope, not the whole tenant",
                    "Read Azure spend using the management.azure.com audience. The deployment resource-group Reader assignment does not cover external billing scopes.",
                    "Assign Cost Management Reader at each supported subscription/resource-group scope. EA/MCA billing scopes have separate billing-role rules; ask the billing owner to approve access, not assume a subscription role covers them.",
                    "https://learn.microsoft.com/azure/cost-management-billing/costs/assign-access-acm-data",
                    "The owner of each external Azure/billing scope grants read access separately. The installer does not grant access to those cost scopes.");
                b.Check("azure-cost-scopes", "Explicit billing scopes and meter filters", "Azure billing administrator", "App Service runtime settings",
                    "The installer does not populate AzureCostScopes. With no meter filter every meter in the configured scope can be imported.",
                    "Set AzureCostScopes explicitly; review AzureCostGroupBy, service/meter filters and billing tags. Isolate workloads by scope when billing cannot distinguish them. Prepaid capacity is not Azure pay-as-you-go spend.", ProjectReference);
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.CopilotCostManagement), "Copilot credit history (preview)", b =>
            {
                foreach (var scope in CostScopes.Graph)
                    b.Add("cost-" + scope, PrerequisiteCategory.DelegatedConnections, scope.Substring(scope.LastIndexOf('/') + 1),
                        "Microsoft Graph - delegated, admin consent", "Runtime app + dedicated connected reader",
                        "Read selected-policy Copilot credit balances and consumption through Microsoft Graph beta APIs.",
                        "Consent this delegated permission, not an application permission. Register the portal HTTPS origin plus /copilot-cost/callback as a Web redirect URI. A Portal.Administration operator connects a reader with active tenant-wide Global Reader; allow openid, profile and offline_access. Do not bypass MFA or Conditional Access.", GraphReference);
                b.Permission("User.Read.All", "Directory inventory for the preview collector, independently of the user metadata import.");
                b.Check("cost-preview", "Preview approval and durable Table storage", "Tenant security administrator", "Delegated reader and solution storage",
                    "The source APIs are beta and not supported for production use. A connected reader and reachable Azure Table storage are mandatory.",
                    "Approve preview use explicitly. The installer enables the connection flag but does not connect a reader or grant consent. Verify the post-deployment probe; Test Configuration cannot validate delegated access.", ProjectReference);
            })
        };

        public static IReadOnlyList<string> WorkloadProperties => Catalogue.Select(w => w.Property).ToArray();

        public static DeploymentPrerequisiteReport Build(SolutionInstallConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var settings = config.SolutionConfig?.ImportTaskSettings ?? new ImportTaskSettings();
            var selected = Catalogue.Where(w => settings.IsImportEnabled(w.Property)).ToArray();
            var builder = new Builder { Workload = Shared };
            AddSharedRequirements(builder, config);
            foreach (var workload in selected)
            {
                builder.Workload = workload.Name;
                workload.AddRequirements(builder);
            }
            builder.CoverGroupReadPermissions();
            AddSelectedInfrastructure(builder, config, settings);
            AddPlatformChecks(builder, config, settings);
            var report = new DeploymentPrerequisiteReport
            {
                Workloads = selected.Select(w => w.Name).ToArray(),
                OptionalComponents = SelectedOptionalComponents(config, settings),
                ConfigurationChoices = new[]
                {
                    "Environment preset: " + config.EnvironmentType + ". Resource requests are described below; this is not a tenant-size capacity assessment.",
                    "SQL authentication selection: " + (config.SqlAuthMode == SqlServerAuthMode.EntraId ? "Microsoft Entra ID" : "legacy SQL login") + ". Existing-server authentication is detected during deployment, not converted by this report.",
                    "Networking: " + (config.NetworkConfig?.Enabled == true
                        ? config.NetworkConfig.AllowPublicAccess ? "VNet/private endpoints selected, with public access also allowed." : "VNet/private endpoints selected, with public data-service access disabled."
                        : "public service endpoints; no VNet/private endpoints selected."),
                    "Optional services: Service Bus " + (config.ServiceBusEnabled ? "selected" : "off")
                        + "; Azure AI Language " + (config.CognitiveServicesEnabled ? "selected" : "off")
                        + "; Azure OpenAI " + (config.FoundryPromptEnabled ? "selected" : "off")
                        + "; health alerts " + (config.HealthMonitoring?.Enabled == true ? "selected" : "off") + ".",
                    "Installer tasks: release content " + (config.TasksConfig?.InstallLatestSolutionContent == true ? "selected" : "not selected")
                        + "; database schema upgrade " + (config.TasksConfig?.UpgradeSchema == true ? "selected" : "not selected") + ".",
                    "Package source: " + (config.DownloadLatestStable ? "latest stable GitHub release." : "local release ZIPs; automatic GitHub downloads are not selected.")
                },
                Requirements = builder.Items.OrderBy(r => r.Category).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray()
            };
            report.BuildWorkloadPermissions();
            return report;
        }

        private static IReadOnlyList<OptionalAzureComponent> SelectedOptionalComponents(SolutionInstallConfig config, ImportTaskSettings settings)
        {
            var components = new List<OptionalAzureComponent>();
            if (config.CognitiveServicesEnabled)
                components.Add(new OptionalAzureComponent
                {
                    Id = "language", Name = "Azure AI Language / Text Analytics", CanRemoveWithoutChangingImports = true,
                    FunctionalityLost = "Azure Language sentiment scoring, detected language and key phrases where used, including sent-email sentiment and Copilot interaction-history prompt analysis. Base import records/statistics can still be collected without those extra scores.",
                    HowToRemove = "Turn off Language / Text Analytics provisioning on Azure AI. For an existing deployment, also remove or replace its runtime endpoint/credential configuration; do not assume the checkbox deletes or disconnects an existing service."
                });
            if (config.ServiceBusEnabled)
                components.Add(new OptionalAzureComponent
                {
                    Id = "service-bus", Name = "Azure Service Bus", CanRemoveWithoutChangingImports = !settings.Calls,
                    FunctionalityLost = settings.Calls
                        ? "Teams call-record notification buffering and call-record ingestion. Teams usage reports and Teams deep analytics are separate imports and do not require Service Bus."
                        : "No loss for the currently selected imports: Teams call-record import is off. Teams usage reports and Teams deep analytics do not require Service Bus. Call-record features would need both Calls and Service Bus enabled later.",
                    HowToRemove = settings.Calls
                        ? "First deselect Teams calls / call-records import on Workloads, accepting loss of call-record analytics; then disable Service Bus on Database & storage. Leaving Calls selected without Service Bus is not a working smaller deployment."
                        : "Teams calls import is not selected. Disable Service Bus on Database & storage if no other externally managed component uses the namespace."
                });
            if (config.FoundryPromptEnabled)
                components.Add(new OptionalAzureComponent
                {
                    Id = "openai", Name = "Azure OpenAI account and model deployment", CanRemoveWithoutChangingImports = true,
                    FunctionalityLost = "Prompt-category classification using the Azure OpenAI backend. Copilot audit collection and non-classification import statistics do not require this resource. Provisioning alone does not turn prompt categorisation on.",
                    HowToRemove = "Turn off Azure OpenAI provisioning on Azure AI. If prompt categorisation is enabled in the portal/runtime, disable it or configure an approved alternative backend before removing an existing deployment."
                });
            return components;
        }

        private static void AddSharedRequirements(Builder b, SolutionInstallConfig config)
        {
            b.Check("runtime-app", "Runtime Entra app registration", "Entra application administrator / consent administrator",
                "Runtime app, separate from deployment app",
                "The importer and portal authenticate to Microsoft 365 as this application. The installer uses its client-secret credential for runtime Azure RBAC fallback; this is not DefaultAzureCredential.",
                "Create the app, configure the supported credential and portal HTTPS Web redirect URI, and grant only the selected API permissions with tenant admin consent. API consent does not follow from Azure Owner. An appropriately privileged consent administrator (for example Privileged Role Administrator for Graph application permissions) approves; the runtime app needs no Entra administrator directory role.", GraphReference,
                "App-registration owner prepares the deployment/runtime applications and credentials; Entra consent administrator approves API access. The installer consumes those details; it does not create the registrations.");
            b.Check("portal-roles", "Portal.Administration / Portal.SeePII", "Portal users and groups - app roles", "Runtime enterprise application",
                "Portal administration and access to identifiable-person analytics are independent permissions; neither implies the other.",
                "Define and assign the documented app roles to approved users/groups. Assign Administration to operators and SeePII only to authorised analysts; aggregates do not require SeePII. Do not substitute Entra Global Administrator for portal access.", ProjectReference,
                "App-registration / enterprise-application owner defines the portal app roles and assigns approved users/groups. Azure resource provisioning does not make these user-access decisions.");
            var production = config.EnvironmentType == Models.EnvironmentTypeEnum.Production;
            b.Resource("app-service", "Windows App Service plan + web app", "Portal and both continuous WebJobs",
                (production ? "This installer requests Basic B2 for Production. " : "This installer requests Basic B1 for Testing. ")
                + "Always On must be enabled. Share the solution host across workloads; validate production sizing.");
            b.Resource("sql", "Azure SQL logical server + analytics database", "Shared analytics store",
                (production || config.NetworkConfig?.Enabled == true ? "This installer requests S2. " : "This installer requests Basic for Testing without VNet. ")
                + "Existing resources may retain their tier. Size for data volume and retention, not user count alone; the testing tier is not a 200,000-user production sizing recommendation.");
            b.Resource("storage", "Storage account - Blob and Table services", "Runtime state, checkpoints and delegated tokens", "Keep both endpoints reachable. Table access is separate from Blob access; Redis is not used.");
            b.Resource("insights", "Application Insights + Log Analytics workspace", "Importer/portal diagnostics and page telemetry", "The current installer provisions these for every installation, not only page-traffic workloads.");
            b.Resource("vault", "Azure Key Vault", "Runtime credential/certificate bootstrap", "Current installer always provisions the vault and appsecret; review the supported secret/certificate lifecycle.");

            b.Access("reader", "Reader", "Runtime app - Azure RBAC", "Deployment resource group",
                "Current installer grants control-plane Reader across the deployment group, including Application Insights discovery.",
                "No Contributor/Owner needed by the runtime. If maintaining resources manually, evaluate resource-scoped read/query access; running this installer still adds its resource-group Reader assignment.");
            b.Access("blob", "Storage Blob Data Contributor", "Runtime app - Azure RBAC", "Deployment resource group (current installer)",
                "Blob checkpoint/tracing writes and fallback when shared-key credentials are refused.",
                "For a hand-managed deployment, scope to the solution storage account rather than unrelated accounts. The current installer applies this role to the group.");
            b.Access("table", "Storage Table Data Contributor", "Runtime app - Azure RBAC", "Deployment resource group (current installer)",
                "Durable AnalyticsState reads/writes; required for delegated connections and RBAC fallback when keys are disabled, invalid or rotated.",
                "For a hand-managed deployment, scope to the solution storage account. Blob roles do not grant Table access; keep the Table endpoint reachable.");
            b.Access("vault-runtime", "Key Vault Secrets User + Key Vault Certificate User", "Runtime app AND App Service managed identity",
                "Solution Key Vault only",
                "The installer grants runtime secret Get/List and certificate Get, and managed-identity secret/certificate Get. RBAC vaults use these built-in roles; access-policy vaults use the corresponding permissions.",
                "Keep vault reads separate from management roles. Certificate private-key retrieval also needs secret access. Grant no vault-write access to these runtime identities.");
            if (config.SqlAuthMode == SqlServerAuthMode.EntraId)
                b.Add("sql-runtime", PrerequisiteCategory.RuntimeAccess, "db_datareader + db_datawriter + db_ddladmin",
                    "App Service system-assigned managed identity - SQL contained user", "Analytics database only",
                    "Current Entra-authentication path grants these roles to the App Service identity for the portal and hosted WebJobs; Azure RBAC Reader/Contributor does not grant SQL data access.",
                    "Provision the contained user and these database roles. Keep the installer database owner separate. Existing SQL authentication is detected at deployment, so validate existing-server mode rather than assume this selection converts it.",
                    "https://learn.microsoft.com/azure/azure-sql/database/authentication-aad-overview");
            else
                b.Check("sql-login", "SQL-login access (legacy mode)", "SQL administrator", "Analytics database connection",
                    "The selected SQL-login path uses the configured SQL administrator credential for the deployed connection. This is broader than a least-privilege application login.",
                    "Prefer the supported Entra mode for new deployments. Do not assume changing this selection converts an existing server; the installer detects its real authentication mode. Agree database-access and credential handling with the DBA.", ProjectReference);
            if (config.SqlAuthMode == SqlServerAuthMode.EntraId && config.SQLEntraDatabaseUsers?.Any() == true)
                b.Add("sql-additional-users", PrerequisiteCategory.RuntimeAccess, "db_owner (additional database users)",
                    "Configured Entra users/groups - SQL contained users", "Analytics database only",
                    "Additional database users are selected. The current installer grants db_owner, not read-only access, to each configured principal. Direct SQL access bypasses portal PII restrictions.",
                    "Have the DBA approve these broad grants separately, or use a manually managed read-only alternative if sufficient. Provide principal object IDs out of band to avoid extra directory-resolution grants; do not replace the server Entra administrator.", ProjectReference);

            b.Add("deploy-azure", PrerequisiteCategory.DeploymentAccess, "Contributor + Role Based Access Control Administrator (or equivalent)",
                "Deployment app - Azure control plane", "Dedicated deployment resource group",
                "Provision/update resources and assign roles. Contributor alone excludes Microsoft.Authorization/roleAssignments/write. Owner is an alternative, not a minimum runtime requirement.",
                "An Azure administrator can pre-create the resource group and register the required providers, then delegate these rights at that group. Creating the group/registering providers needs separate subscription-level rights. Review role-assignment conditions/deny policies and any existing shared resources outside the group.",
                RbacReference);
            b.Add("deploy-vault", PrerequisiteCategory.DeploymentAccess, "Key Vault Secrets Officer + Key Vault Certificate User",
                "Deployment app - vault data plane", "Solution Key Vault only",
                "The installer reads/writes the runtime secret; on an RBAC vault its policy-mapping tasks assign these roles. Access-policy vaults use equivalent Get/List/Set/Delete/Recover/Backup/Restore and certificate Get permissions.",
                "Allow vault-scoped role assignment or access-policy updates plus the documented data-plane access. An Azure management role by itself does not read or write secrets.", RbacReference);
            b.Add("deploy-sql", PrerequisiteCategory.DeploymentAccess, "Database owner for schema/configuration tasks",
                "Deployment app / SQL deployment identity", "Analytics database only",
                "Migrations and configuration registration create/alter schema. Entra installs use the deployment app as the default server Entra administrator, or bootstrap it as a contained db_owner on an existing server.",
                "Have the DBA approve the selected authentication path and grant database deployment access. Do not replace an existing server's Entra administrator just to grant a person access. Directory Readers on the SQL server identity is only a fallback when SQL must resolve principals by name, not a blanket prerequisite.",
                ProjectReference);
        }

        private static void AddSelectedInfrastructure(Builder b, SolutionInstallConfig config, ImportTaskSettings settings)
        {
            b.Workload = "Selected infrastructure options";
            if (settings.Calls || config.ServiceBusEnabled)
            {
                b.Resource("service-bus", "Azure Service Bus namespace + graphcalls queue", "Teams call-record notification buffer",
                    config.ServiceBusEnabled
                        ? "Selected in Database & storage. Required by Calls; otherwise optional and removable. Register Microsoft.ServiceBus; VNet/private endpoints require Premium."
                        : "Required by the selected Calls workload, but Service Bus is disabled. Enable it before deployment. Register Microsoft.ServiceBus; private endpoints require Premium.",
                    config.ServiceBusEnabled ? null : "Not currently scheduled: the operator must enable Service Bus first. The installer then creates or reuses the namespace/queue; it will not do so while this option is off.");
                b.Access("service-bus", "Azure Service Bus Data Owner", "Runtime app - Azure RBAC", "Deployment resource group (current installer)",
                    "Webhook sends and importer receives. This is the current installer's broader built-in grant, not the theoretical minimum for those two actions.",
                    "For a manually managed solution, review Data Sender + Data Receiver scoped to the queue. The current installer assigns Data Owner whenever Service Bus is enabled; it does not apply the narrower pair.");
            }
            if (config.CognitiveServicesEnabled)
            {
                b.Resource("language", "Azure AI Language (Text Analytics)", "Optional sentiment and key-phrase enrichment",
                    "Selected in Azure AI; not a prerequisite for raw audit, usage reports or page-hit ingestion. Register Microsoft.CognitiveServices. Adds billable calls; disable if unnecessary.");
                b.Access("language", "Cognitive Services User", "Runtime app - Azure RBAC", "Deployment resource group (current installer)",
                    "Language data-plane fallback when local-key authentication is unavailable.",
                    "A hand-managed deployment can scope this to the Language resource. The current installer grants at resource-group scope.");
            }
            if (config.FoundryPromptEnabled)
            {
                b.Resource("openai", "Azure OpenAI account + model deployment", "Optional prompt categorisation",
                    "Selected in Azure AI. Register Microsoft.CognitiveServices; requires model availability, supported deployment type, region and quota. Provisioning does not itself enable prompt-category processing.");
                b.Access("openai", "Cognitive Services OpenAI User", "Runtime app - Azure RBAC", "Deployment resource group (current installer)",
                    "Inference using the runtime service principal when no API key is configured.",
                    "A hand-managed deployment can scope this to the OpenAI resource. No resource-management role is needed for inference.");
            }
            if (settings.GraphUsageReports)
            {
                b.Resource("automation", "Azure Automation account + maintenance runbooks", "Current usage-report deployment path",
                    "Provisioned when Microsoft 365 usage reports are selected; not for Copilot-only usage reports. Requires Microsoft.Automation provider.");
                if (config.SqlAuthMode == SqlServerAuthMode.EntraId)
                    b.Add("automation-sql", PrerequisiteCategory.RuntimeAccess, "db_owner (maintenance runbooks)",
                        "Automation account managed identity - SQL contained user", "Analytics database only",
                        "The current maintenance runbooks run index maintenance and schema/profiling work.",
                        "Approve this separate identity explicitly; it is not the runtime Microsoft 365 app. The App Service identity's narrower SQL roles do not cover the maintenance tasks.", ProjectReference);
            }
            if (config.HealthMonitoring?.Enabled == true)
                b.Resource("health", "Azure Monitor alert rules, action group and availability check", "Selected health monitoring",
                    "Selected in Hosting & services. Up to seven billable default rules plus selected availability checks; approve notification routing out of band. Disabling alerts does not remove basic telemetry.");
            if (config.NetworkConfig?.Enabled == true)
            {
                b.Resource("private-network", "VNet, integration subnet and private endpoints", "Selected private networking",
                    "Requires Microsoft.Network; SQL, web app/SCM, Blob, Table and Key Vault endpoints, plus endpoints for enabled optional services. Keep a separate delegated App Service integration subnet.");
                b.Resource("private-dns", "Private DNS and resolution paths", "Selected private networking",
                    config.NetworkConfig.DeployDnsZones
                        ? "Installer-managed zone links for selected services. Check existing shared zones and ownership before changing DNS."
                        : "Private DNS creation is disabled. The network owner must provide equivalent records/forwarding, including Blob, Table and SCM resolution.",
                    config.NetworkConfig.DeployDnsZones ? null : "Network owner supplies DNS; automatic private DNS creation is not selected.");
                b.Check("private-reachability", "Private-network reachability review", "Network administrator", "Installer host, App Service and Automation",
                    "Private endpoints require working DNS and a connected deployment host. Public-network settings do not automatically privatise Application Insights or Log Analytics.",
                    "Provide VPN/peering or an in-network installer host, configure App Service integration, and review Azure Monitor Private Link Scope separately if required. Public Graph callbacks/browser tracking still need an approved reachable route; do not assume private-only hosting works for Calls or page tracking.", ProjectReference);
                if (settings.GraphUsageReports && (!config.NetworkConfig.AllowPublicAccess
                    || !string.IsNullOrWhiteSpace(config.NetworkConfig.HybridWorkerVmResourceId)))
                    b.Resource("hybrid-worker", "Connected Windows Hybrid Runbook Worker VM", "Automation with private data services",
                        string.IsNullOrWhiteSpace(config.NetworkConfig.HybridWorkerVmResourceId)
                            ? "No worker selected. For private-only data services, supply an existing connected VM; the installer registers the worker but does not create that VM."
                            : "An existing worker VM is selected. Approve extension/VM rights at its own scope, managed identity, DNS and access to SQL/private endpoints.",
                        "Azure/network owner supplies an existing connected VM. The installer registers its worker group/extension; it does not create the VM.");
            }
            if (settings.ActivityLog || settings.WebTraffic)
                b.Add("deploy-sharepoint", PrerequisiteCategory.DeploymentAccess, "SharePoint administrator + target site-collection administrator",
                    "Interactive SharePoint deployment user, not runtime app", "Tenant app catalog and selected sites",
                    "The current SharePoint deployment path is available for selected audit/page-traffic workloads. Raw audit-feed reads do not themselves need site script deployment.",
                    "Use the built-in SharePoint Online Management Shell sign-in app if permitted. The supported custom public-client setup uses the http://localhost redirect and SharePoint delegated AllSites.FullControl with consent. This is the interactive deployment user's app, not a runtime Graph application grant; confirm tenant policy and remove deployment-only access when no longer needed.", ProjectReference);
        }

        private static void AddPlatformChecks(Builder b, SolutionInstallConfig config, ImportTaskSettings settings)
        {
            b.Workload = "Shared solution / selected services";
            var privateNetwork = config.NetworkConfig?.Enabled == true;
            var privateOnly = privateNetwork && !config.NetworkConfig.AllowPublicAccess;
            b.Check("installer-https", "Installer outbound HTTPS and deployment endpoint", "Network / proxy owner",
                "Installer host -> login.microsoftonline.com, management.azure.com and {web-app}.scm.azurewebsites.net / TCP 443",
                "The installer obtains Entra tokens, manages Azure resources and uploads release ZIPs to the App Service SCM (Kudu) endpoint. Successful portal browsing alone does not prove that the installer can deploy.",
                "Before the run, allow these HTTPS destinations through the host firewall/proxy. Resolve the SCM host from the installer machine and confirm its TLS connection and publishing access. With private App Service access, the installer must be connected to the VNet and resolve both the web-app and SCM names privately. The proxy setting in the installer applies to deployment requests; it does not configure the deployed web app's outbound network.",
                "https://learn.microsoft.com/azure/app-service/deploy-zip");
            if (config.DownloadLatestStable)
                b.Check("release-download", "Stable release download access", "Network / proxy owner",
                    "Installer host -> api.github.com, github.com and release asset redirect hosts / TCP 443",
                    "Latest stable release is selected, so the installer reads GitHub release metadata and follows the download URLs for the release ZIPs.",
                    "Allow the public repository's GitHub API and release downloads, including the asset host reached by redirects (commonly release-assets.githubusercontent.com). Verify an actual ZIP download from the installer machine, not just github.com home-page access. If that path is not approved, download the release through an approved process and choose local packages in Release & packages.",
                    ProjectReference);

            var sqlNetwork = privateOnly
                ? "The SQL name must resolve to the private endpoint from the installer, App Service and any Hybrid Worker. Do not enable a public SQL firewall exception as a substitute for private routing."
                : "Confirm that SQL firewall rules admit the installer host and the runtime host's applicable outbound addresses; those addresses are supplied separately, not exported here.";
            b.Check("sql-network", "SQL connection path and connection policy", "Network owner + database owner",
                "Installer, App Service and selected Automation execution host -> {sql-server}.database.windows.net / TCP 1433 initially",
                "Schema upgrades, imports and portal queries all open database connections. SQL authentication cannot work if DNS, routing or the SQL firewall blocks the connection.",
                sqlNetwork + " Check the server's actual connection policy: public-endpoint Redirect also requires regional SQL destinations on TCP 11000-11999; Proxy uses TCP 1433. Private-endpoint Redirect has its own documented port requirements, so do not assume public ranges apply. Prove an authenticated database connection from each execution location before scheduling deployment.",
                "https://learn.microsoft.com/azure/azure-sql/database/connectivity-architecture");
            b.Check("storage-network", "Blob and Table data endpoints", "Network / storage owner",
                "App Service and importer -> {storage-account}.blob.core.windows.net and {storage-account}.table.core.windows.net / TCP 443",
                "Blob data and Table state use separate service endpoints. A working Blob connection does not prove that delta tokens, checkpoints or delegated connections can use Table storage.",
                (privateNetwork
                    ? "Provide both Blob and Table private endpoints and private DNS; check resolution from App Service, not only the administrator's workstation. "
                    : "Confirm both services permit the runtime network path under the storage firewall settings. ")
                + "Prove read/write access to the solution's Blob containers and AnalyticsState table with the configured runtime identity. An Azure management-plane Reader assignment is not that data-access test.",
                "https://learn.microsoft.com/azure/storage/common/storage-private-endpoints");
            b.Check("vault-network", "Key Vault secret and certificate endpoint", "Network / Key Vault owner",
                "Installer and App Service -> {key-vault}.vault.azure.net / TCP 443",
                "The installer writes the runtime secret; the deployed application reads the configured secret/certificate using the documented vault identities.",
                (privateOnly
                    ? "Run the installer on a connected private-network host and verify the vault name resolves privately. "
                    : "Confirm the vault firewall admits the installer and App Service paths; access to management.azure.com does not prove data-plane vault access. ")
                + "Verify a permitted secret operation as each relevant identity. DNS/firewall access and Key Vault data permissions must both work; do not broaden roles to troubleshoot a blocked route.",
                "https://learn.microsoft.com/azure/key-vault/general/network-security");
            b.Check("monitor-network", "Application Insights query and telemetry endpoints", "Network / monitoring owner",
                "App Service -> api.applicationinsights.io and the configured Application Insights ingestion endpoint / TCP 443",
                "The importer/portal query Application Insights and send diagnostics. The ingestion host comes from the resource connection string and can be regional; it is not the query API host.",
                "Permit HTTPS to api.applicationinsights.io and the exact IngestionEndpoint selected for the resource. Use the current Azure Monitor endpoint/service-tag guidance rather than a copied IP list. "
                + (settings.WebTraffic ? "Tracked SharePoint browsers must also reach the telemetry endpoint and portal over HTTPS. " : "")
                + (config.HealthMonitoring?.Enabled == true ? "If using availability checks, permit the approved Azure Monitor probe source to reach the configured web-app check URL. " : "")
                + "The installer leaves Application Insights and Log Analytics public even in private-only mode. If policy requires them private, have the monitoring/network owner design and configure Azure Monitor Private Link Scope separately.",
                "https://learn.microsoft.com/azure/azure-monitor/fundamentals/azure-monitor-network-access");

            var apiHosts = new List<string> { "login.microsoftonline.com (token acquisition)" };
            if (b.Items.Any(r => r.Category == PrerequisiteCategory.ApplicationPermissions && r.Identity == Graph)
                || settings.GraphTeams || settings.CopilotCostManagement)
                apiHosts.Add("graph.microsoft.com (selected Microsoft Graph reads)");
            if (settings.UsesActivityApi) apiHosts.Add("manage.office.com (selected Office 365 Management Activity feeds)");
            if (settings.GraphUsageReports || settings.GraphCopilotUsageReports)
                apiHosts.Add("the pre-authenticated CSV download host returned by Graph usage reports (commonly reports.office.com)");
            if (settings.CopilotStudioCredits) apiHosts.Add("api.powerplatform.com (Copilot Studio billing)");
            if (settings.AzureCostManagement) apiHosts.Add("management.azure.com (Azure Cost Management)");
            b.Check("runtime-api-network", "Selected Microsoft 365 / billing API egress", "Network / proxy owner",
                "App Service / WebJobs -> selected public-cloud API hosts / TCP 443",
                "The background importers make outbound API requests independently of the desktop installer. A successful installer sign-in does not test their egress path.",
                "Allow: " + string.Join("; ", apiHosts) + ". Resolve and test these from the App Service execution environment. "
                + "Private endpoints for SQL/Storage/Key Vault do not provide private routing to Microsoft Graph or these billing/audit APIs. A corporate proxy or TLS inspection rule must support the actual token and API requests, including selected report download redirects.",
                ProjectReference);
            if (settings.Calls)
                b.Check("calls-network", "Calls queue transport and inbound Graph validation", "Network / Teams owner",
                    "App Service webhook + importer -> {service-bus}.servicebus.windows.net / TCP 5671 and HTTPS 443",
                    "The current ServiceBusClient uses the SDK's default AMQP/TLS transport, not AMQP-over-WebSockets. Graph also sends inbound HTTPS validation and call notifications to the portal webhook.",
                    "Allow outbound AMQP/TLS TCP 5671 to the namespace plus HTTPS for token/SDK operations; opening only TCP 443 is not sufficient for this build's queue transport. Keep the approved webhook URL reachable by Microsoft Graph over HTTPS without an interactive sign-in challenge. For private Service Bus, use Premium and prove the namespace resolves to its private endpoint from App Service. Validate the subscription and an end-to-end notification after deployment.",
                    "https://learn.microsoft.com/azure/service-bus-messaging/service-bus-faq");
            if (config.CognitiveServicesEnabled || config.FoundryPromptEnabled)
            {
                var aiHosts = new List<string>();
                if (config.CognitiveServicesEnabled) aiHosts.Add("{language-account}.cognitiveservices.azure.com (Language)");
                if (config.FoundryPromptEnabled) aiHosts.Add("{openai-account}.openai.azure.com (OpenAI)");
                b.Check("ai-network", "Selected Azure AI data endpoints", "Network / Azure AI owner",
                    "App Service / importer -> selected AI resource endpoints / TCP 443",
                    "AI resource provisioning is an ARM operation; sentiment/key-phrase analysis and prompt categorisation use different data endpoints.",
                    "For the selected services, permit HTTPS to " + string.Join("; ", aiHosts) + ". "
                    + (privateNetwork ? "Resolve the selected resource's private DNS name from App Service and route through its private endpoint. " : "Confirm its public-network/firewall settings permit the runtime path. ")
                    + "Before enabling AI processing, verify the resource endpoint and inference/data-plane identity together. Merely creating the resource does not enable every AI processing option.",
                    ProjectReference);
            }

            var providers = new List<string> { "Microsoft.Web", "Microsoft.Sql", "Microsoft.Storage", "Microsoft.KeyVault", "Microsoft.Insights", "Microsoft.OperationalInsights" };
            if (config.ServiceBusEnabled || settings.Calls) providers.Add("Microsoft.ServiceBus");
            if (config.CognitiveServicesEnabled || config.FoundryPromptEnabled) providers.Add("Microsoft.CognitiveServices");
            if (settings.GraphUsageReports) providers.Add("Microsoft.Automation");
            if (privateNetwork) providers.Add("Microsoft.Network");
            b.Check("provider-registration", "Subscription resource-provider registration", "Azure subscription owner",
                "Target Azure subscription",
                "Azure must have the namespaces for the selected resource types registered. Resource-group Contributor access does not automatically authorise subscription-wide registration.",
                "Confirm registrationState is Registered for: " + string.Join(", ", providers) + ". "
                + "Use Subscription > Resource providers, or az provider show --namespace {namespace} --subscription {subscription-id}. Only if needed, a subscription owner registers that namespace. No registration change is needed for an already Registered provider; the installer does not need permanent subscription Owner just for this check.",
                "https://learn.microsoft.com/azure/azure-resource-manager/management/resource-providers-and-types");
            b.Check("policy-compatibility", "Azure Policy checks for this deployment method", "Azure policy / subscription owner",
                "Target subscription, resource group and any shared network resources",
                "A role grant does not override Azure Policy denies. The installer can reuse resources, but cannot bypass a policy that prohibits a requested setting.",
                "Before deployment, check the selected resource types, allowed locations/SKUs, required tags and role-assignment conditions. "
                + (privateOnly ? "The selected data services disable public access, but Application Insights/Log Analytics remain public unless AMPLS is configured separately. " : "Public service access is selected; confirm that the relevant publicNetworkAccess settings are permitted. ")
                + "The Windows App Service needs Always On. The current content-deployment path enables SCM basic publishing credentials for ZIP deployment and disables FTP publishing; a policy forbidding SCM basic publishing needs an approved compatible deployment approach, not a wider runtime role. "
                + "Confirm that the chosen Key Vault RBAC/access-policy model and the required runtime role assignments are allowed. The installer supports credential-free Table/Blob access when shared keys are disabled. Resolve any incompatible policy through its owner before the run; do not request blanket policy exemptions.",
                "https://learn.microsoft.com/azure/governance/policy/overview");
            var production = config.EnvironmentType == Models.EnvironmentTypeEnum.Production;
            b.Check("capacity-approval", "Requested SKUs, regional availability and spending approval", "Azure subscription / service owner",
                "Chosen deployment region and subscription; actual names supplied separately",
                "Permission to deploy is not a reservation of compute, database or AI capacity, and it does not approve ongoing charges.",
                "For new resources this preset requests App Service " + (production ? "Basic B2" : "Basic B1")
                + " and Azure SQL " + (production || privateNetwork ? "S2" : "Basic") + ". "
                + (config.ServiceBusEnabled || settings.Calls ? "Service Bus requests " + (privateNetwork ? "Premium for private endpoints" : "Standard") + "; an existing namespace may need a separate approved upgrade. " : "")
                + (config.FoundryPromptEnabled ? "For OpenAI, check the selected model/version and deployment type are offered in the region and that the subscription has sufficient deployment/token-rate quota for the selected capacity. " : "")
                + "Check available App Service workers, SQL/server quota, any selected Service Bus/AI quota and approval for recurring storage/telemetry/alert costs. Existing resources may keep their tier. At a 200,000-user tenant, review expected ingest volume and retention with the service owner; these presets are not a performance guarantee.",
                "https://learn.microsoft.com/azure/azure-resource-manager/management/azure-subscription-service-limits");
            if (privateNetwork)
            {
                var zones = new List<string> { "privatelink.database.windows.net", "privatelink.azurewebsites.net", "privatelink.blob.core.windows.net", "privatelink.table.core.windows.net", "privatelink.vaultcore.azure.net" };
                if (config.ServiceBusEnabled || settings.Calls) zones.Add("privatelink.servicebus.windows.net");
                if (config.CognitiveServicesEnabled) zones.Add("privatelink.cognitiveservices.azure.com");
                if (config.FoundryPromptEnabled) zones.Add("privatelink.openai.azure.com");
                if (settings.GraphUsageReports) zones.Add("privatelink.azure-automation.net");
                b.Check("private-dns-routing", "Private DNS and routing acceptance checks", "Network / DNS owner",
                    "Installer host, App Service integration subnet and any Hybrid Worker",
                    "Private endpoint records must be visible from each execution network. A private endpoint in Azure is not proof that the installer or web app can use it.",
                    (config.NetworkConfig.DeployDnsZones ? "Installer-managed zone creation is selected. Verify the resulting links/records and any corporate DNS forwarding. " : "Automatic zone creation is off: the network owner supplies the zone records, links and forwarding. ")
                    + "Selected zone names: " + string.Join(", ", zones) + ". Resolve each resource FQDN from the installer host and the App Service console, and confirm the private IP and allowed route/port. Include separate web-app and SCM records, and both Blob and Table services. Verify VNet integration and NSG/UDR rules rather than assuming a private endpoint gives App Service outbound connectivity. For private Automation data access, use the connected Hybrid Worker described under Azure resources.",
                    "https://learn.microsoft.com/azure/private-link/private-endpoint-dns");
            }
        }

        private sealed class Builder
        {
            private readonly Dictionary<string, DeploymentRequirement> _items = new Dictionary<string, DeploymentRequirement>();
            public IEnumerable<DeploymentRequirement> Items => _items.Values;
            public string Workload { get; set; }

            public void Add(string id, PrerequisiteCategory category, string name, string identity, string scope,
                string purpose, string action, string reference, string responsibility = null)
            {
                if (!_items.TryGetValue(id, out var item))
                    _items.Add(id, item = new DeploymentRequirement
                    {
                        Id = id, Category = category, Name = name, Identity = identity, Scope = scope,
                        Purpose = purpose, AdminAction = action, Reference = reference,
                        Responsibility = responsibility ?? DefaultResponsibility(category)
                    });
                else if (!item.Purpose.Contains(purpose))
                    item.Purpose += " " + purpose;
                item.AddWorkload(Workload, purpose);
            }

            private static string DefaultResponsibility(PrerequisiteCategory category)
            {
                switch (category)
                {
                    case PrerequisiteCategory.ApplicationPermissions:
                        return "Entra consent administrator, before deployment. The installer does not add or consent these API permissions.";
                    case PrerequisiteCategory.DelegatedConnections:
                        return "Entra consent administrator approves permissions; an authorised portal operator connects the reader after deployment.";
                    case PrerequisiteCategory.AzureResources:
                        return "Installer creates or reuses/configures the service during deployment. Azure owner approves policy, cost and capacity beforehand.";
                    case PrerequisiteCategory.RuntimeAccess:
                        return "Installer configures the solution's runtime grants during deployment, using the authorised deployment identity. External-scope exceptions are stated below.";
                    case PrerequisiteCategory.DeploymentAccess:
                        return "Deployment owner arranges the installer's authority before the run. The installer can then configure the supported vault/SQL permissions described in the setup notes.";
                    case PrerequisiteCategory.OptionalEnrichment:
                        return "Workload/security owner decides whether the extra feature is wanted; Entra consent administrator grants only approved extra access. The installer does not consent it.";
                    default:
                        return "The named service or network owner checks this condition at the stated stage. If it is already satisfied, no configuration change is needed.";
                }
            }

            public void Permission(string name, string purpose) => Add("graph-" + name,
                PrerequisiteCategory.ApplicationPermissions, name, Graph, GraphScope, purpose,
                "Add as an application permission on the runtime app and grant tenant admin consent. Do not add a delegated permission with the same name instead.", GraphReference);

            public void CoverGroupReadPermissions()
            {
                Cover("Group.Read.All", "Directory.Read.All");
                Cover("GroupMember.Read.All", _items.ContainsKey("graph-Directory.Read.All") ? "Directory.Read.All" : "Group.Read.All");
            }

            private void Cover(string permission, string coveringPermission)
            {
                if (!_items.TryGetValue("graph-" + permission, out var item)
                    || !_items.TryGetValue("graph-" + coveringPermission, out var covering)) return;
                covering.Purpose += " Also covers " + permission + ": " + item.Purpose;
                foreach (var workload in item.Workloads)
                    covering.AddWorkload(workload, coveringPermission + " also covers " + permission + " for this workload: " + item.PurposeFor(workload));
                _items.Remove(item.Id);
            }

            public void Reports(string purpose)
            {
                Permission("Reports.Read.All", purpose);
                Check("report-identities", "Identifiable usage reports", "Microsoft 365 reports administrator",
                    "Microsoft 365 report privacy setting", "Per-person matching needs identifiable user names in report exports.",
                    "Review the tenant-wide setting that conceals user, group and site names before allowing identifiable reports. Do not change it without privacy approval.", GraphReference);
            }

            public void Audit(bool dlp, string purpose)
            {
                Add(dlp ? "activity-dlp" : "activity-read", PrerequisiteCategory.ApplicationPermissions,
                    dlp ? "ActivityFeed.ReadDlp" : "ActivityFeed.Read", "Office 365 Management APIs - application, admin consent",
                    dlp ? "DLP.All feed / runtime app" : "Audit.SharePoint / Audit.General feeds / runtime app",
                    purpose + (dlp ? " ActivityFeed.Read does not grant DLP access." : " This is not the Microsoft Graph AuditLog.Read.All permission."),
                    "Grant the named Office 365 Management APIs application permission with admin consent. Enable unified auditing and confirm the workload's audit/licence coverage; do not assign an Entra administrator role to the runtime app.", AuditReference);
            }

            public void Resource(string id, string name, string purpose, string action, string responsibility = null) =>
                Add("resource-" + id, PrerequisiteCategory.AzureResources, name, "Azure resources", "Dedicated solution resource group unless noted", purpose, action, ProjectReference, responsibility);

            public void Access(string id, string name, string identity, string scope, string purpose, string action) =>
                Add("access-" + id, PrerequisiteCategory.RuntimeAccess, name, identity, scope, purpose, action, RbacReference);

            public void Check(string id, string name, string identity, string scope, string purpose, string action, string reference, string responsibility = null) =>
                Add("check-" + id, PrerequisiteCategory.OperationalChecks, name, identity, scope, purpose, action, reference, responsibility);
        }
    }
}
