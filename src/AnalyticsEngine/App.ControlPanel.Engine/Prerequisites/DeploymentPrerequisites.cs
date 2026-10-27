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
        public string Reference { get; internal set; }
        public IReadOnlyList<string> Workloads => _workloads;
        private readonly List<string> _workloads = new List<string>();

        internal void AddWorkload(string name)
        {
            if (!_workloads.Contains(name)) _workloads.Add(name);
        }

        public string Details => Name + "\r\n\r\nCategory: " + DeploymentPrerequisiteReport.CategoryTitle(Category)
            + "\r\nIdentity / permission type: " + Identity
            + "\r\nScope / audience: " + Scope + "\r\nNeeded for: " + string.Join(", ", Workloads)
            + "\r\n\r\nWhy: " + Purpose + "\r\n\r\nAdministrator action: " + AdminAction;
    }

    public sealed class DeploymentPrerequisiteReport
    {
        public IReadOnlyList<string> Workloads { get; internal set; }
        public IReadOnlyList<DeploymentRequirement> Requirements { get; internal set; }

        public const string ScopeNotice = "Planning guidance, not an access check. Based on the current workload and infrastructure selections. "
            + "No Azure or tenant discovery is performed. Optional enrichment is not part of the minimum import request.";
        public const string PrivacyNotice = "Shareable by design: entered names, IDs, addresses, paths and credentials are excluded. "
            + "Administrators must identify the target tenant, apps, resources and scopes separately.";

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
                default: return "Optional enrichment - not required for the base import";
            }
        }

        public string ToMarkdown()
        {
            var text = new StringBuilder("# Microsoft 365 Analytics Insights - deployment prerequisites\n\n");
            text.AppendLine(ScopeNotice).AppendLine().AppendLine(PrivacyNotice).AppendLine();
            text.AppendLine("## Selected workloads").AppendLine();
            if (Workloads.Count == 0) text.AppendLine("No workloads selected. Shared infrastructure below is still part of this installer's deployment.");
            foreach (var workload in Workloads) text.Append("- ").AppendLine(workload);
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
                        .Append("**Administrator action:** ").AppendLine(requirement.AdminAction).AppendLine()
                        .Append("[Reference](").Append(requirement.Reference).AppendLine(")");
                }
            }
            text.AppendLine().AppendLine("## Administrator handoff checklist").AppendLine();
            text.AppendLine("1. Confirm the selected workloads and optional infrastructure; remove unneeded options in the installer and regenerate this report.");
            text.AppendLine("2. Identify the runtime app, deployment app, managed identities and any delegated readers out of band. Approve only the listed audiences, permission types and scopes.");
            text.AppendLine("3. Grant consent and scoped Azure/database access; register the required callbacks and connect any delegated readers after deployment.");
            text.AppendLine("4. Verify licensing, audit availability, privacy settings, networking and quotas. Complete every administrator-review item; a role assignment alone does not prove API access.");
            text.AppendLine("5. Run Test Configuration, then validate the selected workloads in the deployed portal and importer logs. Some delegated and billing imports are not covered by Test Configuration.");
            return text.ToString();
        }

        private static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
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
                b.Reports();
                b.Check("report-identities", "Identifiable usage reports", "Microsoft 365 reports administrator",
                    "Microsoft 365 report privacy setting", "Per-person matching needs identifiable user names in report exports.",
                    "Review the tenant-wide setting that conceals user, group and site names before allowing identifiable reports. Do not change it without privacy approval.", GraphReference);
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.GraphCopilotUsageReports), "Microsoft 365 Copilot usage reports", b =>
            {
                b.Reports();
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
                    "Restrict the app to approved mailboxes with supported Exchange application access controls; validate that the exact importer requests still work. Consent and an import filter alone are not mailbox scoping.",
                    "https://learn.microsoft.com/exchange/permissions-exo/application-rbac");
            }),
            new WorkloadDefinition(nameof(ImportTaskSettings.ActivityLog), "SharePoint / OneDrive audit", b => b.Audit(false)),
            new WorkloadDefinition(nameof(ImportTaskSettings.Copilot), "Microsoft 365 Copilot audit", b =>
            {
                b.Audit(false);
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
            new WorkloadDefinition(nameof(ImportTaskSettings.ImportPowerPlatform), "Power Platform audit activity", b => b.Audit(false)),
            new WorkloadDefinition(nameof(ImportTaskSettings.ImportDlp), "Microsoft Purview DLP events", b =>
            {
                b.Audit(true);
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
                    "https://learn.microsoft.com/azure/cost-management-billing/costs/assign-access-acm-data");
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
            return new DeploymentPrerequisiteReport
            {
                Workloads = selected.Select(w => w.Name).ToArray(),
                Requirements = builder.Items.OrderBy(r => r.Category).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray()
            };
        }

        private static void AddSharedRequirements(Builder b, SolutionInstallConfig config)
        {
            b.Check("runtime-app", "Runtime Entra app registration", "Entra application administrator / consent administrator",
                "Runtime app, separate from deployment app",
                "The importer and portal authenticate to Microsoft 365 as this application. The installer uses its client-secret credential for runtime Azure RBAC fallback; this is not DefaultAzureCredential.",
                "Create the app, configure the supported credential and portal HTTPS Web redirect URI, and grant only the selected API permissions with tenant admin consent. API consent does not follow from Azure Owner. An appropriately privileged consent administrator (for example Privileged Role Administrator for Graph application permissions) approves; the runtime app needs no Entra administrator directory role.", GraphReference);
            b.Check("portal-roles", "Portal.Administration / Portal.SeePII", "Portal users and groups - app roles", "Runtime enterprise application",
                "Portal administration and access to identifiable-person analytics are independent permissions; neither implies the other.",
                "Define and assign the documented app roles to approved users/groups. Assign Administration to operators and SeePII only to authorised analysts; aggregates do not require SeePII. Do not substitute Entra Global Administrator for portal access.", ProjectReference);
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
            b.Check("network", "Connectivity, providers, policy and capacity", "Azure / network administrators", "Installer host, runtime host and selected services",
                "Successful API consent does not prove network reachability, regional capacity or resource-policy approval.",
                "Allow required Entra, Graph and selected API egress, ARM and Kudu deployment, database connectivity and chosen data-service endpoints. Register Microsoft.Web, Microsoft.Sql, Microsoft.Storage, Microsoft.KeyVault, Microsoft.Insights and Microsoft.OperationalInsights plus selected optional providers. Validate production sizing, region and policy before deployment.", ProjectReference);
        }

        private static void AddSelectedInfrastructure(Builder b, SolutionInstallConfig config, ImportTaskSettings settings)
        {
            b.Workload = "Selected infrastructure options";
            if (settings.Calls || config.ServiceBusEnabled)
            {
                b.Resource("service-bus", "Azure Service Bus namespace + graphcalls queue", "Teams call-record notification buffer",
                    config.ServiceBusEnabled
                        ? "Selected in Database & storage. Required by Calls; otherwise optional and removable. Register Microsoft.ServiceBus; VNet/private endpoints require Premium."
                        : "Required by the selected Calls workload, but Service Bus is disabled. Enable it before deployment. Register Microsoft.ServiceBus; private endpoints require Premium.");
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
                        : "Private DNS creation is disabled. The network owner must provide equivalent records/forwarding, including Blob, Table and SCM resolution.");
                b.Check("private-reachability", "Private-network reachability review", "Network administrator", "Installer host, App Service and Automation",
                    "Private endpoints require working DNS and a connected deployment host. Public-network settings do not automatically privatise Application Insights or Log Analytics.",
                    "Provide VPN/peering or an in-network installer host, configure App Service integration, and review Azure Monitor Private Link Scope separately if required. Public Graph callbacks/browser tracking still need an approved reachable route; do not assume private-only hosting works for Calls or page tracking.", ProjectReference);
                if (settings.GraphUsageReports)
                    b.Resource("hybrid-worker", "Connected Windows Hybrid Runbook Worker VM", "Automation with private data services",
                        string.IsNullOrWhiteSpace(config.NetworkConfig.HybridWorkerVmResourceId)
                            ? "No worker selected. For private-only data services, supply an existing connected VM; the installer registers the worker but does not create that VM."
                            : "An existing worker VM is selected. Approve extension/VM rights at its own scope, managed identity, DNS and access to SQL/private endpoints.");
            }
            if (settings.ActivityLog || settings.WebTraffic)
                b.Add("deploy-sharepoint", PrerequisiteCategory.DeploymentAccess, "SharePoint administrator + target site-collection administrator",
                    "Interactive SharePoint deployment user, not runtime app", "Tenant app catalog and selected sites",
                    "The current SharePoint deployment path is available for selected audit/page-traffic workloads. Raw audit-feed reads do not themselves need site script deployment.",
                    "Use the built-in SharePoint Online Management Shell sign-in app if permitted. The supported custom public-client setup uses the http://localhost redirect and SharePoint delegated AllSites.FullControl with consent. This is the interactive deployment user's app, not a runtime Graph application grant; confirm tenant policy and remove deployment-only access when no longer needed.", ProjectReference);
        }

        private sealed class Builder
        {
            private readonly Dictionary<string, DeploymentRequirement> _items = new Dictionary<string, DeploymentRequirement>();
            public IEnumerable<DeploymentRequirement> Items => _items.Values;
            public string Workload { get; set; }

            public void Add(string id, PrerequisiteCategory category, string name, string identity, string scope,
                string purpose, string action, string reference)
            {
                if (!_items.TryGetValue(id, out var item))
                    _items.Add(id, item = new DeploymentRequirement
                    {
                        Id = id, Category = category, Name = name, Identity = identity, Scope = scope,
                        Purpose = purpose, AdminAction = action, Reference = reference
                    });
                else if (!item.Purpose.Contains(purpose))
                    item.Purpose += " " + purpose;
                item.AddWorkload(Workload);
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
                foreach (var workload in item.Workloads) covering.AddWorkload(workload);
                _items.Remove(item.Id);
            }

            public void Reports() => Permission("Reports.Read.All", "Read the selected Microsoft 365 usage-report endpoints; shared by general and Copilot usage reports.");

            public void Audit(bool dlp)
            {
                Add(dlp ? "activity-dlp" : "activity-read", PrerequisiteCategory.ApplicationPermissions,
                    dlp ? "ActivityFeed.ReadDlp" : "ActivityFeed.Read", "Office 365 Management APIs - application, admin consent",
                    dlp ? "DLP.All feed / runtime app" : "Audit.SharePoint / Audit.General feeds / runtime app",
                    dlp ? "DLP is a separate permission; ActivityFeed.Read does not grant it." : "Read the selected Management Activity audit feed; this is not the Microsoft Graph AuditLog.Read.All permission.",
                    "Grant the named Office 365 Management APIs application permission with admin consent. Enable unified auditing and confirm the workload's audit/licence coverage; do not assign an Entra administrator role to the runtime app.", AuditReference);
            }

            public void Resource(string id, string name, string purpose, string action) =>
                Add("resource-" + id, PrerequisiteCategory.AzureResources, name, "Azure resources", "Dedicated solution resource group unless noted", purpose, action, ProjectReference);

            public void Access(string id, string name, string identity, string scope, string purpose, string action) =>
                Add("access-" + id, PrerequisiteCategory.RuntimeAccess, name, identity, scope, purpose, action, RbacReference);

            public void Check(string id, string name, string identity, string scope, string purpose, string action, string reference) =>
                Add("check-" + id, PrerequisiteCategory.OperationalChecks, name, identity, scope, purpose, action, reference);
        }
    }
}
