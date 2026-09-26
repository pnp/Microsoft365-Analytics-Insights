/**
 * English text shared across the whole portal: generic actions, loading and empty states, units,
 * and the shared components in `src/components/shared`.
 *
 * A string belongs here only when it genuinely appears in more than one area. Area-specific text
 * lives in that area's module, so a change to the Copilot page cannot silently reword a button on
 * the Health page.
 *
 * Every key here must have a Spanish counterpart in `../es/common.ts`; the type of that module
 * makes a missing one a build failure.
 */
export const common = {
  // Actions
  'common.action.apply': 'Apply',
  'common.action.cancel': 'Cancel',
  'common.action.clear': 'Clear',
  'common.action.close': 'Close',
  'common.action.copyToClipboard': 'Copy to clipboard',
  'common.action.export': 'Export',
  'common.action.print': 'Print',
  'common.action.refresh': 'Refresh',
  'common.action.retry': 'Retry',
  'common.action.search': 'Search',
  'common.action.showAll': 'Show all',
  'common.action.showLess': 'Show less',
  'common.action.showMore': 'Show more',

  // States
  'common.state.loading': 'Loading\u2026',
  'common.state.noData': 'No data',
  'common.state.notAvailable': 'Not available',
  'common.state.notReported': 'Not reported',
  'common.state.none': 'None',
  'common.state.unknown': 'Unknown',
  'common.state.error': 'Something went wrong',
  'common.state.on': 'on',
  'common.state.off': 'off',
  'common.state.yes': 'Yes',
  'common.state.no': 'No',
  'common.state.enabled': 'Enabled',
  'common.state.disabled': 'Disabled',

  // Units and counts. English and Spanish share the same one/other split, so `plural()` picks
  // between these pairs rather than the catalog carrying a pluralisation engine.
  'common.unit.user.one': '{count} user',
  'common.unit.user.other': '{count} users',
  'common.unit.day.one': '{count} day',
  'common.unit.day.other': '{count} days',
  'common.unit.users': 'users',
  'common.unit.days': 'days',

  // Relative time
  'common.time.today': 'today',
  'common.time.yesterday': 'yesterday',
  'common.time.daysAgo': '{days} days ago',

  // InfoTip
  'common.infoTip.ariaLabel': 'How "{title}" is calculated',
  'common.infoTip.calculation': 'Calculation',

  // Dismissible data warnings
  'common.warnings.show.one': 'Show 1 data warning',
  'common.warnings.show.other': 'Show {count} data warnings',
  'common.warnings.hide': 'Hide these warnings',

  // Print button
  'common.print.preparing': 'Preparing\u2026',
  'common.print.tooManyRows.title': 'Too many rows to print',
  'common.print.tooManyRows.body':
    '{rows} rows match this list\u2019s filters, and a list prints in full only up to {limit} rows. Narrow the list with its filters and print again, or export it to get every row.',
  'common.print.failed.title': 'Could not prepare the printout',
  'common.print.failed.body':
    'The full list could not be loaded for printing, so nothing was printed. Try again in a moment.',

  // SQL popover
  'common.sql.title': 'SQL to reproduce this',
  'common.sql.buttonLabel': 'SQL',
  'common.sql.copied': 'SQL copied to clipboard',
  'common.sql.copyFailed': 'Could not copy to clipboard',

  // Sentiment traffic light
  'common.sentiment.notScored': 'Not scored for this period. {note}',
  'common.sentiment.detail': 'Sentiment {score} ({band}). {note}',
  'common.sentiment.scaleNote': 'Sentiment runs 0 (negative) to 1 (positive), weighted by message count, and 0.5 is neutral. It is not a percentage of positive messages.',
  'common.sentiment.band.negative': 'negative',
  'common.sentiment.band.leaningNegative': 'leaning negative',
  'common.sentiment.band.neutral': 'neutral',
  'common.sentiment.band.leaningPositive': 'leaning positive',
  'common.sentiment.band.positive': 'positive',

  // Placeholder labels the SERVER writes into data - the bucket for rows with no department, the
  // roll-up of every site outside the top N. Word for word what the C# and SQL write: the SPA
  // recognises them by these values (see components/shared/serverPlaceholder.ts), and
  // serverAuthoredText.test.ts fails when the server's list and this one disagree.
  'common.serverPlaceholder.noDepartment': '(no department)',
  'common.serverPlaceholder.noDepartmentCapitalised': '(No department)',
  'common.serverPlaceholder.noCountry': '(no country)',
  'common.serverPlaceholder.noOffice': '(no office)',
  'common.serverPlaceholder.noCompany': '(no company)',
  'common.serverPlaceholder.noManager': '(no manager)',
  'common.serverPlaceholder.noDomain': '(no domain)',
  'common.serverPlaceholder.noDomainCapitalised': '(No domain)',
  'common.serverPlaceholder.noReasonRecorded': '(no reason recorded)',
  'common.serverPlaceholder.unknown': '(unknown)',
  'common.serverPlaceholder.unknownSite': '(unknown site)',
  'common.serverPlaceholder.otherSites': '(other sites)',
  'common.serverPlaceholder.unknownCountry': '(unknown country)',
  'common.serverPlaceholder.otherCountries': '(other countries)',
  'common.serverPlaceholder.unknownDevice': '(unknown device)',
  'common.serverPlaceholder.otherDevices': '(other devices)',
  'common.serverPlaceholder.untitledElement': '(untitled element)',
  'common.serverPlaceholder.unnamedAgent': '(unnamed agent)',
  'common.serverPlaceholder.notSet': '(not set)',
  'common.serverPlaceholder.notStated': '(not stated)',
  'common.serverPlaceholder.none': '(none)',
  'common.serverPlaceholder.disabled': '(disabled)',
  'common.serverPlaceholder.redisNotConfigured': '(not configured - Teams deep analytics disabled)',
} as const;

export default common;
