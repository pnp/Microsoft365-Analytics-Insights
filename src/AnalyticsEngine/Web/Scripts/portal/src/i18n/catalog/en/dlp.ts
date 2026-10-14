/**
 * English text for the DLP impact page.
 *
 * Every key here must have a Spanish counterpart in `../es/dlp.ts`; the type of that module
 * makes a missing one a build failure.
 */
export const dlp = {
  // Page header and controls
  'dlp.title': 'DLP impact on Copilot',
  'dlp.intro': 'Where Microsoft Purview Data Loss Prevention policies stopped Microsoft 365 Copilot from using content — which agents and people are most affected, and which policies are responsible.',
  'dlp.period.ariaLabel': 'Reporting period',
  'dlp.period.last7Days': 'Last 7 days',
  'dlp.period.last28Days': 'Last 28 days',
  'dlp.period.last90Days': 'Last 90 days',
  'dlp.period.last180Days': 'Last 180 days',
  'dlp.loading': 'Loading DLP data...',
  'dlp.error.loadData': 'Failed to load DLP data.',

  // Server-authored availability reasons

  'dlp.availability.reason.copilotImportOff': "The Microsoft 365 Copilot audit import is switched off, so there is no per-agent DLP data. Enable 'Copilot interactions' in the installer. This does NOT need the DLP permission - Copilot DLP blocks are carried on the Copilot interaction records themselves.",
  'dlp.availability.reason.tenantImportOff': "The Data Loss Prevention import (DLP.All) is switched off, so tenant-wide policy activity for Exchange, SharePoint/OneDrive and Endpoint is not shown. Enable 'DLP policy events' in the installer and grant the runtime app the 'ActivityFeed.ReadDlp' application permission, which is separate from 'ActivityFeed.Read' and needs its own admin consent.",

  // Common table columns and empty states
  'dlp.table.empty': 'Nothing in this period.',
  'dlp.column.agent': 'Agent',
  'dlp.column.user': 'User',
  'dlp.column.policy': 'Policy',
  'dlp.column.sensitivityLabel': 'Sensitivity label',
  'dlp.column.blocked': 'Blocked',
  'dlp.column.auditedOnly': 'Audited only',
  'dlp.column.users': 'Users',
  'dlp.policyDrilldown.title': 'Policies affecting {name}',
  'dlp.policyDrilldown.thisAgent': 'this agent',

  // Summary KPIs
  'dlp.kpi.blocked.label': 'Blocked',
  'dlp.kpi.blocked.hint': 'Copilot was denied content',
  'dlp.kpi.auditedOnly.label': 'Audited only',
  'dlp.kpi.auditedOnly.hint': 'Policy matched, nothing withheld',
  'dlp.kpi.peopleAffected.label': 'People affected',
  'dlp.kpi.peopleAffected.hint': 'Distinct users blocked',
  'dlp.kpi.agentsAffected.label': 'Agents affected',
  'dlp.kpi.agentsAffected.hint': 'Distinct agents blocked',
  'dlp.kpi.policiesInvolved.label': 'Policies involved',
  'dlp.kpi.policiesInvolved.hint': 'Distinct DLP policies',
  'dlp.noCopilotActivity': 'No DLP policy affected Copilot in this period. If you expected activity, remember that a policy change can take up to four hours to reach Copilot, and that the policy must target the "Microsoft 365 Copilot and Copilot Chat" location.',

  // Copilot impact tables
  'dlp.affected.title': 'Who and what is affected',
  'dlp.affected.agents.title': 'Agents',
  'dlp.affected.agents.description': 'Copilot agents whose access to content was affected by a DLP policy. This is the only view that can attribute a DLP block to a specific agent. Select an agent to see which policies affected it.',
  'dlp.affected.people.title': 'People',
  'dlp.affected.people.description': 'Users whose Copilot requests were affected most often. Select a person to see which policies affected them.',
  'dlp.affected.policies.title': 'Policies',
  'dlp.affected.policies.description': "The DLP policies responsible. A policy with blocks in the 'Audited only' column is matching without withholding anything - typically because its rules are in simulation mode.",
  'dlp.affected.sensitivityLabels.title': 'Sensitivity labels',
  'dlp.affected.sensitivityLabels.description': "Labels on the content Copilot was stopped from using. The most common Copilot DLP policy shape is 'prevent Copilot processing content with label X', so this is usually the explanation.",

  // Copilot governance signals (#648)
  'dlp.governance.title': 'Copilot governance signals',
  'dlp.governance.description': 'Prompt-safety and grounding signals Microsoft records on each Copilot interaction in this period. A rate counts only the interactions on which Microsoft reported the signal: a missing flag means it was not reported, not that the interaction was safe.',
  'dlp.governance.loading': 'Loading governance signals...',
  'dlp.governance.error': 'Failed to load the governance signals.',
  'dlp.governance.importOff': 'The Copilot audit import is switched off, so there are no governance signals to show.',
  'dlp.governance.noInteractions': 'No Copilot interactions in this period.',
  'dlp.governance.rate.value': '{rate} per 10,000',
  'dlp.governance.rate.notReported': 'Not reported',
  'dlp.governance.rate.fraction': '{flagged} of the {reported} interactions where Microsoft reported the signal',
  'dlp.governance.rate.coverage': 'Reported on {reported} of {interactions} interactions in this period ({share}).',
  'dlp.governance.rate.noneReported': 'Microsoft reported this signal on none of the {interactions} interactions in this period, so there is no rate.',
  'dlp.governance.jailbreak.title': 'Jailbreak attempts',
  'dlp.governance.jailbreak.hint': 'Interactions with a prompt Microsoft flagged as an attempt to get round Copilot’s safeguards.',
  'dlp.governance.xpia.title': 'Cross-prompt injection (XPIA)',
  'dlp.governance.xpia.hint': 'Interactions in which Microsoft detected instructions planted in content Copilot used.',
  'dlp.governance.labels.title': 'Labelled content used',
  'dlp.governance.labels.fraction': '{labelled} of the {resources} resources Copilot used carried a sensitivity label',
  'dlp.governance.labels.coverage': 'Copilot used content in {withResources} of {interactions} interactions. A resource counts once for each interaction that used it.',
  'dlp.governance.labels.none': 'No content used',
  'dlp.governance.agents.title': 'Agents with DLP blocks',
  'dlp.governance.agents.hint': 'Distinct agents a DLP policy blocked in this period. The most blocked:',
  'dlp.governance.agents.blocked': 'Blocked: {count}',
  'dlp.governance.agents.none': 'No agent was blocked in this period.',
  'dlp.governance.agents.pointer': 'Whether an agent is still worth keeping is shown in {link}, where every agent has a verdict such as Retire or Review.',
  'dlp.governance.agents.pointerLink': 'Copilot Adoption > Agents',
  'dlp.governance.models.title': 'AI models',
  'dlp.governance.models.description': 'The models Microsoft named, each as a share of all {interactions} interactions in this period. A model was named on {withModel} of them: Microsoft does not name the model for most Microsoft 365 Copilot interactions, so the shares do not add up to 100%. DEEP_LEO is the model Microsoft reports when Copilot used deep reasoning.',
  'dlp.governance.models.empty': 'Microsoft named no model in this period.',
  'dlp.governance.plugins.title': 'System plugins',
  'dlp.governance.plugins.description': 'The plugins Copilot invoked, each as a share of all {interactions} interactions in this period. Copilot invoked a plugin on {withPlugin} of them. BingWebSearch means Copilot searched the public web.',
  'dlp.governance.plugins.empty': 'Copilot invoked no plugin in this period.',
  'dlp.governance.column.model': 'Model',
  'dlp.governance.column.plugin': 'Plugin',
  'dlp.governance.column.interactions': 'Interactions',
  'dlp.governance.column.share': 'Share of interactions',

  // Tenant-wide DLP activity
  'dlp.tenant.title': 'Tenant-wide DLP activity',
  'dlp.tenant.description': 'DLP policy activity across Exchange, SharePoint/OneDrive and endpoint devices, from the separate DLP audit feed. These records identify the person but never the Copilot agent, so they are reported separately and are not combined with the Copilot figures above.',
  'dlp.tenant.importOff': 'The DLP import is switched off, so there is nothing to show here.',
  'dlp.tenant.blocked.hint': 'Across all workloads',
  'dlp.tenant.auditedOnly.hint': 'Matched, not enforced',
  'dlp.tenant.policies.title': 'Policies (tenant-wide)',
  'dlp.tenant.policies.description': 'Policies firing across the tenant, from the DLP audit feed.',
  // The same section while a portal administrator's filter narrows it, when it is no longer tenant-wide.
  'dlp.tenant.titleFiltered': 'DLP activity across all workloads',
  'dlp.tenant.policies.titleFiltered': 'Policies (all workloads)',
  'dlp.tenant.policies.descriptionFiltered': 'Policies firing for the people the administrator’s filter covers, from the DLP audit feed.',
} as const;

export default dlp;
