/**
 * English text for the administration pages: service configuration, Teams permissions, user lookup, profiling and the install log.
 *
 * Every key here must have a Spanish counterpart in `../es/admin.ts`; the type of that module
 * makes a missing one a build failure.
 */
export const admin = {
  // Shared administration labels.
  'admin.common.enabled': 'Enabled',
  'admin.common.no': 'No',
  'admin.common.unknown': 'Unknown',
  'admin.common.unknownWithPeriod': 'Unknown.',
  'admin.common.yes': 'Yes',

  // Teams permissions.
  'admin.teamsPermissions.description':
    'This page is so you can authorise deep analytics for a Team. This will allow Microsoft 365 Advanced Analytics and Insights to read messages for anonymous statistical reporting purposes only.',
  'admin.teamsPermissions.errors.fetchGraphProfile': 'Unable to fetch Graph profile.',
  'admin.teamsPermissions.errors.fetchJoinedTeams': 'Unable to fetch joined teams.',
  'admin.teamsPermissions.loadingTeams': 'Loading your Teams...',
  'admin.teamsPermissions.noTeamsFound': 'No Teams found for your account.',
  'admin.teamsPermissions.noTokenMessage':
    "The site couldn't get a Microsoft Graph token for your session, so your Teams can't be listed. This usually means the sign-in that captured your refresh token has expired or predates it - sign out and sign in again. If it keeps happening, check that the runtime app registration has the delegated Teams permissions and that the site's reply URL is registered.",
  'admin.teamsPermissions.noTokenTeamsPlaceholder':
    'Your Teams will be listed here once the site can get a Graph token for your session.',
  'admin.teamsPermissions.title': 'Grant Team Access to the Microsoft 365 Advanced Analytics Engine',
  'admin.teamsPermissions.tokenNote':
    "Note: tokens are securely stored in a temporary Redis cache & aren't accessible to anyone.",
  'admin.teamsPermissions.yourTeamsDescription':
    'Here are all the Teams you have access to. Select which Teams you want to enable for deep analytics and continue.',
  'admin.teamsPermissions.yourTeamsTitle': 'Your Teams - {displayName}',

  // Teams authorisation list.
  'admin.teams.confirmSelection.actionsToApply': 'Actions to apply:',
  'admin.teams.confirmSelection.saveChanges': 'Save Changes',
  'admin.teams.confirmSelection.summary':
    'De-authorise {deAuthCount} Team(s); Authorise {authCount} Team(s)',
  'admin.teams.teamList.ariaLabel': 'Teams',
  'admin.teams.teamList.columnAuthorised': 'Authorised?',
  'admin.teams.teamList.columnGraphId': 'Graph ID',
  'admin.teams.teamList.columnTeamName': 'Team Name',
  'admin.teams.teamList.saveSuccess':
    'Selected Teams enabled for deep analytics successfully. It may take several hours before the extra metadata appears in any reports.',
  'admin.teams.teamList.unexpectedApiResponse': 'Unexpected response from API. Check JS log for more details.',
  'admin.teams.teamListItem.authorised': 'Authorised',
  'admin.teams.teamListItem.notAuthorised': 'Not authorised',
  'admin.teams.teamListItem.unnamedTeam': '(unnamed team)',

  // User data lookup page.
  'admin.userLookup.page.description':
    "Enter a user's UPN (user principal name, e.g. {exampleUpn}) to see all of the data held for them in the analytics database.",
  'admin.userLookup.page.exampleUpn': 'jane.doe@contoso.com',
  'admin.userLookup.page.loading': 'Looking up user data...',
  'admin.userLookup.page.lookupButton': 'Look up',
  'admin.userLookup.page.lookupFailed': 'Lookup failed.',
  'admin.userLookup.page.noUserLookedUp': 'No user looked up yet.',
  'admin.userLookup.page.title': 'User Data Lookup',
  'admin.userLookup.page.upnAriaLabel': 'User principal name',
  'admin.userLookup.page.upnPlaceholder': 'user@contoso.com',

  // User profile card.
  'admin.userLookup.profile.accountEnabled': 'Account enabled',
  'admin.userLookup.profile.azureAdId': 'Azure AD id',
  'admin.userLookup.profile.company': 'Company',
  'admin.userLookup.profile.countryOrRegion': 'Country / region',
  'admin.userLookup.profile.department': 'Department',
  'admin.userLookup.profile.jobTitle': 'Job title',
  'admin.userLookup.profile.lastUpdatedUtc': 'Last updated (UTC)',
  'admin.userLookup.profile.licensesTitle': 'Licenses ({count})',
  'admin.userLookup.profile.manager': 'Manager',
  'admin.userLookup.profile.noLicenses': 'No licenses recorded.',
  'admin.userLookup.profile.office': 'Office',
  'admin.userLookup.profile.postalCode': 'Postal code',
  'admin.userLookup.profile.stateOrProvince': 'State / province',
  'admin.userLookup.profile.title': 'Profile',
  'admin.userLookup.profile.usageLocation': 'Usage location',
  'admin.userLookup.profile.utcContractWarning':
    "Values written before this UTC contract may reflect the web-job host's old local time.",

  // User data category summary.
  'admin.userLookup.categoryTable.dataHeldTitle':
    'Data held ({records} records across {categories} categories)',
  'admin.userLookup.categoryTable.importWorkloadsHint':
    'Data is only collected for enabled workloads. A category fed only by disabled workloads will show 0 records - that is expected, not a fault.',
  'admin.userLookup.categoryTable.importWorkloadsTitle':
    'Import workloads ({enabledCount} of {totalCount} enabled)',
  'admin.userLookup.categoryTable.sqlHint':
    'Click the {sql} button on any row to view and copy the query behind its count.',

  // User data category rows.
  'admin.userLookup.categoryRow.columnDetail': 'Detail',
  'admin.userLookup.categoryRow.columnWhen': 'When',
  'admin.userLookup.categoryRow.copyToClipboard': 'Copy to clipboard',
  'admin.userLookup.categoryRow.hideRecent': 'Hide',
  'admin.userLookup.categoryRow.importOff': 'import off',
  'admin.userLookup.categoryRow.importOffTooltip.one':
    'This data isn\'t being imported (workload "{workloads}" disabled), so a count of 0 is expected.',
  'admin.userLookup.categoryRow.importOffTooltip.other':
    'This data isn\'t being imported (workloads "{workloads}" disabled), so a count of 0 is expected.',
  'admin.userLookup.categoryRow.loadDetailFailed': 'Failed to load detail.',
  'admin.userLookup.categoryRow.loadingRecentRows': 'Loading recent rows...',
  'admin.userLookup.categoryRow.noRows': 'No rows.',
  'admin.userLookup.categoryRow.recentRowsAriaLabel': '{category} recent rows',
  'admin.userLookup.categoryRow.showingRecent': 'Showing {count} most recent of {total}.',
  'admin.userLookup.categoryRow.source': 'Source: {source}',
  'admin.userLookup.categoryRow.sqlCopied': 'SQL copied to clipboard',
  'admin.userLookup.categoryRow.sqlCopyFailed': 'Could not copy to clipboard',
  'admin.userLookup.categoryRow.sqlTitle': 'SQL to reproduce this count',
  'admin.userLookup.categoryRow.viewRecent': 'View recent',

  // Service configuration: page and webhook status.
  'admin.serviceConfiguration.loadFailed': 'Failed to load the service configuration.',
  'admin.serviceConfiguration.loading': 'Loading service configuration...',
  'admin.serviceConfiguration.noConfiguration': 'No configuration available.',
  'admin.serviceConfiguration.title': 'Service configuration',
  'admin.serviceConfiguration.titleWithBuild': 'Service configuration - {buildLabel}',
  'admin.serviceConfiguration.webhook.active': 'Active',
  'admin.serviceConfiguration.webhook.callRecordsPermission': 'CallRecords.Read.All',
  'admin.serviceConfiguration.webhook.couldNotCheck': "Couldn't check",
  'admin.serviceConfiguration.webhook.missingHelp':
    'The importer web-job registers and renews this on every import cycle. If it stays missing, check the importer web-job is running and that its app registration has the {permission} Microsoft Graph application permission.',
  'admin.serviceConfiguration.webhook.noActiveSubscription': 'No active subscription found',
  'admin.serviceConfiguration.webhook.notApplicable': 'Not applicable - Teams calls import is disabled',
  'admin.serviceConfiguration.webhook.renewsAutomatically': 'renews automatically; expires {expiry}',
  'admin.serviceConfiguration.webhook.testFailed': 'Webhook test failed.',
  'admin.serviceConfiguration.webhook.testSuccess': 'Success. Got back test-token "{token}"',
  'admin.serviceConfiguration.webhook.testUnexpectedResponse':
    'Unexpected response. Got back response body "{token}"',

  // Service configuration: updates.
  'admin.serviceConfiguration.updates.ariaLabel': 'Update check',
  'admin.serviceConfiguration.updates.build': 'Build {build}',
  'admin.serviceConfiguration.updates.checked': 'Checked {checkedAt}',
  'admin.serviceConfiguration.updates.checkFailed': 'Update check failed.',
  'admin.serviceConfiguration.updates.checkForUpdates': 'Check for updates',
  'admin.serviceConfiguration.updates.checking': 'Checking...',
  'admin.serviceConfiguration.updates.currentBuildLabel': 'This site is running',
  'admin.serviceConfiguration.updates.description':
    'Compares the build this site is running against the latest published release on GitHub. Nothing is sent to GitHub until you press the button.',
  'admin.serviceConfiguration.updates.latestReleaseLabel': 'Latest published release',
  'admin.serviceConfiguration.updates.openLatestRelease': 'Open the latest release',
  'admin.serviceConfiguration.updates.openReleaseNotes': 'Open the release notes and downloads',
  'admin.serviceConfiguration.updates.published': 'Published {publishedAt}',
  'admin.serviceConfiguration.updates.title': 'Software updates',
  'admin.serviceConfiguration.updates.updateAvailableLead': 'An update is available.',
  'admin.serviceConfiguration.updates.updateAvailableNoLink':
    '{lead} This site is on build {currentBuild}; build {latestBuild} has been released. Read the release notes before upgrading - they call out any database migrations and configuration changes.',
  'admin.serviceConfiguration.updates.updateAvailableWithLink':
    '{lead} This site is on build {currentBuild}; build {latestBuild} has been released. {releaseLink}. Read the release notes before upgrading - they call out any database migrations and configuration changes.',
  'admin.serviceConfiguration.updates.upToDate': 'This site is up to date - no newer release has been published.',
  'admin.serviceConfiguration.updates.viewCurrentRelease': 'View the current release',

  // Service configuration: Azure resources.
  'admin.serviceConfiguration.azureResources.ariaLabel': 'Azure resources',
  'admin.serviceConfiguration.azureResources.cognitiveAnalyticsAvailable':
    'Yes - cognitive analytics will be available',
  'admin.serviceConfiguration.azureResources.cognitiveAnalyticsDisabled':
    'No - cognitive analytics are disabled',
  'admin.serviceConfiguration.azureResources.cognitiveServicesEnabled': 'Cognitive Services Enabled',
  'admin.serviceConfiguration.azureResources.cognitiveServicesEndpoint': 'Cognitive Services Endpoint',
  'admin.serviceConfiguration.azureResources.description':
    'These are the resources this deployment is configured to use:',
  'admin.serviceConfiguration.azureResources.redisSslEndpoint': 'Redis SSL Endpoint',
  'admin.serviceConfiguration.azureResources.title': 'Azure resources',
  'admin.serviceConfiguration.azureResources.webAppUrl': 'Web app URL',

  // Service configuration: imports and schema.
  'admin.serviceConfiguration.importsAndSchema.configLoadFailed': "Couldn't load configuration: {error}",
  'admin.serviceConfiguration.importsAndSchema.databaseBehind':
    'The database is behind this build - run the upgrader. ({migrations})',
  'admin.serviceConfiguration.importsAndSchema.description':
    'Which import workloads are turned on - so an empty report reads as "feature off", not "broken".',
  'admin.serviceConfiguration.importsAndSchema.noneEnabled': "None enabled in this app's config.",
  'admin.serviceConfiguration.importsAndSchema.pendingMigrations': '{count} migration(s) pending',
  'admin.serviceConfiguration.importsAndSchema.schemaCheckFailed': "Couldn't check: {error}",
  'admin.serviceConfiguration.importsAndSchema.schemaStateAriaLabel': 'Schema state',
  'admin.serviceConfiguration.importsAndSchema.schemaVersion': 'Schema / migration version',
  'admin.serviceConfiguration.importsAndSchema.title': 'Imports and schema',
  'admin.serviceConfiguration.importsAndSchema.upToDate': 'Up to date with this build',

  // Service configuration: Teams calls.
  'admin.serviceConfiguration.teamsCalls.ariaLabel': 'Teams calls configuration',
  'admin.serviceConfiguration.teamsCalls.disabled': 'Disabled - Teams call records are not being imported',
  'admin.serviceConfiguration.teamsCalls.healthCheckExpiry': 'Health check last saw it expiring {expiry}.',
  'admin.serviceConfiguration.teamsCalls.importLabel': 'Teams Calls Import',
  'admin.serviceConfiguration.teamsCalls.testWebhook': 'test webhook with validation POST',
  'admin.serviceConfiguration.teamsCalls.title': 'Teams calls',
  'admin.serviceConfiguration.teamsCalls.webhookEndpoint': 'Graph Call Webhook Endpoint',
  'admin.serviceConfiguration.teamsCalls.webhookSubscription': 'Calls Webhook Subscription',

  // Profiling status.
  'admin.profiling.compiledData.description':
    "Built by the profiling runbooks. If these are empty or stale, the runbooks haven't run (or errored).",
  'admin.profiling.compiledData.title': 'Compiled profiling data',
  'admin.profiling.dataFreshness': 'Data freshness',
  'admin.profiling.description':
    "The current state of the profiling data: how fresh each table is, and the profiling runbooks' own trace log. Use this to check the runbooks have run and that data is up to date.",
  'admin.profiling.errors.loadStatusFailed': 'Failed to load profiling status.',
  'admin.profiling.errors.loadTraceLogsFailed': 'Failed to load trace logs.',
  'admin.profiling.loadingStatus': 'Loading profiling status...',
  'admin.profiling.rangeSection.columnData': 'Data',
  'admin.profiling.rangeSection.columnEarliest': 'Earliest',
  'admin.profiling.rangeSection.columnLatest': 'Latest',
  'admin.profiling.rangeSection.sqlTitle': 'SQL to reproduce these dates',
  'admin.profiling.refresh': 'Refresh',
  'admin.profiling.sourceActivityData.description':
    'The raw activity-log tables that feed the profiling compile, imported from the Microsoft 365 usage reports.',
  'admin.profiling.sourceActivityData.title': 'Source activity data',
  'admin.profiling.title': 'Profiling',
  'admin.profiling.traceLogs.ariaLabel': 'Profiling trace logs',
  'admin.profiling.traceLogs.columnMessage': 'Message',
  'admin.profiling.traceLogs.columnWhen': 'When',
  'admin.profiling.traceLogs.description':
    'Trace output written by the profiling runbooks (profiling.TraceLogs), newest first.',
  'admin.profiling.traceLogs.loading': 'Loading…',
  'admin.profiling.traceLogs.loadingTraceLogs': 'Loading trace logs...',
  'admin.profiling.traceLogs.next': 'Next',
  'admin.profiling.traceLogs.none': 'No trace logs.',
  'admin.profiling.traceLogs.noneOnPage': 'No trace logs on this page.',
  'admin.profiling.traceLogs.previous': 'Previous',
  'admin.profiling.traceLogs.readFailed': "Couldn't read the profiling trace logs: {error}",
  'admin.profiling.traceLogs.rowsPerPage': 'Rows per page',
  'admin.profiling.traceLogs.showing': 'Showing {firstRow}–{lastRow} of {total}',
  'admin.profiling.traceLogs.sqlTitle': 'SQL behind the trace log',
  'admin.profiling.traceLogs.title': 'Trace logs',

  // Install log.
  'admin.installLog.ariaLabel': 'Install log',
  'admin.installLog.close': 'Close',
  'admin.installLog.columnApplied': 'Applied',
  'admin.installLog.columnConfiguration': 'Configuration',
  'admin.installLog.columnInstalledBy': 'Installed by',
  'admin.installLog.columnMessages': 'Messages',
  'admin.installLog.current': 'Current',
  'admin.installLog.description':
    'History of configurations applied to the solution (the {table} table). The most recent entry is the current configuration.',
  'admin.installLog.dialogTitle': 'Install log — {appliedAt}',
  'admin.installLog.loadFailed': 'Failed to load the install log.',
  'admin.installLog.loading': 'Loading install log...',
  'admin.installLog.noneApplied': 'No configurations applied yet.',
  'admin.installLog.title': 'Install Log',
  'admin.installLog.viewConfig': 'View config',
  'admin.installLog.viewLog': 'View log',
} as const;

export default admin;
