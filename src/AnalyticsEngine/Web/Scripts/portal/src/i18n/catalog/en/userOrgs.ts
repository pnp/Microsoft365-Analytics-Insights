/**
 * English text for the User organisations admin page.
 *
 * Every key here must have a Spanish counterpart in `../es/userOrgs.ts`; the type of that module
 * makes a missing one a build failure.
 *
 * Note what is deliberately NOT here: the name an administrator gives an organisation type, and the
 * values that arrive from Entra or a CSV. Those are tenant data, invented at runtime, so they are
 * rendered as-is in either language - a catalogue cannot hold something it has never seen.
 */
export const userOrgs = {
  // Page shell
  'userOrgs.page.title': 'User organisations',
  'userOrgs.page.new': 'New organisation type',
  'userOrgs.page.intro':
    'Group users by something your directory does not track reliably - a cost centre, a business unit, a team from an HR export. Each type takes its values either from a custom Microsoft Entra attribute, read on every user import, or from a CSV you upload here. A user has at most one value per type.',
  'userOrgs.page.loading': 'Loading organisation types...',

  // Types table
  'userOrgs.types.title': 'Organisation types',
  'userOrgs.types.empty': 'None defined yet. Create one to start grouping users.',
  'userOrgs.column.name': 'Name',
  'userOrgs.column.source': 'Source',
  'userOrgs.column.lastRefreshed': 'Last refreshed',
  'userOrgs.column.usersAssigned': 'Users assigned',
  'userOrgs.column.distinctValues': 'Distinct values',
  'userOrgs.column.state': 'State',
  'userOrgs.column.actions': 'Actions',
  'userOrgs.source.entra': 'Entra attribute',
  'userOrgs.source.csv': 'CSV upload',
  'userOrgs.source.lastImported': 'last imported {when} by {who} ({status})',
  'userOrgs.source.unknownUser': 'unknown',
  'userOrgs.state.enabled': 'Enabled',
  'userOrgs.state.disabled': 'Disabled',
  'userOrgs.action.edit': 'Edit',
  'userOrgs.action.delete': 'Delete',
  'userOrgs.types.viewUsers.one': 'View the {count} user in {name}',
  'userOrgs.types.viewUsers.other': 'View the {count} users in {name}',

  // "Last refreshed" for a type that never has been, by what it is waiting for
  'userOrgs.lastRefreshed.waitingForImport': 'Waiting for the next user import',
  'userOrgs.lastRefreshed.noImportYet': 'No successful import yet',
  'userOrgs.lastRefreshed.clearedBySourceChange': 'Cleared when the source changed',
  'userOrgs.lastRefreshed.never': 'Never',
  'userOrgs.lastRefreshed.holdsLists':
    "Can't be imported: its attribute holds a list of values. Point the type at an attribute with one value per user.",

  // Job status, as shown beside a CSV type
  'userOrgs.status.pending': 'pending',
  'userOrgs.status.running': 'running',
  'userOrgs.status.succeeded': 'succeeded',
  'userOrgs.status.failed': 'failed',
  'userOrgs.status.cancelled': 'cancelled',
  'userOrgs.status.interrupted': 'interrupted',

  // Toasts and confirmations
  'userOrgs.toast.created': 'Created {name}.',
  'userOrgs.toast.saved': 'Saved {name}.',
  'userOrgs.toast.deleted': 'Deleted {name}.',
  'userOrgs.delete.confirm':
    'Delete "{name}"?\n\nThis removes the organisation values of {count} user(s) and cannot be undone.',

  // Entra explainer card
  'userOrgs.entraCard.title': 'How Entra-sourced types are kept up to date',
  'userOrgs.entraCard.body':
    'These are read during the normal user import, so values appear after the next import cycle. Any change to which Entra attributes are in use - adding or deleting a type, enabling or disabling one, pointing one at a different attribute, or switching one to CSV - makes the next cycle re-read every user once so the new set is populated for people who have not otherwise changed. That one cycle takes longer than usual, and on a large tenant noticeably so.',
  'userOrgs.entraCard.lastRefreshed':
    'Last refreshed shows when a user import last read the attribute and applied its values. That happens every importer cycle by default, and about once a day when ImportAggressiveness is set to Balanced or Gentle; a type saved while an import is already running waits for the next one. If every Entra type stops moving at once, Microsoft Graph is probably rejecting one of the attributes - the user import then carries on without any of them. Test each type to find the one at fault, or check the importer log.',

  // CSV import card
  'userOrgs.import.cardTitle': 'Import {name} from a file',
  'userOrgs.import.cardIntro':
    "A CSV with a user column and an organisation column, in either order. A row with a blank organisation clears that user's value.",

  // What an uploaded file looks like - shown when a CSV type is created, and beside its upload
  'userOrgs.csvFormat.title': 'What the file should look like',
  'userOrgs.csvFormat.intro':
    "Two columns: each person's user principal name, and the value they have for this type. For example:",
  'userOrgs.csvFormat.defaultColumn': 'Organisation',
  'userOrgs.csvFormat.example':
    'UserPrincipalName,{column}\nalex.wilber@contoso.com,Finance\nmegan.bowen@contoso.com,Research & Development\nadele.vance@contoso.com,',
  'userOrgs.csvFormat.exampleAria': 'Example file',
  'userOrgs.csvFormat.ruleHeader':
    'The header row is optional, and the two columns can be in either order. The user column is recognised by a header such as UserPrincipalName, UPN, User or Email; when the file has both an email column and a UserPrincipalName or UPN column, use the UPN one. With no header row, the first column is taken as the user and the second as the value.',
  'userOrgs.csvFormat.ruleSeparator':
    'Separate the columns with commas, semicolons, tabs or pipes - the separator is detected. Save the file as UTF-8 ("CSV UTF-8" in Excel). Other encodings are refused because accented and non-Latin names would be corrupted.',
  'userOrgs.csvFormat.ruleBlank': "A row with no value, like the last one above, clears that person's value.",
  'userOrgs.csvFormat.ruleUsers':
    'Use the user principal names this product imports from Microsoft Entra. Before anything is imported, a preview shows how many rows match a user; rows that match nobody are skipped.',
  'userOrgs.csvFormat.ruleColumns':
    'Name the value column after this organisation type, as in the example. If the file has more columns, you will be asked which one holds the user principal name and which one holds the value.',

  // Create / edit dialog
  'userOrgs.dialog.editTitle': 'Edit {name}',
  'userOrgs.dialog.newTitle': 'New organisation type',
  'userOrgs.dialog.nameLabel': 'Name',
  'userOrgs.dialog.nameHint':
    "The label this grouping is shown under - on the user lookup page, and as a property in the Copilot Adoption report's filter. For example, Cost Centre.",
  'userOrgs.dialog.sourceLabel': 'Where the values come from',
  'userOrgs.dialog.sourceEntra': 'A custom Microsoft Entra attribute, read on every user import',
  'userOrgs.dialog.sourceCsv': 'A CSV file uploaded here',
  'userOrgs.dialog.discardWarning.one':
    'Saving this discards the {count} value this type holds today. It was read from a source that will no longer be the source of truth for it, so leaving it would show a stale value indefinitely - a CSV Merge in particular never touches users the file does not mention.',
  'userOrgs.dialog.discardWarning.other':
    'Saving this discards the {count} values this type holds today. They were read from a source that will no longer be the source of truth for it, so leaving them would show stale values indefinitely - a CSV Merge in particular never touches users the file does not mention.',
  'userOrgs.dialog.attributeLabel': 'Entra attribute',
  'userOrgs.dialog.attributeHint':
    'One of extensionAttribute1-15, employeeId, employeeType, employeeOrgData.costCenter, employeeOrgData.division, a directory extension (extension_APPID_NAME, with the application id and the extension name) or a schema extension.',
  'userOrgs.dialog.testLabel': 'Test it against a user',
  'userOrgs.dialog.testHint':
    'Required before saving. Microsoft Graph rejects the whole user import if it does not recognise the attribute, so it has to be proved first.',
  'userOrgs.dialog.testButton': 'Test',
  'userOrgs.dialog.testing': 'Testing...',
  'userOrgs.dialog.testFirst': 'Test the attribute against a user before saving.',
  'userOrgs.dialog.enabled': 'Enabled - included in imports',
  'userOrgs.dialog.disabled': 'Disabled - not imported',
  'userOrgs.dialog.cancel': 'Cancel',
  'userOrgs.dialog.save': 'Save',
  'userOrgs.dialog.saving': 'Saving...',

  // Test outcome
  'userOrgs.test.failed': 'The attribute could not be read.',
  'userOrgs.test.succeeded': 'The attribute was read successfully.',
  'userOrgs.test.graphProperty': 'Graph property',
  'userOrgs.test.rawValue': 'Value from Graph',
  'userOrgs.test.storedAs': 'Stored as',
  'userOrgs.test.unverifiedBeforeDiscard':
    'Saving this change discards the values this type holds now, so it can only be saved once a test finds a value.',

  // CSV import panel - controls
  'userOrgs.csv.fileLabel': 'Choose a CSV file',
  'userOrgs.csv.clear': 'Clear',
  'userOrgs.csv.previewing': 'Reading the file and checking every user...',
  'userOrgs.csv.modeLabel': 'What should happen to users who are not in the file?',
  'userOrgs.csv.modeMerge': 'Merge - leave them exactly as they are',
  'userOrgs.csv.modeReplace': 'Replace - clear their {name} value (the file is the complete list)',
  'userOrgs.csv.import.one': 'Import {count} row',
  'userOrgs.csv.import.other': 'Import {count} rows',
  'userOrgs.csv.columnChooser.hint': 'This file has {count} columns. Choose which columns to import.',
  'userOrgs.csv.columnChooser.user': 'User principal name column',
  'userOrgs.csv.columnChooser.value': 'Value column',
  'userOrgs.csv.columnChooser.fallback': 'Column {number}',
  'userOrgs.csv.columnChooser.placeholder': 'Choose a column',

  // Replace warning
  'userOrgs.csv.clearWarning.one': 'This will clear 1 user\u2019s {name} value.',
  'userOrgs.csv.clearWarning.other': 'This will clear {count} users\u2019 {name} value.',
  'userOrgs.csv.clearWarning.keeps':
    'The file keeps {kept} of the {assigned} users who have one today. Anyone it does not cover loses theirs.',
  'userOrgs.csv.clearWarning.unknown.one':
    '1 row in the file matches no user at all - if that is unexpected, check the file before continuing.',
  'userOrgs.csv.clearWarning.unknown.other':
    '{count} rows in the file match no user at all - if that is unexpected, check the file before continuing.',
  'userOrgs.csv.confirmClear': 'I understand, clear the users this file does not cover',
  'userOrgs.csv.mergeClearWarning.one':
    "This will clear 1 user's {name} value: the file lists them with an empty value.",
  'userOrgs.csv.mergeClearWarning.other':
    "This will clear {count} users' {name} value: the file lists them with an empty value.",
  'userOrgs.csv.confirmMergeClear': 'I understand, clear the values the file leaves empty',

  // Preview
  'userOrgs.csv.headerFound':
    'Header row found: "{upnColumn}" and "{orgColumn}", {delimiter}-separated.',
  'userOrgs.csv.headerMissing':
    'No header row recognised, so the first column is treated as the user and the second as the organisation. {delimiter}-separated.',
  'userOrgs.csv.matchSummary': '{matched} of the {total} rows in the file match a user in this database.',
  'userOrgs.csv.unknownRows.one':
    '1 row in the file matches no user in this database and will be skipped. Check the file uses the same user principal names the product imports, and that it is not a partial export.',
  'userOrgs.csv.unknownRows.other':
    '{count} rows in the file match no user in this database and will be skipped. Check the file uses the same user principal names the product imports, and that it is not a partial export.',
  'userOrgs.csv.noMatches':
    'None of the rows match a user in this database. Check that the user column holds the user principal names this product imports (for example megan.bowen@contoso.com), not email addresses or display names.',
  'userOrgs.csv.previewAriaLabel': 'File preview',
  'userOrgs.csv.column.line': 'Line',
  'userOrgs.csv.column.user': 'User',
  'userOrgs.csv.column.organisation': 'Organisation',
  'userOrgs.csv.column.matches': 'Matches a user',
  'userOrgs.csv.clearsValue': 'clears the value',
  'userOrgs.csv.showingFirst': 'Showing the first {count} rows. The whole file is imported.',
  'userOrgs.csv.truncated.one':
    '1 row in this file has an organisation name longer than {max} characters. If it is imported, the name is stored shortened, and names that are identical for their first {max} characters become one organisation.',
  'userOrgs.csv.truncated.other':
    '{count} rows in this file have an organisation name longer than {max} characters. Any of those names that are imported are stored shortened, and names that are identical for their first {max} characters become one organisation.',
  'userOrgs.csv.problems':
    'Some rows cannot be used: {problems}. They are skipped and counted; the rest of the file still imports.',
  'userOrgs.csv.problemLine': 'line {line} ({reason})',
  'userOrgs.csv.problem.missingUserColumn': 'the user column is missing',
  'userOrgs.csv.problem.userEmptyOrTooLong': 'the user principal name is empty or too long',
  'userOrgs.csv.problem.notAValidUpn': 'the user value is not a valid user principal name',
  'userOrgs.csv.problem.tooManyValues':
    'the row has more values than the other rows - put a value that contains the separator in double quotes',
  'userOrgs.csv.problem.unknownUser': 'the user principal name does not match a user in this database',
  'userOrgs.csv.unusable.download.one': "Download the 1 row that can't be imported (CSV)",
  'userOrgs.csv.unusable.download.other': "Download the {count} rows that can't be imported (CSV)",
  'userOrgs.csv.unusable.truncated': 'The download lists the first {shown} of {count} rows.',
  'userOrgs.csv.unusable.defaultFileName': 'user-organisations',
  'userOrgs.csv.unusable.column.line': 'Line',
  'userOrgs.csv.unusable.column.user': 'User',
  'userOrgs.csv.unusable.column.value': '{name}',
  'userOrgs.csv.unusable.column.reason': 'Reason',
  'userOrgs.csv.blocking.notUtf8':
    "This file isn't saved as UTF-8, so accented and non-Latin names would be corrupted (the first problem is on line {line}). In Excel, use Save As and choose 'CSV UTF-8 (Comma delimited)', then choose the file again.",
  'userOrgs.csv.blocking.excelWorkbook':
    "This is an Excel workbook, not a CSV file. In Excel, use Save As and choose 'CSV UTF-8 (Comma delimited)', then choose that file.",
  'userOrgs.csv.blocking.notText':
    "This doesn't look like a text CSV file. Save it as 'CSV UTF-8 (Comma delimited)' and choose it again.",
  'userOrgs.csv.blocking.unterminatedQuote':
    "A quotation mark on line {line} is never closed, so the rest of the file can't be read as rows. Fix the quoting and choose the file again.",
  'userOrgs.csv.blocking.rowSpansLines':
    "Lines {line}-{lastLine} were read as one row, because a quotation mark on line {line} isn't closed on that line. Organisation names can't contain line breaks: remove the stray quotation mark, or put the whole value in quotation marks, and choose the file again.",
  'userOrgs.csv.blocking.chooseColumns':
    "This file has several columns. Choose which one holds each person's user principal name and which holds the value.",
  'userOrgs.csv.blocking.oneColumn':
    "This file has only one column. It needs two: each person's user principal name, and their value.",
  'userOrgs.csv.oneColumnSecondBlank':
    "Only one column holds anything in this file's first rows, so it can't tell whether the other is the values or an empty column after a list of names. If it is the values - blank on those rows, which clears them - choose the two columns below.",
  'userOrgs.csv.blocking.tooManyRows':
    'This file has more than {max} rows. Split it into smaller files and import them one at a time.',
  'userOrgs.csv.blocking.tooManyColumns':
    "Line {line} has more than {max} columns - more than any spreadsheet can hold - so this isn't a file of users and values. Check that you chose the right file, and that it was saved as a CSV.",
  'userOrgs.csv.blocking.noRows': 'This file has no rows to import.',
  'userOrgs.csv.blocking.noUsableRows': 'None of the rows in this file can be used.',
  'userOrgs.csv.blocking.generic': "This file can't be imported.",
  'userOrgs.csv.apiError.importInProgress':
    'Another import is already running for this organisation type. Wait for it to finish, then choose the file again.',
  'userOrgs.csv.apiError.draftNotFound': 'This preview has expired or was already imported. Choose the file again.',
  'userOrgs.csv.apiError.typeChanged':
    'The organisation type was changed after the preview. Check its settings and choose the file again.',
  'userOrgs.csv.apiError.typeNotFound': 'This organisation type no longer exists.',
  'userOrgs.csv.apiError.typeNotCsv': '{name} is not a CSV-sourced organisation type.',
  'userOrgs.csv.apiError.typeDisabled': '{name} is disabled. Enable it before importing a file.',
  'userOrgs.csv.apiError.noMatchingUsers':
    'None of the rows match a user in this database. Check the user column and choose the file again.',
  'userOrgs.csv.apiError.clearExceedsConfirmed':
    "This import would now clear {count} users' values, not the {confirmed} you confirmed. The data changed since the preview; choose the file again to see the new numbers.",
  'userOrgs.csv.apiError.noFile': 'Choose a CSV file before previewing it.',
  'userOrgs.csv.apiError.uploadUnreadable': 'The file could not be read. Choose it again, or save a new copy from Excel.',
  'userOrgs.csv.apiError.uploadTooLarge':
    'This file is larger than the {maxMb} MB upload limit. Split it into smaller files and import them one at a time.',
  'userOrgs.csv.apiError.invalidMode': 'Choose Merge or Replace before importing.',
  'userOrgs.csv.apiError.invalidColumns': 'Choose two different columns: one for the user principal name and one for the value.',

  // Job progress
  'userOrgs.job.waiting': 'Waiting to start...',
  'userOrgs.job.importing': 'Importing {count} rows... started at {time}.',
  'userOrgs.job.resumed': 'Resumed after the web app restarted.',
  'userOrgs.job.poll.warning':
    "Can't reach the server to check on this import. The import carries on regardless; still trying...",
  'userOrgs.job.poll.notFound':
    'This import can no longer be found. The organisation type may have been deleted.',
  'userOrgs.job.poll.sessionExpired':
    'Your session has expired. Reload the page to see how the import finished.',
  'userOrgs.job.interrupted':
    "This import stopped reporting progress, usually because the web app restarted. Nothing was changed: an import is saved in one step with its success status, so it cannot be left half done. The server will resume it automatically. If it has not restarted within a few minutes, upload the file again.",
  'userOrgs.job.failed': 'The import {status}. {message}',
  'userOrgs.job.finished': 'Import finished.',
  'userOrgs.job.nothingChanged.detail': 'Nothing changed. {reason}',
  'userOrgs.job.nothingChanged.unknown.one': '1 row matched no user.',
  'userOrgs.job.nothingChanged.unknown.other': '{count} rows matched no user.',
  'userOrgs.job.nothingChanged.sameValues': 'Everyone already had these values.',
  'userOrgs.job.changed': '{count} changed',
  'userOrgs.job.cleared': '{count} cleared',
  'userOrgs.job.unknownUsers': '{count} unknown user(s)',
  'userOrgs.job.unusableRows': '{count} unusable row(s)',
  'userOrgs.job.error.failed':
    'The import could not be completed. Nothing was changed. Try again; if it keeps failing, check the service logs.',
  'userOrgs.job.error.superseded':
    'This import was replaced by a later one for the same organisation type after it stopped reporting progress. The later import is the one that counts.',
  'userOrgs.job.error.typeChanged':
    'The organisation type was changed after the file was previewed, so nothing was imported. Check its settings and choose the file again.',
  'userOrgs.job.error.clearExceedsConfirmed':
    "Nothing was imported: by the time it ran, it would have cleared more users' values than you confirmed. Choose the file again to see the new numbers.",
  'userOrgs.job.error.interruptedRepeatedly':
    'The web app restarted during this import several times, so it was stopped. Nothing was changed. Upload the file again.',
  'userOrgs.lastImport.line': 'Last import: {date} by {who}. {outcome} {counts}',
  'userOrgs.history.title': 'Import history',
  'userOrgs.history.loading': 'Loading import history...',
  'userOrgs.history.empty': 'No imports yet.',
  'userOrgs.history.column.date': 'Date',
  'userOrgs.history.column.who': 'By',
  'userOrgs.history.column.mode': 'Mode',
  'userOrgs.history.column.status': 'Status',
  'userOrgs.history.column.counts': 'Counts',
  'userOrgs.history.column.reason': 'Reason',
  'userOrgs.history.mode.merge': 'Merge',
  'userOrgs.history.mode.replace': 'Replace',
  'userOrgs.history.reason.none': 'None',
  'userOrgs.history.outcome.succeeded': 'Succeeded.',
  'userOrgs.history.counts':
    '{changed} changed, {cleared} cleared, {unknown} unknown, {unusable} unusable',
  'userOrgs.history.column.changes': 'Changes',

  // What an import changed
  'userOrgs.changes.open': 'View changes',
  'userOrgs.changes.openAfterImport': 'See what changed',
  'userOrgs.changes.title': 'What this import changed',
  'userOrgs.changes.close': 'Close',
  'userOrgs.changes.importedBy': 'Imported {date} by {who} ({mode}) from {file}.',
  'userOrgs.changes.importedByNoFile': 'Imported {date} by {who} ({mode}).',
  'userOrgs.changes.counts': '{added} added, {changed} changed, {cleared} cleared',
  'userOrgs.changes.storage.tableStorage': 'This change list is kept in Azure Table Storage.',
  'userOrgs.changes.storage.memory':
    "This change list is kept in this web server's memory only, because no storage account was set up when it was written. It's lost when the web app restarts, and other web servers can't see it.",
  'userOrgs.changes.truncated':
    'Only the first {stored} of {count} changes were kept: the in-memory change list is limited in size.',
  'userOrgs.changes.status.pending': 'The change list is still being written. Try again in a moment.',
  'userOrgs.changes.status.none': 'There is no change list for this import, because it was not applied.',
  'userOrgs.changes.status.missingMemory':
    'This change list was kept in memory and is no longer available: the web app has restarted since, or it is on another web server.',
  'userOrgs.changes.status.missingTable':
    'This change list could not be found in the storage account. It may have been deleted.',
  'userOrgs.changes.status.unavailable':
    "The storage account can't be reached right now, so the change list can't be shown. Try again later.",
  'userOrgs.changes.retry': 'Try again',
  'userOrgs.changes.searchPlaceholder': 'Search by user principal name',
  'userOrgs.changes.tableLabel': 'Changes made by this import',
  'userOrgs.changes.column.user': 'User',
  'userOrgs.changes.column.before': 'Before',
  'userOrgs.changes.column.after': 'After',
  'userOrgs.changes.column.change': 'Change',
  'userOrgs.changes.kind.added': 'Added',
  'userOrgs.changes.kind.changed': 'Changed',
  'userOrgs.changes.kind.cleared': 'Cleared',
  'userOrgs.changes.noValue': '(no value)',
  'userOrgs.changes.empty': 'This import changed nobody: everyone already had these values.',
  'userOrgs.changes.noMatches': 'No changes for users whose name starts with \u201c{search}\u201d.',
  'userOrgs.changes.loading': 'Loading changes...',
  'userOrgs.changes.loadFailed': "The changes couldn't be loaded.",
  'userOrgs.changes.loadMore': 'Show more',
  'userOrgs.changes.showing': 'Showing {count} of {total}',
  'userOrgs.changes.download': 'Download all changes (CSV)',
  'userOrgs.changes.downloading': 'Downloading... {count} changes so far',
  'userOrgs.changes.downloadFailed': "The download didn't finish. Try again.",
  'userOrgs.changes.defaultFileName': 'user-organisation-changes',

  // CSV column separators, as the preview names them
  'userOrgs.csv.delimiter.comma': 'comma',
  'userOrgs.csv.delimiter.semicolon': 'semicolon',
  'userOrgs.csv.delimiter.tab': 'tab',
  'userOrgs.csv.delimiter.pipe': 'pipe',

  // Messages the API sends as a code (UserOrgMessageCodes.cs); the server's English is only a fallback
  'userOrgs.message.noType': 'No organisation type was supplied.',
  'userOrgs.message.nameRequired': 'An organisation type name is required.',
  'userOrgs.message.nameTooLong': 'An organisation type name can be at most {max} characters.',
  'userOrgs.message.duplicateName': 'An organisation type called \u201c{name}\u201d already exists.',
  'userOrgs.message.invalidSource': 'An organisation type takes its values either from an Entra attribute or from a CSV file.',
  'userOrgs.message.typeGone': 'That organisation type no longer exists - it may have been deleted in another session.',
  'userOrgs.message.typeChangedElsewhere':
    'Someone else changed \u201c{name}\u201d while you were editing it, so your changes were not saved. Close this and open it again to see theirs.',
  'userOrgs.message.discardExceedsConfirmed':
    '\u201c{name}\u201d now holds {count} values - more than when you opened it - and this change would discard them, so nothing was saved. Close this and open it again to see them before you decide.',
  'userOrgs.message.typeChangedBeforeDelete':
    'Someone else changed this organisation type after the page loaded, so it was not deleted. The list now shows it as it is: check it, and delete it again if you still want to.',
  'userOrgs.message.importRunningChange': 'An import for {name} is running. Wait for it to finish before changing the type.',
  'userOrgs.message.importRunningDelete':
    'An import for this organisation type is running. Wait for it to finish before deleting the type.',
  'userOrgs.message.attributeRequired': 'An Entra attribute name is required.',
  'userOrgs.message.attributeTooLong': 'An attribute name can be at most {max} characters.',
  'userOrgs.message.openExtension':
    "Open extensions (read with $expand=extensions) can't be used: Microsoft Graph does not support $expand on /users/delta, which is how this product tracks user changes. Use a directory extension, a schema extension, or one of the extensionAttribute1-15 slots instead.",
  'userOrgs.message.attributeHasSpaces':
    '\u201c{attribute}\u201d is not a valid attribute name - attribute names cannot contain spaces.',
  'userOrgs.message.badDirectoryExtension':
    '\u201c{attribute}\u201d looks like a directory extension but is not in the required format extension_<application id>_<name>, where the application id is exactly 32 hexadecimal characters.',
  'userOrgs.message.tooManyDots': '\u201c{attribute}\u201d is not a valid attribute name - it has more than one \u201c.\u201d separator.',
  'userOrgs.message.nothingAfterDot':
    '\u201c{attribute}\u201d is not a valid attribute name - nothing follows the \u201c.\u201d separator.',
  'userOrgs.message.notEmployeeOrgDataProperty':
    '\u201c{property}\u201d is not a property of employeeOrgData. Microsoft Graph defines only costCenter and division.',
  'userOrgs.message.directoryExtensionSubProperty':
    '\u201c{attribute}\u201d is not valid. A directory extension is a single flat property, so it cannot have a \u201c.\u201d sub-property.',
  'userOrgs.message.unknownContainer':
    '\u201c{container}\u201d is not a recognised property. Expected one of the extensionAttribute1-15 slots, employeeOrgData.costCenter, employeeOrgData.division, a directory extension (extension_<application id>_<name>), or a schema extension (<owner>_<schema name>.<property>).',
  'userOrgs.message.badSchemaProperty': '\u201c{property}\u201d is not a valid schema extension property name.',
  'userOrgs.message.employeeOrgDataContainer':
    'employeeOrgData is a container, not a value. Use employeeOrgData.costCenter or employeeOrgData.division.',
  'userOrgs.message.onPremisesContainer':
    'onPremisesExtensionAttributes is a container, not a value. Use one of its slots, for example extensionAttribute1.',
  'userOrgs.message.unsupportedAttribute':
    '\u201c{attribute}\u201d is not a supported organisation attribute. Expected one of the extensionAttribute1-15 slots, employeeId or employeeType, employeeOrgData.costCenter, employeeOrgData.division, a directory extension (extension_<application id>_<name>), or a schema extension (<owner>_<schema name>.<property>).',
  'userOrgs.message.badOnPremisesAttribute':
    '\u201c{attribute}\u201d is not a valid on-premises extension attribute. Expected extensionAttribute1 through extensionAttribute15.',
  'userOrgs.message.noTestRequest': 'No test request was supplied.',
  'userOrgs.message.upnRequired': 'A user principal name is required.',
  'userOrgs.message.graphAuthFailed':
    "Could not authenticate to Microsoft Graph. Check that the app registration's client secret or certificate has not expired, and that it has the User.Read.All application permission with admin consent. The service logs have the detail.",
  'userOrgs.message.userNotFound': 'That user was not found in this tenant. Check the user principal name.',
  'userOrgs.message.propertyRejected':
    'Microsoft Graph does not recognise the property \u201c{property}\u201d on a user. Check the attribute name - a directory extension must be in the full extension_<application id>_<name> form. This attribute cannot be used until Graph accepts it: saving it would make every user import fail.',
  'userOrgs.message.notAuthorised':
    'This app registration is not allowed to read that user or that property. Reading users needs the User.Read.All application permission, granted with admin consent.',
  'userOrgs.message.throttled': 'Microsoft Graph is throttling this tenant right now. Wait a moment and try again.',
  'userOrgs.message.graphError':
    'Microsoft Graph returned HTTP {status}. Try again in a moment; the service logs have the detail if it keeps happening.',
  'userOrgs.message.unreadableResponse': 'Microsoft Graph returned a response that could not be read.',
  'userOrgs.message.multiValued':
    '\u201c{property}\u201d holds a list of values, not one value, so it cannot be an organisation type: a user can be in only one organisation of each type.',
  'userOrgs.message.noValue':
    'The attribute was read successfully, but this user has no value for it. During an import, that means their organisation value would be cleared.',
  'userOrgs.message.noValueUnverified':
    "Microsoft Graph accepted the schema extension '{container}', but this user has no value for it - and Graph does not check the property name after the dot, so a misspelt name reads exactly like this. Test with a user who has a value to be sure the name is right.",
  'userOrgs.message.wouldTruncate': 'The value is longer than {max} characters and would be shortened when stored.',
  'userOrgs.message.discoveryAuthFailed':
    "Could not authenticate to Microsoft Graph to look for directory extensions. Check the app registration's credentials and permissions; the service logs have the detail. You can still type a directory extension name in full and test it.",
  'userOrgs.message.discoveryForbidden':
    'This app registration cannot list directory extensions - that call needs the Directory.Read.All application permission, which is more than the rest of this product requires. You can still type a directory extension name in full and test it.',
  'userOrgs.message.discoveryGraphError':
    'Microsoft Graph could not list directory extensions (HTTP {status}). You can still type a directory extension name in full and test it.',
  'userOrgs.message.discoveryNoneReturned':
    "No directory extensions were returned. Microsoft Graph's discovery call is documented as returning nothing on tenants with more than 1,000 service principals, so this does not necessarily mean the tenant has none. You can type a directory extension name in full and test it.",
  'userOrgs.message.discoveryUnreachable':
    'Could not reach Microsoft Graph to list directory extensions. The service logs have the detail. You can still type a directory extension name in full and test it.',
  'userOrgs.message.jobGone': 'That import could not be found. Reload the page to see the latest imports.',
  'userOrgs.message.valueGone': 'That organisation no longer exists - a later import may have removed it.',
  'userOrgs.message.notFromPortal': 'This request did not come from the portal. Reload the page and try again.',
  'userOrgs.message.unexpected': 'Something went wrong handling that request. Check the service logs for details.',

  // Who is in each organisation
  'userOrgs.browse.title': 'Who is in each organisation',
  'userOrgs.browse.intro':
    'Choose an organisation type, then an organisation, to see the users in it. Organisations are listed largest first; one that nobody is in any more is listed with 0.',
  'userOrgs.browse.typeLabel': 'Organisation type',
  'userOrgs.browse.searchOrgsPlaceholder': 'Search organisations',
  'userOrgs.browse.searchMembersPlaceholder': 'Search by user principal name',
  'userOrgs.browse.orgsTableLabel': 'Organisations',
  'userOrgs.browse.column.organisation': 'Organisation',
  'userOrgs.browse.column.users': 'Users',
  'userOrgs.browse.column.user': 'User',
  'userOrgs.browse.column.department': 'Department',
  'userOrgs.browse.column.jobTitle': 'Job title',
  'userOrgs.browse.membersOf': 'Users in {name}',
  'userOrgs.browse.pickOrg': 'Choose an organisation to see who is in it.',
  'userOrgs.browse.noOrgs': 'This type has no organisations yet.',
  'userOrgs.browse.noOrgMatches': 'No organisation matches that search.',
  'userOrgs.browse.noMembers': 'Nobody is in this organisation at the moment.',
  'userOrgs.browse.noMemberMatches': 'Nobody in this organisation matches that search.',
  'userOrgs.browse.accountDisabled': 'Account disabled',
  'userOrgs.browse.loading': 'Loading...',
  'userOrgs.browse.loadFailed':
    'This list could not be loaded. Try again, or refresh the page - the organisation may have been changed or deleted in another session.',
  'userOrgs.browse.retry': 'Try again',
  'userOrgs.browse.showing': 'Showing {from}\u2013{to} of {total}',
  'userOrgs.browse.previous': 'Previous',
  'userOrgs.browse.next': 'Next',
} as const;

export default userOrgs;
