/**
 * English text for the per-user licence and data diagnostics on Administration > User lookup.
 *
 * Every key here must have a Spanish counterpart in `../es/userDiagnostics.ts`; the type of that module
 * makes a missing one a build failure.
 *
 * Not here, deliberately: licence names, SKU part numbers, UPNs and group names. Those are tenant or
 * Microsoft data and render exactly as stored.
 */
export const userDiagnostics = {
  'userDiagnostics.title': 'Licence and data diagnostics',
  'userDiagnostics.intro':
    'Evidence for why this person shows as licensed or unlicensed, and how fresh each kind of data held about them is. Nothing here changes any data unless you ask for a refresh.',
  'userDiagnostics.loading': 'Loading diagnostics...',
  'userDiagnostics.loadFailed': 'The diagnostics could not be loaded.',
  'userDiagnostics.loadedAt': 'Loaded {when}',

  // Findings
  'userDiagnostics.findings.title': 'What the evidence suggests',
  'userDiagnostics.findings.none': 'Nothing in the stored evidence looks wrong for this person.',
  'userDiagnostics.finding.noStoredUser':
    'The user import has never stored this person, and a report cannot count someone who is not in the database. Check the import scope below, then compare with Microsoft Graph to see whether the directory has them.',
  'userDiagnostics.finding.noStoredLicences':
    'No licence assignments are stored for this person. That is what the database holds, not proof that Entra ID assigns them nothing - compare with Microsoft Graph to check.',
  'userDiagnostics.finding.noStoredCopilotSeat':
    'None of the stored licences counts as a Microsoft 365 Copilot seat under the current rules, so Copilot Adoption treats this person as unlicensed.',
  'userDiagnostics.finding.accountDisabled': 'The account is disabled in the stored data.',
  'userDiagnostics.finding.scopeExcluded':
    'This person is outside the configured user scope, so the import does not store data about them and live directory reads are not available.',
  'userDiagnostics.finding.scopeUnknown':
    'The user scope could not be fully resolved, so it is not known whether this person is in it. Live directory reads stay unavailable until it resolves - see Administration > User scope.',
  'userDiagnostics.finding.userImportDisabled': 'The user metadata and licence import is switched off, so stored licences are not being refreshed.',
  'userDiagnostics.finding.userImportNotRecorded':
    'No completed user import is recorded in Azure Table storage, so how fresh the stored licences are cannot be shown.',
  'userDiagnostics.finding.userImportOverdue':
    'The user import last completed {when}, longer ago than its configured interval allows. Stored licences may be out of date - check the importer on the Health page.',
  'userDiagnostics.finding.licenceHistoryUnavailable': 'This database does not record licence history yet, so past assignments cannot be shown.',
  'userDiagnostics.finding.catalogueMissingSku':
    "SKU {sku} is not in this build's licensing catalogue, so it is stored under its part number. A newer build may name and classify it differently.",
  'userDiagnostics.finding.possibleCopilotRuleGapNotCounted':
    "Microsoft's licensing catalogue lists the Microsoft 365 Copilot seat service plans in {name} ({sku}), but the Copilot rules do not count it as a seat. Anyone who has Copilot only through it shows as unlicensed in Copilot Adoption. Try a what-if seat selection on that report to see the effect.",
  'userDiagnostics.finding.possibleCopilotRuleGapCounted':
    "{name} ({sku}) counts as a Copilot seat under the rules, but Microsoft's licensing catalogue does not list the Copilot seat service plans in it.",

  // Scope
  'userDiagnostics.scope.title': 'Import scope',
  'userDiagnostics.scope.unfiltered': 'No user scope filter is configured, so everyone is in scope.',
  'userDiagnostics.scope.inScope': 'In scope: a direct member of a group the user scope filter resolved to ({members} people in {groups} groups, resolved {when}).',
  'userDiagnostics.scope.excluded': 'Out of scope: not a direct member of any group the user scope filter resolved to ({members} people in {groups} groups, resolved {when}).',
  'userDiagnostics.scope.unknown': 'Unknown: the user scope filter could not be resolved ({reason}).',
  'userDiagnostics.scope.failure.directoryRead': 'a directory read failed',
  'userDiagnostics.scope.failure.budgetExhausted': 'the directory read limit was reached',
  'userDiagnostics.scope.failure.clientUnavailable': 'no Microsoft Graph client could be created',
  'userDiagnostics.scope.failure.unexpected': 'an unexpected error',

  // Freshness
  'userDiagnostics.freshness.title': 'How fresh are the stored licences?',
  'userDiagnostics.freshness.metadataLastWritten': 'Profile last written by the import',
  'userDiagnostics.freshness.metadataLastWrittenHint':
    "When the import last changed this person's profile row. An unchanged person is not rewritten, so an old date here is normal and does not mean their licences went unchecked.",
  'userDiagnostics.freshness.userImportLastCompleted': 'User import last completed (whole tenant)',
  'userDiagnostics.freshness.lastTenantLicenceRefresh': 'Last licence refresh recorded (whole tenant)',
  'userDiagnostics.freshness.userVerification': 'Last confirmed for this person',
  'userDiagnostics.freshness.interval': 'Runs at most every {hours} hours',
  'userDiagnostics.freshness.notRecorded': 'Not recorded',
  'userDiagnostics.freshness.importDisabled': 'The import is switched off',
  'userDiagnostics.freshness.storageNotConfigured': 'Not available: no Azure Storage is configured',
  'userDiagnostics.freshness.storageUnavailable': 'Not available: Azure Table storage could not be read',
  'userDiagnostics.freshness.tenantWideHint':
    'Whole-tenant times say an import finished, not that this person was in it. Compare with Microsoft Graph to check this person now.',

  // Stored licences
  'userDiagnostics.licences.title': 'Stored licences',
  'userDiagnostics.licences.none': 'No licence assignments are stored for this person.',
  'userDiagnostics.licences.columnLicence': 'Licence',
  'userDiagnostics.licences.columnSku': 'SKU part number',
  'userDiagnostics.licences.columnCopilot': 'Counts as a Copilot seat',
  'userDiagnostics.licences.columnWhy': 'Why',
  'userDiagnostics.licences.columnHeldSince': 'Held since',
  'userDiagnostics.licences.columnCatalogue': 'Licensing catalogue',
  'userDiagnostics.licences.heldSince.observed': 'First seen {when}',
  'userDiagnostics.licences.heldSince.beforeTrackingBegan': 'Already held when history began ({when})',
  'userDiagnostics.licences.heldSince.unknown': 'Not recorded',
  'userDiagnostics.licences.catalogue.listed': 'Listed',
  'userDiagnostics.licences.catalogue.listedWithSeat': 'Listed, with the Copilot seat plans',
  'userDiagnostics.licences.catalogue.notListed': "Not in this build's catalogue",

  // Classification reasons
  'userDiagnostics.reason.seatSkuPrefix': 'The SKU starts with {rule}, a Microsoft 365 Copilot seat or a suite that includes one.',
  'userDiagnostics.reason.excludedSkuPrefix': 'The SKU starts with {rule}: a Copilot-branded product that is not a Microsoft 365 Copilot seat.',
  'userDiagnostics.reason.seatDisplayName': 'No SKU rule applies, but the product name reads as Microsoft 365 Copilot ({rule}).',
  'userDiagnostics.reason.excludedNameWord': 'The product name mentions Copilot together with "{rule}", which marks another Copilot-branded product.',
  'userDiagnostics.reason.copilotNameWithoutQualifier': 'The product name mentions Copilot but not Microsoft 365, so it is not counted.',
  'userDiagnostics.reason.noRuleMatched': 'Nothing about the SKU or its name indicates Microsoft 365 Copilot.',
  'userDiagnostics.reason.reportOverrideIncluded': "Counted because the report's what-if seat selection includes it; the rules would not.",
  'userDiagnostics.reason.reportOverrideExcluded': "Not counted because the report's what-if seat selection leaves it out; the rules would count it.",
  'userDiagnostics.reason.unknown': 'No explanation was returned for this classification.',

  // History
  'userDiagnostics.history.title': 'Licence history',
  'userDiagnostics.history.hint':
    'Each row is when a licence refresh observed a holding start or end, not when Entra ID made the change: the real change happened between the previous refresh and the observing one.',
  'userDiagnostics.history.trackingBegan': 'History began {when}',
  'userDiagnostics.history.none': 'No history is recorded for this person.',
  'userDiagnostics.history.columnFrom': 'Observed from',
  'userDiagnostics.history.columnTo': 'Observed until',
  'userDiagnostics.history.open': 'Still held',
  'userDiagnostics.history.heldBeforeTracking': 'Held before history began',
  'userDiagnostics.history.truncated': 'Showing the {count} most recent rows.',
  'userDiagnostics.history.show': 'Show licence history',
  'userDiagnostics.history.hide': 'Hide licence history',
  'userDiagnostics.history.recordedByUserRefresh': 'Seen by a one-person refresh: the change happened at an unknown time before this',

  // One-person refresh
  'userDiagnostics.refresh.title': 'Refresh this person from the directory',
  'userDiagnostics.refresh.description':
    "Asks the importer to re-read this person's profile and licences from Microsoft Graph and correct the database for them alone. Nobody else's data is touched and the whole-tenant import is not reset. Organisation attributes are left to the scheduled import.",
  'userDiagnostics.refresh.when':
    'The importer picks the request up at its next safe point: the start of its next cycle, or as soon as a user import that is already running finishes. Reports show any change automatically once it is applied.',
  'userDiagnostics.refresh.button': 'Refresh from the directory...',
  'userDiagnostics.refresh.confirmText':
    "Re-read {upn} from Microsoft Graph and update their stored profile and licences to match? A licence is only removed when the directory's answer is complete.",
  'userDiagnostics.refresh.confirm': 'Refresh this person',
  'userDiagnostics.refresh.cancel': 'Cancel',
  'userDiagnostics.refresh.submitting': 'Sending the request...',
  'userDiagnostics.refresh.state.queued': 'Waiting for the importer since {when}. If no importer picks it up by {expires}, the request lapses.',
  'userDiagnostics.refresh.state.running': 'The importer is refreshing this person now (started {when}).',
  'userDiagnostics.refresh.state.expired':
    'The request lapsed: no importer picked it up within a day. Check on the Health page that the importer is running.',
  'userDiagnostics.refresh.checkAgain': 'Check now',
  'userDiagnostics.refresh.observedAt': 'Directory read {when}',
  'userDiagnostics.refresh.added': 'Licences added',
  'userDiagnostics.refresh.removed': 'Licences removed',
  'userDiagnostics.refresh.heldBack': 'Licences kept for lack of evidence',
  'userDiagnostics.refresh.profileChanges': 'Profile fields updated',
  'userDiagnostics.refresh.outcome.updated': 'Done: the database now matches the directory for this person.',
  'userDiagnostics.refresh.outcome.unchanged':
    'Done: the database already matched the directory. Nothing changed, and this person is now recorded as confirmed.',
  'userDiagnostics.refresh.outcome.partial':
    'Partly done: the changes the evidence supports were applied, but some were held back. See the details below.',
  'userDiagnostics.refresh.outcome.scopeExcluded': 'Not refreshed: this person is outside the configured user scope.',
  'userDiagnostics.refresh.outcome.scopeUnknown':
    'Not refreshed: the user scope could not be resolved, so it is not known whether this person is in it.',
  'userDiagnostics.refresh.outcome.notStored': 'Not refreshed: this person is not in the database. Only the scheduled user import adds people.',
  'userDiagnostics.refresh.outcome.userNotFoundInDirectory': 'Not refreshed: Microsoft Graph has no user with this identity. Nothing was changed.',
  'userDiagnostics.refresh.outcome.identityMismatch':
    'Not refreshed: the directory returned a different account from the stored one. Nothing was changed.',
  'userDiagnostics.refresh.outcome.permissionDenied':
    'Failed: Microsoft Graph refused the request. The app registration needs the User.Read.All application permission. Nothing was changed.',
  'userDiagnostics.refresh.outcome.graphUnavailable': 'Failed: Microsoft Graph could not be read. Nothing was changed; try again shortly.',
  'userDiagnostics.refresh.outcome.importDisabled': 'Not refreshed: the user metadata and licence import is switched off.',
  'userDiagnostics.refresh.outcome.interrupted':
    'Failed: the importer stopped part-way through more than once. Check the importer on the Health page, then try again.',
  'userDiagnostics.refresh.outcome.error': "Failed: an unexpected error. The importer's log has the details.",
  'userDiagnostics.refresh.note.incompleteDirectoryAnswer': "The directory's answer was incomplete, so no licence was removed on the strength of it.",
  'userDiagnostics.refresh.note.ambiguousLicenceKept':
    'A licence stored under an older product name was kept, because this person still holds that SKU. The scheduled import refiles it under the current name.',
  'userDiagnostics.refresh.note.userPrincipalNameChanged':
    'The directory now knows this person by a different sign-in name. The stored name is left for the scheduled import to update.',
  'userDiagnostics.refresh.note.noLicenceHistory': 'This database has no licence history tables, so the change was not recorded in history.',
  'userDiagnostics.refresh.note.reconciledAfterInterruption':
    'An earlier attempt was interrupted; this one re-read the directory and applied whatever was still different.',
  'userDiagnostics.refresh.note.profileNotUpdated': "The licences were refreshed, but the profile could not be updated. The importer's log has the details.",
  'userDiagnostics.refresh.note.reportCachesNotRefreshed':
    'Reports could not be told about the change straight away; they show it within about ten minutes, when their caches expire.',
  'userDiagnostics.refresh.unavailable.storageNotConfigured':
    'Not available: one-person refreshes are queued in Azure Table storage, and no Storage account is configured.',
  'userDiagnostics.refresh.unavailable.storageUnavailable': 'Not available right now: Azure Table storage could not be reached.',
  'userDiagnostics.refresh.unavailable.importDisabled': 'Not available: the user metadata and licence import is switched off.',
  'userDiagnostics.refresh.unavailable.notStored':
    'Not available: this person is not in the database. Only the scheduled user import adds people - see Administration > User import.',
  'userDiagnostics.refresh.unavailable.scopeExcluded': 'Not available: this person is outside the configured user scope.',
  'userDiagnostics.refresh.unavailable.scopeUnknown': 'Not available until the user scope can be resolved.',
  'userDiagnostics.refresh.unavailable.queueFull': 'Too many refreshes are already waiting. Try again once the importer has worked through them.',
  'userDiagnostics.refresh.recent': 'Recent requests for this person',
  'userDiagnostics.refresh.columnRequested': 'Requested',
  'userDiagnostics.refresh.columnBy': 'By',
  'userDiagnostics.refresh.columnResult': 'Result',
  'userDiagnostics.refresh.status.queued': 'Waiting',
  'userDiagnostics.refresh.status.running': 'Running',
  'userDiagnostics.refresh.status.expired': 'Lapsed',
  'userDiagnostics.refresh.status.updated': 'Updated',
  'userDiagnostics.refresh.status.unchanged': 'Already correct',
  'userDiagnostics.refresh.status.partial': 'Partly applied',
  'userDiagnostics.refresh.status.notRefreshed': 'Not refreshed',
  'userDiagnostics.refresh.status.failed': 'Failed',
  'userDiagnostics.refresh.field.accountEnabled': 'Account enabled',
  'userDiagnostics.refresh.field.mail': 'Email address',
  'userDiagnostics.refresh.field.postalCode': 'Postal code',
  'userDiagnostics.refresh.field.objectId': 'Entra object ID',
  'userDiagnostics.refresh.field.department': 'Department',
  'userDiagnostics.refresh.field.jobTitle': 'Job title',
  'userDiagnostics.refresh.field.officeLocation': 'Office location',
  'userDiagnostics.refresh.field.usageLocation': 'Usage location',
  'userDiagnostics.refresh.field.country': 'Country or region',
  'userDiagnostics.refresh.field.stateOrProvince': 'State or province',
  'userDiagnostics.refresh.field.companyName': 'Company',
  'userDiagnostics.refresh.field.manager': 'Manager',

  // Live comparison
  'userDiagnostics.live.title': 'Compare with Microsoft Graph',
  'userDiagnostics.live.description':
    "Reads this person's licences from the directory now, with the solution's app registration, and compares them with what the database holds. Read-only: nothing is changed.",
  'userDiagnostics.live.button': 'Compare with Microsoft Graph',
  'userDiagnostics.live.loading': 'Reading the directory...',
  'userDiagnostics.live.notAllowed.scopeExcluded': 'Not available: this person is outside the configured user scope.',
  'userDiagnostics.live.notAllowed.scopeUnknown': 'Not available until the user scope can be resolved.',
  'userDiagnostics.live.failed.userNotFoundInDirectory': 'Microsoft Graph has no user with this identity. The person may have been deleted or renamed.',
  'userDiagnostics.live.failed.permissionDenied': 'Microsoft Graph refused the request (HTTP {status}). The app registration needs the User.Read.All application permission.',
  'userDiagnostics.live.failed.graphUnavailable': 'Microsoft Graph could not be read just now. Try again shortly.',
  'userDiagnostics.live.failed.credentialsUnavailable': "The solution's app registration credentials could not be loaded.",
  'userDiagnostics.live.failed.timeout': 'Microsoft Graph did not answer in time. Try again shortly.',
  'userDiagnostics.live.failed.identityMismatch': 'Microsoft Graph returned a different person for this name, so no comparison was made.',
  'userDiagnostics.live.observedAt': 'Observed {when}',
  'userDiagnostics.live.incomplete':
    'The directory answer was incomplete, so a licence missing from it does not prove the database is wrong. Rows marked "Cannot tell" need another look.',
  'userDiagnostics.live.renamed': 'The directory now knows this person as {upn}.',
  'userDiagnostics.live.objectIdMismatch': 'The directory object id differs from the stored one: these may be different accounts.',
  'userDiagnostics.live.columnLicence': 'Licence',
  'userDiagnostics.live.columnStored': 'In database',
  'userDiagnostics.live.columnLive': 'In directory',
  'userDiagnostics.live.columnResult': 'Result',
  'userDiagnostics.live.columnDetail': 'Detail',
  'userDiagnostics.live.none': 'Neither the database nor the directory has any licence for this person.',
  'userDiagnostics.live.comparison.match': 'Match',
  'userDiagnostics.live.comparison.storedOnly': 'Only in database',
  'userDiagnostics.live.comparison.liveOnly': 'Only in directory',
  'userDiagnostics.live.comparison.indeterminate': 'Cannot tell',
  'userDiagnostics.live.mapping.collapsed': 'Several SKUs share this licence name, so the database cannot tell them apart.',
  'userDiagnostics.live.mapping.unmapped': 'The importer has not filed this SKU under any licence yet.',
  'userDiagnostics.live.mapping.catalogueDrift': 'Stored under an older product name; the next import refiles it under the current one.',
  'userDiagnostics.live.path.direct': 'Assigned directly',
  'userDiagnostics.live.path.group': 'Assigned through a group',
  'userDiagnostics.live.assignmentError': 'Assignment state {state}, error {error}',
  'userDiagnostics.live.copilotPlans.enabled': 'Copilot seat plans switched on',
  'userDiagnostics.live.copilotPlans.disabled': 'Copilot seat plans switched off for this person',
  'userDiagnostics.live.copilotPlans.pending': 'Copilot seat plans not yet active',

  // Report verdict
  'userDiagnostics.verdict.title': 'Why does a report count this person this way?',
  'userDiagnostics.verdict.description':
    "Re-runs a report's own licence rule for this one person against the data stored now. If the data has changed since you viewed the report, the answer can differ from what you saw.",
  'userDiagnostics.verdict.report': 'Report',
  'userDiagnostics.verdict.report.copilotAdoption': 'Copilot Adoption',
  'userDiagnostics.verdict.report.licenceActivity': 'Licence Activity',
  'userDiagnostics.verdict.period': 'Period',
  'userDiagnostics.verdict.rollingDays': 'Last {days} days',
  'userDiagnostics.verdict.customRange': 'From {from} to {to}',
  'userDiagnostics.verdict.fromReport': 'Using the period and seat selection of the report you came from.',
  'userDiagnostics.verdict.button': 'Explain',
  'userDiagnostics.verdict.loading': 'Re-running the report rule...',
  'userDiagnostics.verdict.licensed': 'Licensed',
  'userDiagnostics.verdict.unlicensed': 'Unlicensed',
  'userDiagnostics.verdict.basis.currentAssignments': "Decided from the licences stored now, whatever the period.",
  'userDiagnostics.verdict.basis.licenceHistory': 'Decided from licences held at any point in the period, using licence history.',
  'userDiagnostics.verdict.seatSelection.automatic': 'Seat licences chosen by the classification rules.',
  'userDiagnostics.verdict.seatSelection.reportOverride': "Seat licences chosen by the report's what-if selection.",
  'userDiagnostics.verdict.counted': 'Licences the report counts',
  'userDiagnostics.verdict.noneCounted': 'None.',
  'userDiagnostics.verdict.deciding': 'Why licensed',
  'userDiagnostics.verdict.noDeciding': 'This person holds none of the counted licences, so the report treats them as unlicensed.',
  'userDiagnostics.verdict.current': 'Held now',
  'userDiagnostics.verdict.held': 'Held from {from} to {to}',
  'userDiagnostics.verdict.heldOpen': 'Held from {from}, still held',
  'userDiagnostics.verdict.recomputed': 'Recomputed {when} from the data stored now.',
  'userDiagnostics.verdict.note.noSeatLicenceTypes': 'No licence counts as a Copilot seat, so nobody can be licensed in this report.',
  'userDiagnostics.verdict.note.historyPartialForPeriod': 'The period starts before licence history began. Earlier holdings are unknown; licences already held when history began count from the start of the period.',
  'userDiagnostics.verdict.note.historyUnavailableForPeriod': 'This is a past period but the database has no licence history, so the licences stored now were used.',
  'userDiagnostics.verdict.note.rollingWindowUsesCurrentAssignments':
    'A rolling period uses the licences stored now: someone whose licence was removed yesterday counts as unlicensed for the whole period.',
  'userDiagnostics.verdict.note.licenceActivityUsesCurrentAssignments': 'Licence Activity always uses the licences stored now; the period only selects activity.',
  'userDiagnostics.verdict.note.guestExcludedFromUnlicensedLists': 'This is a guest account. Guests are left out of the unlicensed and licence-opportunity lists.',
  'userDiagnostics.verdict.note.noMatchingLicenceTypes': 'The selected licences no longer exist.',
  'userDiagnostics.verdict.showSql': 'Show the query',
  'userDiagnostics.verdict.copySql': 'Copy the query',
  'userDiagnostics.verdict.sqlCopied': 'Query copied to the clipboard',

  // Copilot rules
  'userDiagnostics.rules.title': 'Copilot seat rules and catalogue',
  'userDiagnostics.rules.description':
    "Which licences count as Microsoft 365 Copilot seats, and why. Rows the catalogue and the rules disagree about are flagged: the rules may be out of date for that SKU. The rules are not changed here - test a what-if seat selection on Copilot Adoption.",
  'userDiagnostics.rules.version': 'Rules version {version}, build {build}, licensing catalogue {hash} ({count} SKUs)',
  'userDiagnostics.rules.seatPrefixes': 'Counted SKU prefixes',
  'userDiagnostics.rules.excludedPrefixes': 'Excluded SKU prefixes',
  'userDiagnostics.rules.seatPlans': 'Service plans that make a Copilot seat in the catalogue',
  'userDiagnostics.rules.columnAssigned': 'Assigned',
  'userDiagnostics.rules.gap': 'Rules and catalogue disagree',
  'userDiagnostics.rules.show': 'Show every licence in the tenant ({count})',
  'userDiagnostics.rules.hide': 'Hide the licence list',

  // Coverage
  'userDiagnostics.coverage.title': 'Data held and import freshness',
  'userDiagnostics.coverage.description':
    'The newest record of each kind stored for this person, and what is known about the import that feeds it. No record is not the same as no activity: the source may be switched off, out of scope, or not imported yet.',
  'userDiagnostics.coverage.load': 'Check data freshness',
  'userDiagnostics.coverage.loading': 'Reading the newest record of each kind...',
  'userDiagnostics.coverage.columnData': 'Data',
  'userDiagnostics.coverage.columnNewest': 'Newest record',
  'userDiagnostics.coverage.columnSource': 'Feeding import (whole tenant)',
  'userDiagnostics.coverage.columnReplay': 'Can it be re-read for one person?',
  'userDiagnostics.coverage.none': 'None stored',
  'userDiagnostics.coverage.importOff': 'Import switched off',
  'userDiagnostics.coverage.readFailed': 'Could not be read',
  'userDiagnostics.coverage.kind.eventTime': 'When it happened',
  'userDiagnostics.coverage.kind.reportDate': 'Report day; last activity {lastActivity}',
  'userDiagnostics.coverage.kind.reportDateNoActivity': 'Report day; no activity reported',
  'userDiagnostics.coverage.kind.observedDate': 'When the import observed it',
  'userDiagnostics.coverage.source.recorded': 'Last completed {when}',
  'userDiagnostics.coverage.source.notRecorded': 'No completion recorded',
  'userDiagnostics.coverage.source.noStampForSource': 'Not recorded per run; see the Health page',
  'userDiagnostics.coverage.source.storageNotConfigured': 'Not available without Azure Storage',
  'userDiagnostics.coverage.source.storageUnavailable': 'Azure Table storage could not be read',
  'userDiagnostics.coverage.replay.perUserPossible': 'The source answers per person, but only the scheduled import re-reads it today',
  'userDiagnostics.coverage.replay.sourceWideOnly': 'No - only the whole source can be re-read',
  'userDiagnostics.coverage.replay.notReconstructable': 'No - activity that was not captured cannot be recovered',
  'userDiagnostics.coverage.category.copilot-usage-report': 'Copilot usage report (Microsoft)',

  // Entry point
  'userDiagnostics.troubleshoot': 'Troubleshoot this person',
  'userDiagnostics.troubleshootHint': 'Open the licence and data diagnostics for this person in User lookup.',
  'userDiagnostics.noStoredUserLookup': 'This person is not in the database, so only the diagnostics below are available.',
} as const;

export default userDiagnostics;
