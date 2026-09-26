/**
 * English text for the Insights overview page and its tiles.
 *
 * Every key here must have a Spanish counterpart in `../es/overview.ts`; the type of that module
 * makes a missing one a build failure.
 */
export const overview = {

  // Overview data-count tiles.
  //
  // The figures come from api/SystemStatus, which authors its own English 'name' and 'hint'
  // (SystemStatusAPIController.DataCountDefinition). Server-authored display text cannot be
  // translated where it is written, so the SPA maps the model's STABLE 'key' - the one it already
  // uses to choose an icon - to these entries instead, and only falls back to the server's English
  // for a figure this build has not heard of.
  'overview.dataCount.users.name': 'Users',
  'overview.dataCount.users.hint': 'People discovered by any import',
  'overview.dataCount.auditEvents.name': 'Audit events',
  'overview.dataCount.auditEvents.hint': 'Activity from the unified audit log',
  'overview.dataCount.copilotInteractions.name': 'Copilot interactions',
  'overview.dataCount.copilotInteractions.hint': 'Copilot chats from the audit feed',
  'overview.dataCount.copilotAiInteractions.name': 'Copilot AI interactions',
  'overview.dataCount.copilotAiInteractions.hint': 'From Graph AI interaction history',
  'overview.dataCount.webHits.name': 'Web page hits',
  'overview.dataCount.webHits.hint': 'Page views from the SharePoint tracker',
  'overview.dataCount.trackedUrls.name': 'Tracked URLs',
  'overview.dataCount.trackedUrls.hint': 'Distinct pages seen by the tracker',
  'overview.dataCount.sharePointSites.name': 'SharePoint sites',
  'overview.dataCount.sharePointSites.hint': 'Sites seen in activity or web traffic',
  'overview.dataCount.sentEmails.name': 'Sent emails',
  'overview.dataCount.sentEmails.hint': 'Mail sent, imported from Graph',
  'overview.dataCount.teams.name': 'Teams discovered',
  'overview.dataCount.teams.hint': 'Teams found in the tenant',
  'overview.dataCount.teamsTracked.name': 'Teams with deep tracking',
  'overview.dataCount.teamsTracked.hint': 'Teams that granted channel-level analytics',
  'overview.dataCount.teamsCalls.name': 'Teams calls',
  'overview.dataCount.teamsCalls.hint': 'Call records from Graph',
  'overview.dataCount.powerApps.name': 'Power Apps',
  'overview.dataCount.powerApps.hint': 'Apps seen in Power Platform activity',
  'overview.dataCount.dlpMatches.name': 'DLP rule matches',
  'overview.dataCount.dlpMatches.hint': 'Purview DLP rules triggered',
  'overview.dataCount.licenceTypes.name': 'Licence SKUs',
  'overview.dataCount.licenceTypes.hint': 'Licence types assigned in the tenant',
  'overview.dataCount.copilotStudioCreditDays.name': 'Copilot Studio credit days',
  'overview.dataCount.copilotStudioCreditDays.hint': 'Billed agent credits, per agent per day',
  'overview.dataCount.azureCostDays.name': 'Azure cost days',
  'overview.dataCount.azureCostDays.hint': 'Daily Azure spend from Cost Management',
  // Overview page
  'overview.page.loadError': 'Failed to load the data overview.',
  'overview.page.unknownError': 'unknown error',
  'overview.page.loading': 'Loading data overview...',
  'overview.page.noDataAvailable': 'No data overview available.',
  'overview.page.title': 'Overview',
  'overview.page.lede': 'Microsoft 365 Advanced Analytics collects activity from across your tenant into your own database. Here is what it holds, whether it is still arriving, and where to go next.',
  'overview.page.yourDataHeading': 'Your data',
  'overview.page.importSettingsKnown': 'Only the workloads switched on for this deployment are shown.',
  'overview.page.importSettingsUnknown': "Import settings couldn't be read, so every figure is shown.",
  'overview.page.noImportsPrefix': 'No imports are switched on for this deployment, so there is nothing to summarise yet. Enable them in the installer, then check',
  'overview.page.serviceHealthLink': 'Administration \u2192 Service health',
  'overview.page.zeroFiguresPrefix': 'Every figure is still zero. That is normal for the first few hours after an install - if it persists, check',
  'overview.page.zeroFiguresSuffix': 'to see whether the imports are running.',
  'overview.page.importsSwitchedOn': 'Imports switched on:',
  'overview.enabledImport.activityLog': 'Activity/audit',
  'overview.enabledImport.copilot': 'Copilot',
  'overview.enabledImport.copilotInteractionHistory': 'Copilot AI interaction history (tenant-wide unless scoped)',
  'overview.enabledImport.powerPlatform': 'Power Platform',
  'overview.enabledImport.dlpPolicyEvents': 'DLP policy events',
  'overview.enabledImport.userMetadata': 'User metadata',
  'overview.enabledImport.usageReports': 'Usage reports',
  'overview.enabledImport.copilotUsageReportsGraph': 'Copilot usage reports (Graph)',
  'overview.enabledImport.teams': 'Teams',
  'overview.enabledImport.webTraffic': 'Web traffic',
  'overview.enabledImport.sentEmails': 'Sent emails',
  'overview.enabledImport.teamsCalls': 'Teams calls',
  'overview.enabledImport.copilotStudioCredits': 'Copilot Studio credits (billed)',
  'overview.enabledImport.azureCosts': 'Azure costs (Cost Management)',
  'overview.page.whereToNextHeading': 'Where to next',
  'overview.page.whereToNextNote': 'The parts of the portal that apply to this deployment.',

  // Health snapshot
  'overview.healthSnapshot.title': 'System health',
  'overview.healthSnapshot.checking': 'Checking...',
  'overview.healthSnapshot.openServiceHealth': 'Open Service health',
  'overview.healthSnapshot.summaryError': "The health summary couldn't be read ({error}). The figures above come straight from the database and are unaffected.",
  'overview.healthSnapshot.newest': 'Newest {name}',
  'overview.healthSnapshot.auditEventName': 'audit event',
  'overview.healthSnapshot.webPageHitName': 'web page hit',
  'overview.healthSnapshot.auditEventsLast24h': 'Audit events in the last 24h',
  'overview.healthSnapshot.webPageHitsLast24h': 'Web page hits in the last 24h',
  'overview.healthSnapshot.recentVolumeWarning': 'The freshness and 24h volume scan didn\'t finish on this database, so those figures show "-". This is expected on very large tenants.',
  'overview.healthSnapshot.checkingFreshness': 'Checking how recent the imported data is...',
  'overview.status.degraded': 'Degraded',
  'overview.status.healthy': 'Healthy',
  'overview.status.unknown': 'Unknown',
  'overview.status.unhealthy': 'Unhealthy',

  // Where to next
  'overview.whereToNext.reports.title': 'Reports',
  'overview.whereToNext.reports.blurb': 'Chart activity, Copilot use and page traffic over time, sliced by department or site.',
  'overview.whereToNext.copilotAdoption.title': 'Copilot Adoption',
  'overview.whereToNext.copilotAdoption.blurb': 'Who is getting value from their Copilot licence, and who has stopped using it.',
  'overview.whereToNext.licenceActivity.title': 'Licence activity',
  'overview.whereToNext.licenceActivity.blurb': 'Licences you are paying for against the activity actually seen, service by service.',
  'overview.whereToNext.agentCosts.title': 'Agent costs',
  'overview.whereToNext.agentCosts.blurb': 'Billed Copilot Studio credits and Azure spend, attributed per agent.',
  'overview.whereToNext.dlp.title': 'DLP impact',
  'overview.whereToNext.dlp.blurb': 'Where Purview data-loss-prevention policies are blocking people and agents.',
  'overview.whereToNext.teamsPermissions.title': 'Teams permissions',
  'overview.whereToNext.teamsPermissions.blurb': 'Turn on channel-level Teams analytics, one team at a time.',
  'overview.whereToNext.health.title': 'Service health',
  'overview.whereToNext.health.blurb': 'Import liveness, exceptions, component health and database freshness.',
  'overview.whereToNext.configuration.title': 'Service configuration',
  'overview.whereToNext.configuration.blurb': 'Which imports are switched on, the database schema version and connected services.',
} as const;

export default overview;
