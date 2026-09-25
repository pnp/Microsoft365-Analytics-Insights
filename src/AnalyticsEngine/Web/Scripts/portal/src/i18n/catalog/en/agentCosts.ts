/**
 * English text for the Agent costs page.
 *
 * Every key here must have a Spanish counterpart in `../es/agentCosts.ts`; the type of that module
 * makes a missing one a build failure.
 */
export const agentCosts = {
  // Shared states and units
  'agentCosts.state.notReported': 'Not reported',
  'agentCosts.unit.credits': 'credits',

  // Harness values
  'agentCosts.harness.standardOrCopilotChat': 'Standard / Copilot Chat',
  'agentCosts.harness.unrecognisedFeature': 'Unrecognised feature',
  'agentCosts.harness.noFeatureReported': 'No feature reported',

  // Dimensions
  'agentCosts.dimension.credit.agent.label': 'Agent',
  'agentCosts.dimension.credit.agent.hint': 'Which agent the credits were billed against.',
  'agentCosts.dimension.credit.environment.label': 'Environment',
  'agentCosts.dimension.credit.environment.hint': 'The Power Platform environment the agent lives in.',
  'agentCosts.dimension.credit.harness.label': 'Harness',
  'agentCosts.dimension.credit.harness.hint': 'Standard / Copilot Chat or GitHub Copilot. Inferred from the billing feature, because Microsoft does not report it directly.',
  'agentCosts.dimension.credit.feature.label': 'Billing feature',
  'agentCosts.dimension.credit.feature.hint': 'What was charged for - a generative answer, tenant graph grounding, an agent action.',
  'agentCosts.dimension.azure.meter': 'Meter',
  'agentCosts.dimension.azure.service': 'Service',
  'agentCosts.dimension.azure.category': 'Meter category',
  'agentCosts.dimension.azure.resource': 'Resource',
  'agentCosts.dimension.azure.resourceGroup': 'Resource group',
  'agentCosts.dimension.azure.subscription': 'Subscription',
  'agentCosts.dimension.azure.tag': 'Tag value',

  // Page chrome
  'agentCosts.title': 'Agent costs',
  'agentCosts.intro': 'What Microsoft charged for your Copilot Studio agents, broken down as far as the billing data allows - by agent, environment, harness and billing feature, plus Azure spend for the subscriptions you import. Breakdowns only appear once Microsoft actually reports the dimension behind them.',
  'agentCosts.loading': 'Loading agent costs...',
  'agentCosts.window.last7Days': 'Last 7 days',
  'agentCosts.window.last30Days': 'Last 30 days',
  'agentCosts.window.last60Days': 'Last 60 days',
  'agentCosts.window.last90Days': 'Last 90 days',
  'agentCosts.window.last180Days': 'Last 180 days',

  // Errors and notices
  'agentCosts.error.summary': 'Could not load the agent cost figures.',
  'agentCosts.error.breakdown': 'Could not load the credit breakdown. Try changing the period or refreshing.',
  'agentCosts.error.detail': 'Could not load the billed lines. Try changing the period or refreshing.',
  'agentCosts.error.azure': 'Could not load the Azure cost breakdown. Try changing the period or refreshing.',
  'agentCosts.error.export': 'Could not export the billed lines.',
  'agentCosts.notice.exportedTruncated': 'Exported the first {rows} of {totalRows} billed lines. Narrow the filters or shorten the period to export the rest.',
  'agentCosts.notice.exportedRows': 'Exported {rows} billed line(s).',
  'agentCosts.warning.copilotStudioImportFailing': 'Copilot Studio credit import is failing.',
  'agentCosts.warning.azureCostImportFailing': 'Azure cost import is failing.',

  // Server-authored availability messages

  'agentCosts.availability.message.noImports': 'Neither agent cost import is switched on. Ask whoever installed the product to tick "Copilot Studio credits" and/or "Azure costs" in the installer.',
  'agentCosts.availability.message.copilotImportFailing': 'The Copilot Studio credit import is switched on but is failing. Check the app registration holds a Power Platform role at tenant scope - but if it already does, this is most likely Microsoft refusing application-only access to the licensing API rather than anything left undone here, in which case the import cannot currently succeed and is best switched off. The error was: {error}',
  'agentCosts.availability.message.copilotNoUsage': 'The Copilot Studio credit import ran successfully but found no billed agent usage. That is the expected result for a tenant with no Copilot Studio agents, or none that have consumed credits yet.',
  'agentCosts.availability.message.copilotNotStoredYet': 'The Copilot Studio credit import is switched on but has not stored anything yet. It runs once a day, so allow a cycle before expecting figures.',
  'agentCosts.availability.message.azureImportFailing': 'The Azure cost import is switched on but is failing: {error}',
  'agentCosts.availability.message.azureNoSpend': 'The Azure cost import ran successfully but the query matched no spend. Check the meter filter against a cost export from the same scope - a filter that matches nothing looks exactly like having no spend.',
  'agentCosts.availability.message.azureNotStoredYet': 'The Azure cost import is switched on but has not stored anything yet. Check that a scope is set and allow a cycle before expecting figures.',
  'agentCosts.availability.message.perUserNotUpdating': 'The per-person Copilot Studio figures are not updating: {error} The per-agent figures above are unaffected, but anything shown per person may be out of date.',
  'agentCosts.availability.message.capacityNotUpdating': 'The Copilot Credits capacity snapshot is not updating: {error} Any remaining-capacity figure shown may be out of date.',
  'agentCosts.availability.message.creditEndpointMismatch': 'Copilot Studio spend is reported by Microsoft two ways: per agent, and per person. They come from different Microsoft endpoints rather than one being a breakdown of the other, so their totals will not always match exactly. The per-agent user counts are how many different people used an agent - those cannot be added together, because the same person appears under every agent they used.',
  'agentCosts.availability.message.azureNoPeople': 'Azure costs cannot be attributed to individual people. Azure bills by resource, and no Azure billing report - including the full cost export - records who caused a charge.',
  'agentCosts.availability.message.azureEstimates': 'Azure costs are estimates until Microsoft closes the billing period, which can take a few days after month end. Figures marked as estimates can still change.',

  // Spend summary
  'agentCosts.spend.title': 'Spend in the selected period',
  'agentCosts.spend.importedUtc': 'Imported {when} UTC',
  'agentCosts.kpi.creditsBilled': 'Copilot Credits billed',
  'agentCosts.kpi.creditsNotCharged': 'Credits not charged',
  'agentCosts.kpi.creditsNotCharged.hint': 'Used but covered by an allowance',
  'agentCosts.kpi.agentsWithSpend': 'Agents with spend',
  'agentCosts.kpi.environments': 'Environments',
  'agentCosts.kpi.busiestSlice': 'Busiest slice',
  'agentCosts.kpi.busiestSlice.hint': 'Most people on a single billed line - never a tenant user total',
  'agentCosts.kpi.unclassifiedHarness': 'Unclassified harness',
  'agentCosts.kpi.unclassifiedHarness.hint': 'Microsoft reported a feature we do not recognise',
  'agentCosts.capacity.availableNow': 'Credits available now',
  'agentCosts.capacity.usedOfEntitled': '{consumed} of {entitled} used',
  'agentCosts.capacity.status': 'Capacity status',
  'agentCosts.capacity.status.monthToDate': 'Month to date',
  'agentCosts.capacity.status.withinCapacity': 'Within capacity',
  'agentCosts.capacity.status.overage': 'Overage',
  'agentCosts.capacity.asAt': 'As at {day}',
  'agentCosts.capacity.payAsYouGo': 'Pay-as-you-go credits',
  'agentCosts.capacity.payAsYouGo.hint': 'Billed on top of pre-purchased capacity',
  'agentCosts.azureSpend.title': 'Azure spend',
  'agentCosts.azureSpend.meteredUnitsBilled': '{quantity} metered units billed',
  'agentCosts.azureSpend.includesEstimates': 'Includes estimates that can still change',

  // Trend
  'agentCosts.trend.title': 'Credits per day',
  'agentCosts.trend.empty': 'No credit usage in this period.',
  'agentCosts.trend.barTitle': '{day}: {credits} credits',
  'agentCosts.trend.caption': '{from} to {to}, peak {peak} credits in a day',

  // Filters
  'agentCosts.filters.title': 'Narrow the per-agent figures',
  'agentCosts.filter.agent': 'Agent',
  'agentCosts.filter.agent.all': 'All agents',
  'agentCosts.filter.environment': 'Environment',
  'agentCosts.filter.environment.all': 'All environments',
  'agentCosts.filter.harness': 'Harness',
  'agentCosts.filter.harness.all': 'All harnesses',
  'agentCosts.filter.billingFeature': 'Billing feature',
  'agentCosts.filter.billingFeature.all': 'All features',
  'agentCosts.filter.searchAgentName': 'Search agent name',
  'agentCosts.filter.searchAgentName.placeholder': 'Agent name or ID',

  // Credit breakdown
  'agentCosts.breakdown.title': 'Where the credits went',
  'agentCosts.breakdown.hiddenRows': 'Showing the top {count} of this period\u0027s spend. Shares are of the full {credits} credits, so they will not add up to 100%.',

  // Billed-line detail
  'agentCosts.detail.title': 'Every billed line',
  'agentCosts.detail.description': 'One row per day, agent and billing dimension - the most detailed view Microsoft\u0027s billing data allows. Click a column heading to sort.',
  'agentCosts.detail.exportPage': 'Export this page',
  'agentCosts.detail.exporting': 'Exporting...',
  'agentCosts.detail.exportAllRows': 'Export all {rows} rows',
  'agentCosts.detail.empty': 'No billed lines match the current filters.',
  'agentCosts.export.pageFilename': 'agent-credits-{from}-to-{to}-page{page}.csv',
  'agentCosts.export.filteredFilename': 'agent-credits-{from}-to-{to}-filtered.csv',

  // Tables and paging
  'agentCosts.table.value': 'Value',
  'agentCosts.table.creditsBilled': 'Credits billed',
  'agentCosts.table.share': 'Share',
  'agentCosts.table.notCharged': 'Not charged',
  'agentCosts.table.daysActive': 'Days active',
  'agentCosts.table.busiestSlicePeople': 'Busiest slice (people)',
  'agentCosts.table.day': 'Day',
  'agentCosts.table.agent': 'Agent',
  'agentCosts.table.environment': 'Environment',
  'agentCosts.table.harness': 'Harness',
  'agentCosts.table.billingFeature': 'Billing feature',
  'agentCosts.table.credits': 'Credits',
  'agentCosts.table.people': 'People',
  'agentCosts.table.person': 'Person',
  'agentCosts.table.cost': 'Cost',
  'agentCosts.table.quantity': 'Quantity',
  'agentCosts.table.final': 'Final?',
  'agentCosts.pager.previous': 'Previous',
  'agentCosts.pager.next': 'Next',
  'agentCosts.pager.pageOfBilledLines': 'Page {page} of {totalPages} \u00b7 {rows} billed lines',

  // Per-person spend
  'agentCosts.users.title': 'Who is spending the credits',
  'agentCosts.users.description': 'Billed Copilot Studio credits per person, reported by Microsoft. Nothing here is estimated or shared out - but it comes from a different Microsoft report than the per-agent figures above, so the two totals will not always match exactly. {strong} - Microsoft\u0027s per-person report does not carry those dimensions, so this panel always shows everyone (narrowed only by environment). Azure spend is not included: Azure bills by resource and never records who caused a charge.',
  'agentCosts.users.descriptionStrong': 'The agent, feature, model, tool and channel filters do not apply here',
  'agentCosts.users.empty.importOff': 'The Copilot Studio credit import is switched off.',
  'agentCosts.users.empty.noUsage': 'No per-person credit usage in this period.',
  'agentCosts.users.empty.noFigures': 'No per-person figures yet. Microsoft added these to the Power Platform licensing API in July 2026, so a tenant whose API does not offer them will only ever show the per-agent view above.',
  'agentCosts.users.unresolvedUser': 'Unresolved user',
  'agentCosts.users.shareCaption': 'Shares are of the {credits} credits shown here, which is the top {people} people - not necessarily every person who used an agent.',

  // Azure spend
  'agentCosts.azureSpend.description': 'Daily costs from Microsoft Cost Management for the scopes this deployment is configured to read. Only the date range applies here - the Copilot filters above do not. Azure bills by resource, so these figures cannot be attributed to individual people.',
  'agentCosts.azureSpend.empty.noCosts': 'No Azure costs stored for this period.',
  'agentCosts.azureSpend.empty.importOff': 'The Azure cost import is switched off.',
  'agentCosts.azureSpend.estimate': 'Estimate - can still change',
  'agentCosts.azureSpend.final': 'Final - billing period closed',
} as const;

export default agentCosts;
