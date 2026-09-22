/**
 * English text for the Licence activity page and its panels.
 *
 * Every key here must have a Spanish counterpart in `../es/licenceActivity.ts`; the type of that module
 * makes a missing one a build failure.
 */
export const licenceActivity = {
  'licenceActivity.users.workloadActivity': '{workload} activity',
  'licenceActivity.licenceFallbackName': 'Licence {id}',
  // Shared vocabulary
  'licenceActivity.common.unknown': 'Unknown',
  'licenceActivity.common.notMeasured': 'Not measured',
  'licenceActivity.common.source': 'Source:',
  'licenceActivity.common.lastImported': 'Last imported {date}',
  'licenceActivity.common.dataFrom': 'Data from {from} \u2013 {to}',
  'licenceActivity.common.measurementsTaken': '{observed} of {expected} measurements taken',
  'licenceActivity.common.peopleCouldNotBeMatched': "{count} people couldn't be matched",
  'licenceActivity.common.service': 'Service',
  'licenceActivity.common.licence': 'Licence',
  'licenceActivity.common.peopleAssigned': 'People assigned',
  'licenceActivity.common.people': 'People',
  'licenceActivity.common.department': 'Department',
  'licenceActivity.common.country': 'Country',
  'licenceActivity.common.average': 'Average',
  'licenceActivity.common.lastActive': 'Last active',
  'licenceActivity.common.activity': 'Activity',
  'licenceActivity.common.data': 'Data',
  'licenceActivity.common.apply': 'Apply',
  'licenceActivity.common.refresh': 'Refresh',
  'licenceActivity.common.search': 'Search',
  'licenceActivity.common.previous': 'Previous',
  'licenceActivity.common.next': 'Next',

  // Activity bands and coverage statuses
  'licenceActivity.band.high': 'High',
  'licenceActivity.band.moderate': 'Moderate',
  'licenceActivity.band.low': 'Low',
  'licenceActivity.band.zero': 'No activity',
  'licenceActivity.band.unknown': 'Unknown',
  'licenceActivity.band.description.high': 'Active in three quarters or more of the weeks that were measured.',
  'licenceActivity.band.description.moderate': 'Active in a quarter to under three quarters of the weeks that were measured.',
  'licenceActivity.band.description.low': 'Active in under a quarter of the weeks that were measured, but active in at least one.',
  'licenceActivity.band.description.zero': 'Complete reporting data shows no activity in any week for this user and period. Every week was measured in full.',
  'licenceActivity.band.description.unknown':
    'At least one week could not be measured in full, so there is not enough reporting data to determine activity for this user and period. Reports or user rows may be missing, coverage may be incomplete, or usage counters may be unavailable. This is not evidence of no activity.',
  'licenceActivity.band.copilotCoverageNote':
    'The official Copilot usage report covers Copilot-licensed users only. These charts can also include people with other licences, so someone without a Copilot licence may appear as Unknown rather than inactive. Unknown alone does not tell you whether someone has a Copilot licence.',
  'licenceActivity.band.method':
    "Activity levels describe how many of the period's weeks someone was active in: High = three quarters or more, Moderate = a quarter to under three quarters, Low = under a quarter, No activity = none. A week is only counted when every one of its days was imported; where a week could not be measured in full the level is Unknown, not zero.",
  'licenceActivity.status.available.label': 'Available',
  'licenceActivity.status.available.explanation': 'Measured across the whole period.',
  'licenceActivity.status.partial.label': 'Partial',
  'licenceActivity.status.partial.explanation': 'Part of this period could not be measured in full, so activity here may be understated.',
  'licenceActivity.status.missingCoverage.label': 'Missing coverage',
  'licenceActivity.status.missingCoverage.explanation':
    'Part of the chosen period has no measurement behind it, so nobody can be shown as inactive for this service.',
  'licenceActivity.status.unmatchableIdentity.label': 'Identities could not be matched',
  'licenceActivity.status.unmatchableIdentity.explanation':
    'Microsoft hid the identities in this report, so its activity cannot be tied back to the people holding the licence.',
  'licenceActivity.status.notImported.label': 'Not imported',
  'licenceActivity.status.notImported.explanation': 'This service\u2019s usage data has never been collected on this deployment.',
  'licenceActivity.status.disabled.label': 'Import switched off',
  'licenceActivity.status.disabled.explanation': 'Collection for this service is switched off in the installer.',
  'licenceActivity.status.unknown.label': 'Unknown',
  'licenceActivity.status.unknown.explanation': 'Not measured for this person \u2013 which is not the same as measured as no activity.',

  // Source and sampling labels
  'licenceActivity.source.microsoftGraphUsageReport': 'Microsoft 365 usage reports',
  'licenceActivity.source.microsoftGraphCopilotUsageReport': 'Microsoft 365 Copilot usage report',
  'licenceActivity.source.copilotAudit': 'Copilot audit log',
  'licenceActivity.source.copilotInteractions': 'Copilot chat history',
  'licenceActivity.granularity.weeklySupportingSnapshot': 'one reading per week',
  'licenceActivity.granularity.singleRollingWindow': 'a single rolling report',
  'licenceActivity.granularity.weeklySampleOfRolling7DayReport': 'one 7-day report read per week',
  'licenceActivity.granularity.eventPositiveOnly': 'recorded activity only',
  'licenceActivity.granularity.unknown': 'not applicable',

  // ActivityCoverageHelp
  'licenceActivity.activityCoverage.summary':
    'Unknown means insufficient data, not no activity. No activity means complete reporting data shows no usage.',
  'licenceActivity.activityCoverage.whyUnknown': 'Why is activity Unknown?',
  'licenceActivity.activityCoverage.recordedEventsOnly':
    'When the source contains only recorded events, finding no events does not prove that the person was inactive.',
  'licenceActivity.activityCoverage.showSourcesHelp':
    'Under Where these figures come from, select Show data sources for the source, reporting dates and import status for each service.',

  // Coverage and data sources
  'licenceActivity.coverage.title': 'Where these figures come from',
  'licenceActivity.coverage.preparedHeld': 'Prepared {prepared} ({age}); held for up to {expires}',
  'licenceActivity.coverage.noSourceInfo': 'No source information was reported for these figures.',
  'licenceActivity.coverage.mostRecentDataDaysOld': 'most recent data is {days} days old',
  'licenceActivity.coverage.coversDays': 'covers {days} days',
  'licenceActivity.dataSource.servicesMeasuredAria': '{available} of {total} services measured in full',
  'licenceActivity.dataSource.prepared': '\u00b7 prepared {age}',
  'licenceActivity.dataSource.hide': 'Hide data sources',
  'licenceActivity.dataSource.show': 'Show data sources',

  // Date range
  'licenceActivity.dateRange.preset.lastSettledWeek': 'Last settled week',
  'licenceActivity.dateRange.preset.last4FullySettledWeeks': 'Last 4 fully settled weeks',
  'licenceActivity.dateRange.preset.last90SettledDays': 'Last 90 settled days',
  'licenceActivity.dateRange.preset.last180SettledDays': 'Last 180 settled days',
  'licenceActivity.dateRange.customRange': 'Custom range',
  'licenceActivity.dateRange.daysEnding': '{days} days ending {date}',
  'licenceActivity.dateRange.from': 'From',
  'licenceActivity.dateRange.startDate': 'Start date',
  'licenceActivity.dateRange.to': 'To',
  'licenceActivity.dateRange.endDate': 'End date',
  'licenceActivity.dateRange.error.enterBothDates': 'Enter both a start and end date.',
  'licenceActivity.dateRange.error.earliestSupportedDate': 'The earliest supported date is {date}.',
  'licenceActivity.dateRange.error.startOnOrBeforeEnd': 'The start date must be on or before the end date.',
  'licenceActivity.dateRange.error.endBeforeToday':
    'The end date must be before today (reporting covers whole past days).',
  'licenceActivity.dateRange.error.rangeAtLeastDays': 'The range must be at least {days} days.',
  'licenceActivity.dateRange.error.rangeNoLongerThanDays': 'The range cannot be longer than {days} days.',

  // Overview and selected licence
  'licenceActivity.overview.peopleWithLicence': 'People with a licence',
  'licenceActivity.overview.countingEachPersonOnce': 'in this selection, counting each person once',
  'licenceActivity.overview.licenceTypes': 'Licence types',
  'licenceActivity.overview.assignedOne': 'assigned in this selection',
  'licenceActivity.overview.assignedMany': 'assigned, each measured separately',
  'licenceActivity.overview.selectedLicence': 'Selected licence',
  'licenceActivity.overview.peopleHoldItSeeTabs': '{count} people hold it \u00b7 see By service and People',
  'licenceActivity.overview.noneChosen': 'None chosen',
  'licenceActivity.overview.chooseLicenceBelow': 'Choose a licence below to see its services and people',
  'licenceActivity.selectedLicence.aria': 'Selected licence',
  'licenceActivity.selectedLicence.choose': 'Choose a licence',
  'licenceActivity.selectedLicence.peopleHold': '{count} people hold this licence',

  // Assignments and demographic breakdowns
  'licenceActivity.assignments.empty': 'No licence assignments were found for this selection.',
  'licenceActivity.assignments.title': 'Licence assignments',
  'licenceActivity.assignments.description': 'Select a licence to see how much each service is used, and who holds it.',
  'licenceActivity.assignments.showingOf': 'Showing {shown} of {total}.',
  'licenceActivity.assignments.filterPlaceholder': 'Filter by licence name or code',
  'licenceActivity.assignments.filterAria': 'Filter licences',
  'licenceActivity.assignments.noMatches': 'No licences match \u201c{filter}\u201d.',
  'licenceActivity.demographics.description':
    'People with any imported licence, not necessarily a licence for every service, by {segment}, largest first.',
  'licenceActivity.demographics.capped':
    'Showing only the {count} largest by number of people assigned \u2014 this is not the full list.',

  // Workload distributions
  'licenceActivity.distribution.activeOfMeasured': '{active} of {measured} active ({rate})',
  'licenceActivity.distribution.aria': '{label} activity distribution',
  'licenceActivity.distribution.noActivity': 'No activity is available for this licence.',
  'licenceActivity.distribution.activeCountExplanation':
    'The "active" count is everyone with any measured activity, out of the people whose whole period could be measured.',
  'licenceActivity.distribution.bandTitle': '{label}: {count}. {description}',
  'licenceActivity.distribution.bandLegendLabel': '{label} {count}',

  // Users drill-down and table
  'licenceActivity.users.sort.mostActive': 'Most active first',
  'licenceActivity.users.sort.leastActive': 'Least active first',
  'licenceActivity.users.sort.mostRecentlyActive': 'Most recently active',
  'licenceActivity.users.sort.longestSinceActive': 'Longest since active',
  'licenceActivity.users.sort.upnAsc': 'Sign-in address (A\u2013Z)',
  'licenceActivity.users.sort.upnDesc': 'Sign-in address (Z\u2013A)',
  'licenceActivity.users.topCountAria': 'Number of people in each list',
  'licenceActivity.users.holdLicenceChooseService':
    '{count} people hold this licence. Choose a service to rank them by how much they use it.',
  'licenceActivity.users.showTop': 'Show top',
  'licenceActivity.users.ofEach': 'of each',
  'licenceActivity.users.refreshAria': 'Refresh the list',
  'licenceActivity.users.clearSearch': 'Clear search and try again',
  'licenceActivity.users.incompleteWarning':
    'People with recorded activity still appear as most active; nobody is listed as least active for this service.',
  'licenceActivity.users.loading': 'Loading people...',
  'licenceActivity.users.mostActive': 'Most active',
  'licenceActivity.users.topN': 'top {count}',
  'licenceActivity.users.leastActive': 'Least active',
  'licenceActivity.users.bottomN': 'bottom {count}',
  'licenceActivity.users.everyoneWithLicence': 'Everyone with this licence',
  'licenceActivity.users.peopleCount': '{count} people',
  'licenceActivity.users.noStaffNames':
    'Staff names aren\u2019t collected by this product, so people are listed and searched by their sign-in address.',
  'licenceActivity.users.searchPlaceholder': 'Search by sign-in address',
  'licenceActivity.users.searchAria': 'Search users',
  'licenceActivity.users.sortAria': 'Sort users',
  'licenceActivity.users.showingRange': 'Showing {from}\u2013{to} of {total} people',
  'licenceActivity.users.pageOf': 'Page {page} of {totalPages}',
  'licenceActivity.users.rankUnavailableIncomplete':
    "{workload} activity isn't fully measured, so people can't be ranked here - see the note above.",
  'licenceActivity.users.rankUnavailableEmpty': 'Nobody can be ranked for this service.',
  'licenceActivity.users.noSearchMatches': 'Nobody matches your search.',
  'licenceActivity.users.nobodyToShow': 'Nobody to show for this selection.',
  'licenceActivity.users.couldNotLoad': "Couldn't load the people holding this licence.",
  'licenceActivity.users.expandAria': 'Expand',
  'licenceActivity.users.everyServiceFor': 'Every service for {user}',
  'licenceActivity.users.whereItComesFrom': 'Where it comes from',
  'licenceActivity.users.activeMeasuredExpected': 'Active / measured (expected)',
  'licenceActivity.users.person': 'Person',
  'licenceActivity.users.averageActions': 'Average actions',
  'licenceActivity.users.activeMeasured': 'Active / measured',
  'licenceActivity.users.showAllServicesFor': 'Show all services for {user}',
  'licenceActivity.users.accountDisabled': 'Account disabled',
  'licenceActivity.users.samplesMeasured': '{observed} of {expected} measured',
  'licenceActivity.users.activeObservedOfExpected': '{base} of {expected}',

  // Page
  'licenceActivity.page.title': 'Licence activity',
  'licenceActivity.page.preview': 'Preview',
  'licenceActivity.page.intro':
    'Which licences are assigned, and how much are the people who hold them actually using each Microsoft 365 service. Each service is shown on its own and never blended into a single score. Missing or incomplete reporting data is shown as "Unknown", not proof of no activity.',
  'licenceActivity.page.previewNote':
    "This report is in preview. Figures are kept for up to 5 minutes before being worked out again, so a very recent import may not appear straight away, and the first look at a new date range takes longer. Nothing here is a judgement of anyone's productivity, or a recommendation to take a licence away \u2014 it is evidence of activity only.",
  'licenceActivity.page.checkingAvailability': 'Checking availability...',
  'licenceActivity.page.availabilityFailed': 'Failed to check licence activity availability.',
  'licenceActivity.page.reload': 'Reload',
  'licenceActivity.page.notAvailable': 'Licence activity reporting is not available on this deployment.',
  'licenceActivity.page.reportingWindow': 'Reporting window',
  'licenceActivity.page.filterByDepartment': 'Filter by department',
  'licenceActivity.page.allDepartments': 'All departments',
  'licenceActivity.page.recentOptionsHidden': 'Showing up to {count} recent options; some may be hidden.',
  'licenceActivity.page.filterByCountry': 'Filter by country',
  'licenceActivity.page.allCountries': 'All countries',
  'licenceActivity.page.exportSummaryAndPeopleTooltip':
    'An Excel copy of the summary plus the exact people currently listed in the People tab. Built from the figures already on screen, so it matches what you can see.',
  'licenceActivity.page.exportSummaryTooltip':
    'An Excel copy of the licence and service summary (totals only). Built from the figures already on screen.',
  'licenceActivity.page.exportUnavailableTooltip': 'Available once the report has loaded.',
  'licenceActivity.page.exporting': 'Exporting...',
  'licenceActivity.page.exportToExcel': 'Export to Excel',
  'licenceActivity.page.exportFailed': "Couldn't export the workbook.",
  'licenceActivity.page.tryAgain': 'Try again',
  'licenceActivity.page.loading': 'Loading licence activity...',
  'licenceActivity.page.overviewFailed': 'Failed to load the licence activity overview.',
  'licenceActivity.page.tabOverview': 'Overview',
  'licenceActivity.page.tabByService': 'By service',
  'licenceActivity.page.tabByDemographic': 'By department & country',
  'licenceActivity.page.tabPeople': 'People',
  'licenceActivity.page.activityByService': 'Activity by service',
  'licenceActivity.page.activityByServiceSubtitle':
    'Each Microsoft 365 service on its own, never blended into a single score',
  'licenceActivity.page.chooseLicenceOverview':
    'Choose a licence on the Overview tab to see how much each service is used.',
  'licenceActivity.page.activityByDemographic': 'Activity by department and country',
  'licenceActivity.page.activityByDemographicSubtitle':
    'Where the licences sit in the organisation, and how much they are being used',
  'licenceActivity.page.demographicCapped':
    'The department and country lists are capped, so they may not show every one.',
  'licenceActivity.page.byDepartment': 'By department',
  'licenceActivity.page.byCountry': 'By country',
  'licenceActivity.page.noDemographicBreakdown':
    'No department or country breakdown is available for this selection.',
  'licenceActivity.page.peopleHoldingLicence': 'People holding this licence',
  'licenceActivity.page.peopleSubtitle': "Who is and isn't using a licence",
  'licenceActivity.page.selectLicenceForPeople':
    'Select a licence on the Overview tab to see who is most and least active, or to browse everyone who holds it.',
} as const;

export default licenceActivity;
