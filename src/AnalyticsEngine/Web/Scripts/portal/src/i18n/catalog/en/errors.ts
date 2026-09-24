/**
 * English text for the messages the API layer raises and the pages put on screen.
 *
 * These live in `src/api/*.ts`, which throws `Error`s whose `message` a page renders directly.
 * They are outside React, so they are resolved with `translateActive()` from `src/i18n/runtime`
 * rather than `useT()`.
 *
 * Worth translating even though they are errors - arguably especially because they are errors.
 * An English failure message in the middle of a Spanish page is what a reader sees at the moment
 * something has already gone wrong, and it is the point at which they are least able to guess.
 *
 * Every key here must have a Spanish counterpart in `../es/errors.ts`; the type of that module
 * makes a missing one a build failure.
 */
export const errors = {
  // Shared API/session errors
  'errors.http.sessionExpired': 'Your session has expired. Reload the page to sign in again.',
  'errors.userLookup.requestFailed': 'Request failed ({status})',

  // Agent costs API
  'errors.agentCosts.availabilityFailed': "Couldn't load the agent cost availability ({status}).",
  'errors.agentCosts.summaryFailed': "Couldn't load the agent cost summary ({status}).",
  'errors.agentCosts.trendFailed': "Couldn't load the daily credit trend ({status}).",
  'errors.agentCosts.breakdownFailed': "Couldn't load the credit breakdown ({status}).",
  'errors.agentCosts.detailFailed': "Couldn't load the detailed credit rows ({status}).",
  'errors.agentCosts.azureBreakdownFailed': "Couldn't load the Azure cost breakdown ({status}).",
  'errors.agentCosts.topUsersFailed': "Couldn't load the per-user credit consumption ({status}).",
  'errors.agentCosts.filtersFailed': "Couldn't load the available filters ({status}).",

  // Copilot adoption API
  'errors.copilotAdoption.analysisStillRunning': "The Copilot adoption analysis is taking longer than expected and hasn't finished yet. It is still running on the server - reload the page in a few minutes.",
  'errors.copilotAdoption.analysisStillRunningWithReference': "The Copilot adoption analysis is taking longer than expected and hasn't finished yet. It is still running on the server - reload the page in a few minutes. If this keeps happening, include reference {runId} when you report it.",
  'errors.copilotAdoption.availabilityFailed': "Couldn't load the Copilot adoption availability ({status}).",
  'errors.copilotAdoption.summaryFailed': "Couldn't load the Copilot adoption summary ({status}).",
  'errors.copilotAdoption.filtersFailed': "Couldn't load the Copilot adoption filters ({status}).",
  'errors.copilotAdoption.licensedUsersFailed': "Couldn't load the licensed Copilot users ({status}).",
  'errors.copilotAdoption.opportunitiesFailed': "Couldn't load the Copilot licence opportunities ({status}).",
  'errors.copilotAdoption.coworkFailed': "Couldn't load the Cowork readiness list ({status}).",
  'errors.copilotAdoption.queriesFailed': "Couldn't load the Copilot adoption queries ({status}).",

  // DLP API
  'errors.dlp.availabilityFailed': "Couldn't load DLP availability ({status}).",
  'errors.dlp.summaryFailed': "Couldn't load DLP summary ({status}).",

  // Health API
  'errors.health.summaryFailed': "Couldn't load system health ({status}).",
  'errors.health.dataFailed': "Couldn't load data overview ({status}).",
  'errors.health.livenessFailed': "Couldn't load import liveness ({status}).",
  'errors.health.exceptionsFailed': "Couldn't load exceptions ({status}).",
  'errors.health.componentsFailed': "Couldn't load component health ({status}).",
  'errors.health.configFailed': "Couldn't load configuration ({status}).",

  // Install/profiling/status APIs
  'errors.installLog.loadFailed': "Couldn't load the install log ({status}).",
  'errors.profiling.statusFailed': "Couldn't load profiling status ({status}).",
  'errors.profiling.traceLogsFailed': "Couldn't load profiling trace logs ({status}).",
  'errors.systemStatus.loadFailed': "Couldn't load system status ({status}).",
  'errors.updateCheck.failed': "Couldn't check for updates ({status}).",

  // Licence activity API
  'errors.licenceActivity.figuresExpired': 'These figures are no longer being held. Refresh the report to bring back an up-to-date set.',
  'errors.licenceActivity.userDetailsImportOff': 'Licence activity is not available: the user details import is switched off on this deployment.',
  'errors.licenceActivity.badRequest': 'That request was rejected. Check the selected dates and filters.',
  'errors.licenceActivity.availabilityBusy': 'The server is busy or could not prepare the licence activity availability. Try again in a moment.',
  'errors.licenceActivity.availabilityForbidden': 'You do not have permission to view the licence activity availability.',
  'errors.licenceActivity.availabilityFailed': "Couldn't load the licence activity availability ({status}).",
  'errors.licenceActivity.overviewBusy': 'The server is busy or could not prepare the licence activity overview. Try again in a moment.',
  'errors.licenceActivity.overviewForbidden': 'You do not have permission to view the licence activity overview.',
  'errors.licenceActivity.overviewFailed': "Couldn't load the licence activity overview ({status}).",
  'errors.licenceActivity.usersBusy': 'The server is busy or could not prepare the licensed users. Try again in a moment.',
  'errors.licenceActivity.usersForbidden': 'You do not have permission to view the licensed users.',
  'errors.licenceActivity.usersFailed': "Couldn't load the licensed users ({status}).",
  'errors.licenceActivity.excelExportBusy': 'The server is busy or could not prepare the Excel export. Try again in a moment.',
  'errors.licenceActivity.excelExportForbidden': 'You do not have permission to view the Excel export.',
  'errors.licenceActivity.excelExportFailed': "Couldn't load the Excel export ({status}).",

  // Reports API
  'errors.reports.areasFailed': "Couldn't load report areas ({status}).",
  'errors.reports.copilotReportFailed': "Couldn't load the copilot report ({status}).",
  'errors.reports.copilotAgentsReportFailed': "Couldn't load the copilot-agents report ({status}).",
  'errors.reports.usageReportFailed': "Couldn't load the usage report ({status}).",
  'errors.reports.officeAppsReportFailed': "Couldn't load the office-apps report ({status}).",
  'errors.reports.spoAuditReportFailed': "Couldn't load the spo-audit report ({status}).",
  'errors.reports.webTrafficReportFailed': "Couldn't load the web-traffic report ({status}).",
  'errors.reports.callsReportFailed': "Couldn't load the calls report ({status}).",
  'errors.reports.emailsReportFailed': "Couldn't load the emails report ({status}).",

  // Teams Explorer API
  'errors.teamsExplorer.dataSourcesFailed': "Couldn't load the Teams data sources ({status}).",
  'errors.teamsExplorer.overviewFailed': "Couldn't load the Teams overview ({status}).",
  'errors.teamsExplorer.adoptionFailed': "Couldn't load Teams adoption ({status}).",
  'errors.teamsExplorer.meetingsFailed': "Couldn't load Teams meetings and calls ({status}).",
  'errors.teamsExplorer.collaborationFailed': "Couldn't load teams and channels ({status}).",
  'errors.teamsExplorer.conversationsFailed': "Couldn't load conversation insights ({status}).",
  'errors.teamsExplorer.peopleFailed': "Couldn't load Teams people ({status}).",
  'errors.teamsExplorer.exportPeopleFailed': "Couldn't export people ({status}).",
  'errors.teamsExplorer.exportDormantFailed': "Couldn't export dormant ({status}).",
  'errors.teamsExplorer.exportTeamsFailed': "Couldn't export teams ({status}).",
  'errors.teamsExplorer.exportChannelsFailed': "Couldn't export channels ({status}).",
  'errors.teamsExplorer.exportAdoptionFailed': "Couldn't export adoption ({status}).",

  // Web activity API
  'errors.webActivity.dataSourcesFailed': "Couldn't load the web traffic data sources ({status}).",
  'errors.webActivity.overviewFailed': "Couldn't load the web activity overview ({status}).",
  'errors.webActivity.visitsFailed': "Couldn't load visits ({status}).",
  'errors.webActivity.pagesFailed': "Couldn't load page views ({status}).",
  'errors.webActivity.journeysFailed': "Couldn't load visitor journeys ({status}).",
  'errors.webActivity.geographyFailed': "Couldn't load geography ({status}).",
  'errors.webActivity.searchFailed': "Couldn't load web searches ({status}).",
  'errors.webActivity.technologyFailed': "Couldn't load technology ({status}).",
  'errors.webActivity.exportPagesFailed': "Couldn't export pages ({status}).",
  'errors.webActivity.exportQuietPagesFailed': "Couldn't export quiet-pages ({status}).",
  'errors.webActivity.exportSlowPagesFailed': "Couldn't export slow-pages ({status}).",
  'errors.webActivity.exportEntryPagesFailed': "Couldn't export entry-pages ({status}).",
  'errors.webActivity.exportExitPagesFailed': "Couldn't export exit-pages ({status}).",
  'errors.webActivity.exportTransitionsFailed': "Couldn't export transitions ({status}).",
  'errors.webActivity.exportFlowsFailed': "Couldn't export flows ({status}).",
  'errors.webActivity.exportSearchTermsFailed': "Couldn't export search-terms ({status}).",
  'errors.webActivity.exportTechnologyFailed': "Couldn't export technology ({status}).",
} as const;

export default errors;
