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

  // CSV import card
  'userOrgs.import.cardTitle': 'Import {name} from a file',
  'userOrgs.import.cardIntro':
    "A CSV with a user column and an organisation column, in either order. A row with a blank organisation clears that user's value.",

  // Create / edit dialog
  'userOrgs.dialog.editTitle': 'Edit {name}',
  'userOrgs.dialog.newTitle': 'New organisation type',
  'userOrgs.dialog.nameLabel': 'Name',
  'userOrgs.dialog.nameHint':
    'The label this grouping is shown under on the user lookup page, for example Cost Centre. Organisation types are not yet available as a filter on the reports.',
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

  // CSV import panel - controls
  'userOrgs.csv.clear': 'Clear',
  'userOrgs.csv.modeLabel': 'What should happen to users who are not in the file?',
  'userOrgs.csv.modeMerge': 'Merge - leave them exactly as they are',
  'userOrgs.csv.modeReplace': 'Replace - clear their {name} value (the file is the complete list)',
  'userOrgs.csv.import.one': 'Import {count} row',
  'userOrgs.csv.import.other': 'Import {count} rows',

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
  'userOrgs.csv.previewAriaLabel': 'File preview',
  'userOrgs.csv.column.line': 'Line',
  'userOrgs.csv.column.user': 'User',
  'userOrgs.csv.column.organisation': 'Organisation',
  'userOrgs.csv.column.matches': 'Matches a user',
  'userOrgs.csv.clearsValue': 'clears the value',
  'userOrgs.csv.showingFirst': 'Showing the first {count} rows. The whole file is imported.',
  'userOrgs.csv.truncated.one':
    '1 organisation name is longer than 200 characters and will be stored shortened. Names that are identical for their first 200 characters become one organisation.',
  'userOrgs.csv.truncated.other':
    '{count} organisation names are longer than 200 characters and will be stored shortened. Names that are identical for their first 200 characters become one organisation.',
  'userOrgs.csv.problems':
    'Some rows cannot be used: {problems}. They are skipped and counted; the rest of the file still imports.',
  'userOrgs.csv.problemLine': 'line {line} ({reason})',

  // Job progress
  'userOrgs.job.importing': 'Importing {count} row(s)... this page will update when it finishes.',
  'userOrgs.job.interrupted':
    'This import stopped reporting progress, which usually means the web app restarted while it was running. It was either applied in full or not at all - the file is applied in a single transaction, so it cannot have been left half done - but which of those happened is not recorded. Upload the file again to be sure; importing the same file twice is harmless.',
  'userOrgs.job.failed': 'The import {status}. {message}',
  'userOrgs.job.finished': 'Import finished.',
  'userOrgs.job.changed': '{count} changed',
  'userOrgs.job.cleared': '{count} cleared',
  'userOrgs.job.unknownUsers': '{count} unknown user(s)',
  'userOrgs.job.unusableRows': '{count} unusable row(s)',
} as const;

export default userOrgs;
