/**
 * English text for the Reports page.
 *
 * Every key here must have a Spanish counterpart in `../es/reports.ts`; the type of that module
 * makes a missing one a build failure.
 */
export const reports = {
  // Page header and introduction
  'reports.title': 'Reports',
  'reports.intro.licenceActivity': 'A quick, built-in view of how your Microsoft 365 usage is trending. The report charts appear only when their data is being imported. For licence assignments and activity, open {link} in the Insights navigation.',
  'reports.intro.licenceActivityLink': 'Licence activity',
  'reports.empty.noImports': 'No built-in report charts are available yet because no data imports are enabled. Enable one or more imports (Copilot, usage reports, SharePoint activity, website traffic, Teams calls or emails) in the installer to see them.',
  'reports.loading.reports': 'Loading reports...',
  'reports.error.loadAreas': 'Failed to load report areas.',

  // Report areas
  'reports.area.copilot.label': 'Copilot',
  'reports.area.copilot.blurb': 'Microsoft 365 Copilot adoption and usage.',
  'reports.area.copilotAgents.label': 'Copilot agents',
  'reports.area.copilotAgents.blurb': 'Copilot agent popularity and usage.',
  'reports.area.usage.label': 'Microsoft 365 usage',
  'reports.area.usage.blurb': 'Weekly active users across Microsoft 365 workloads.',
  'reports.area.officeApps.label': 'Office apps',
  'reports.area.officeApps.blurb': 'Which Office apps people use, on which platforms, in which departments, and how far Copilot has reached them. Every figure counts people, not actions - the Microsoft report behind it records who used an app, never how much.',
  'reports.area.spoAudit.label': 'SharePoint & OneDrive',
  'reports.area.spoAudit.blurb': 'File activity from the audit log.',
  'reports.area.webTraffic.label': 'Website traffic',
  'reports.area.webTraffic.blurb': 'Page views and visitors from the page tracker.',
  'reports.area.calls.label': 'Teams calls',
  'reports.area.calls.blurb': 'Teams call volume and duration.',
  'reports.area.emails.label': 'Emails',
  'reports.area.emails.blurb': 'Sent email volume.',

  // Period controls
  'reports.period.label': 'Period',
  'reports.period.ariaLabel': 'Reporting period',
  'reports.period.lastMonth': 'Last month',
  'reports.period.last3Months': 'Last 3 months',
  'reports.period.last6Months': 'Last 6 months',

  // Copilot agent filters
  'reports.topAgents.label': 'Top agents',
  'reports.topAgents.ariaLabel': 'Number of top Copilot agents',
  'reports.topAgents.filterPlaceholder': 'Filter by agent name',
  'reports.topAgents.filterAriaLabel': 'Filter Copilot agents by name',

  // Report area view
  'reports.loading.charts': 'Loading charts...',
  'reports.error.loadReport': 'Failed to load the report.',
  'reports.areaHeader.usageLag': '{blurb} Weeks from {from}. Usage reports arrive a few days late, so the latest weeks appear once their report does.',
  'reports.areaHeader.toNow': '{blurb} Weeks from {from} to now.',
  'reports.callsInfo.teamsExplorer': 'This is the headline call volume only. For meeting size and length, time-of-day patterns, modalities, organiser concentration and call quality, see {link}.',
  'reports.callsInfo.teamsExplorerLink': 'Teams Explorer',
  'reports.clampedWindow': 'Showing the last {current} months rather than {selected}. This report reads one record per person per day, so a longer window cannot be built in time on a large tenant.',
  'reports.promptInsights.notConfigured': 'Prompt insights (common prompt phrases, weekly prompt sentiment and prompt language) are not shown because Azure AI Language is not configured. Those three charts are built from cognitive enrichment of Copilot prompt history, so without it they would always be empty. Add a Cognitive Services endpoint and key in the installer, then re-run the Copilot interaction history import, to enable them.',
  'reports.chart.sqlTitle': 'SQL behind this chart',
  'reports.chart.loadError': "Couldn't load this chart: {error}",
  'reports.chart.noData': 'No data for this period.',
} as const;

export default reports;
