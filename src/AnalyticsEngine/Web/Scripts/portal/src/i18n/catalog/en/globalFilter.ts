/**
 * The administrator's global report filter: conditions every report in Insights applies for everyone who
 * opens it, on top of their own filters, which only a portal administrator can change.
 *
 * Attribute values - department names, sign-in names, managers' addresses - are tenant data and are never
 * in this catalogue. Only the product's own wording is.
 */
const globalFilter = {
  'globalFilter.bar.label': 'Set by your administrator',
  'globalFilter.bar.groupAria': 'Filter set by a portal administrator',
  'globalFilter.bar.info':
    'A portal administrator set this filter. Every report in Insights shows only the people it matches, on top of any filter you add, and you can’t change or remove it. Activity that can’t be linked to a person in the directory isn’t counted while it applies.',
  'globalFilter.bar.infoAria': 'About this filter',
  'globalFilter.bar.coverage': '{matched} of {total} people',
  'globalFilter.bar.readback': 'Showing people where {description}.',
  'globalFilter.bar.edit': 'Edit',
  'globalFilter.bar.switchOff': 'Switch off for my view',
  'globalFilter.bar.switchOffHint':
    'See every report without this filter. Only your own view changes: everyone else still sees the filtered reports.',
  'globalFilter.bar.switchOn': 'Switch back on',
  'globalFilter.bar.switching': 'Switching…',
  'globalFilter.bar.bypassed':
    'The filter a portal administrator set is switched off for your view, so these reports cover everyone. Everyone else still sees only the people it matches.',
  'globalFilter.bar.invalid':
    'The filter a portal administrator set can’t be read by this version of the portal, so reports aren’t available until an administrator fixes it.',
  'globalFilter.bar.invalidAdmin':
    'The global report filter can’t be read by this version of the portal, so reports are refused for everyone until it is replaced.',
  'globalFilter.bar.loadFailed':
    'Couldn’t check whether a portal administrator has set a filter. If one is set, the reports still apply it.',
  'globalFilter.bar.retry': 'Try again',
  'globalFilter.bar.viewerNotFound':
    'Some of these conditions depend on your own details, and you aren’t in the user directory the reports use, so they match nobody. The reports will show no one until your account has been imported.',
  'globalFilter.bar.matchesNobody': 'This filter matches nobody for you, so the reports will be empty.',
  'globalFilter.bar.tooFewPeople.one':
    'This filter leaves you {matched} person. Reports about fewer than {minimum} people show individuals’ activity, which needs the See PII permission, so they aren’t shown to you. Ask a portal administrator if you need them.',
  'globalFilter.bar.tooFewPeople.other':
    'This filter leaves you {matched} people. Reports about fewer than {minimum} people show individuals’ activity, which needs the See PII permission, so they aren’t shown to you. Ask a portal administrator if you need them.',
  'globalFilter.bar.unknownDimension':
    'A condition refers to an organisation type that no longer exists, so that condition matches nobody.',

  'globalFilter.note.overview':
    'It applies to the reports in Insights. The counts on this page describe all the data the service holds.',
  'globalFilter.note.teams':
    'Team-level figures - collaboration and conversations - cover every team, because a team isn’t a person the filter can match.',
  'globalFilter.note.agentCosts':
    'It narrows the list of people. Agent and Azure cost figures cover the whole organisation, because they belong to agents rather than to people.',

  'globalFilter.pill.lockedAria': '{condition}. Set by a portal administrator.',
  'globalFilter.pill.unresolved': 'not recorded for you',

  'globalFilter.viewer.option.own': 'Viewer’s own value',
  'globalFilter.viewer.option.self': 'The viewer',
  'globalFilter.viewer.option.manager': 'The viewer’s manager',
  'globalFilter.viewer.phrase.own': 'the viewer’s own value',
  'globalFilter.viewer.phrase.self': 'the viewer',
  'globalFilter.viewer.phrase.manager': 'the viewer’s manager',
  'globalFilter.viewer.hint.own':
    'Choose “Viewer’s own value” to show each reader the people who share theirs: each manager their own department, for example.',
  'globalFilter.viewer.hint.userName':
    'Choose “The viewer” to show each reader only their own figures. Readers without the See PII permission are shown no reports under such a filter.',
  'globalFilter.viewer.hint.manager':
    '“The viewer” shows each reader their direct reports. “The viewer’s manager” shows them everyone who shares their manager.',
  'globalFilter.viewer.hint.managementChain':
    '“The viewer” shows each reader everyone who reports to them, at any level. “The viewer’s manager” shows their manager’s whole organisation.',

  'globalFilter.reader.own': '{value} (from your profile)',
  'globalFilter.reader.self': '{value} (you)',
  'globalFilter.reader.manager': '{value} (your manager)',
  'globalFilter.reader.unresolved.own':
    '{dimension} must match your own, which isn’t recorded for you, so this condition matches nobody',
  'globalFilter.reader.unresolved.self':
    '{dimension} must match you, but you aren’t in the directory the reports use, so this condition matches nobody',
  'globalFilter.reader.unresolved.manager':
    '{dimension} must match your manager, who isn’t recorded for you, so this condition matches nobody',
  // Sign-in names withheld from a reader without See PII: whose value it is, or how many people, never who.
  'globalFilter.reader.hidden.own': 'your own value',
  'globalFilter.reader.hidden.self': 'you',
  'globalFilter.reader.hidden.manager': 'your manager',
  'globalFilter.reader.hiddenPeople.one': '{count} named person',
  'globalFilter.reader.hiddenPeople.other': '{count} named people',
  'globalFilter.reader.hiddenTerms.one': '{count} search term',
  'globalFilter.reader.hiddenTerms.other': '{count} search terms',

  'globalFilter.print.heading': 'Filter set by a portal administrator',
  'globalFilter.print.description': 'Only people where {description}.',
  'globalFilter.print.coverage': '{matched} of the {total} people in the directory match it for the person viewing.',
  'globalFilter.print.bypassed':
    'A portal administrator’s report filter was switched off for this view, so these figures are not narrowed by it.',

  'globalFilter.banner.setByAdmin': '{description} (set by a portal administrator)',

  'globalFilter.editor.groupAria': 'Global filter conditions',
  'globalFilter.editor.none': 'No conditions: reports cover everyone, apart from any filter a reader adds.',
  'globalFilter.editor.add': 'Add condition',
  'globalFilter.editor.readback': 'Readers see only people where {description}.',
  'globalFilter.editor.problem.tooManyClauses': 'A filter can have at most {max} conditions.',
  'globalFilter.editor.problem.valueTooLong': 'One of the values is too long to filter on.',
  'globalFilter.editor.problem.tooLong': 'The filter is too long to save. Remove some values or conditions.',
  'globalFilter.editor.problem.viewerText': 'A condition that looks for text can’t also compare with the viewer’s own value.',

  'globalFilter.admin.title': 'Global report filter',
  'globalFilter.admin.intro':
    'Conditions every report in Insights applies for everyone who opens it, on top of any filter they add themselves. Readers see these conditions on every page but can’t change or remove them. A condition can compare with the viewer’s own value, so one filter can show every manager their own department.',
  'globalFilter.admin.loading': 'Loading the global filter…',
  'globalFilter.admin.loadFailed': 'Couldn’t load the global filter.',
  'globalFilter.admin.retry': 'Try again',
  'globalFilter.admin.reload': 'Reload',
  'globalFilter.admin.rolesNotEnforced':
    'Portal roles aren’t enforced on this deployment, so everyone who can sign in is a portal administrator: anyone can change this filter or switch it off for their own view. Turn on role enforcement (EnforcePortalRoles) before relying on it to limit what people see.',
  'globalFilter.admin.storageUnavailable':
    'The database hasn’t been upgraded to hold a global filter, so none can be saved. Run the installer, or the manual upgrade script {script}, and reload this page.',
  'globalFilter.admin.invalidStored':
    'The saved filter can’t be read by this version of the portal, so Insights reports are refused for everyone. Save a replacement, or save with no conditions to remove it.',
  'globalFilter.admin.bypassedForYou':
    'The filter is switched off for your own view. Switch it back on from the filter bar on any Insights page.',
  'globalFilter.admin.conditions.heading': 'Conditions',
  'globalFilter.admin.conditions.note':
    'People must match these conditions to appear in any report. Conditions joined by AND must all match; OR starts another group, and matching any one group is enough.',
  'globalFilter.admin.conditions.privacyNote':
    'Readers without the See PII permission never see the sign-in names a condition uses, and are shown no reports while the filter leaves them fewer than {minimum} people - even when the only person left is themselves.',
  'globalFilter.admin.preview.heading': 'What you would see',
  'globalFilter.admin.preview.note':
    'The filter applies to portal administrators too, so this is what your own reports would show once it is saved.',
  'globalFilter.admin.preview.none': 'No conditions, so reports would cover everyone.',
  'globalFilter.admin.preview.loading': 'Checking…',
  'globalFilter.admin.preview.failed': 'Couldn’t preview this filter.',
  'globalFilter.admin.preview.coverage': 'You would see {matched} of the {total} people in the directory.',
  'globalFilter.admin.preview.description': 'For you, that means: {description}.',
  'globalFilter.admin.preview.viewerNotFound':
    'You aren’t in the user directory the reports use, so conditions on your own details match nobody for you. They still work for readers who are.',
  'globalFilter.admin.save': 'Save',
  'globalFilter.admin.saving': 'Saving…',
  'globalFilter.admin.discard': 'Discard changes',
  'globalFilter.admin.unsaved': 'Unsaved changes',
  'globalFilter.admin.saveFailed': 'Couldn’t save the global filter.',
  'globalFilter.admin.neverSet': 'No global filter has been saved yet.',
  'globalFilter.admin.lastChanged': 'Last changed {when}.',
  'globalFilter.admin.lastChangedBy': 'Last changed {when} by {by}.',
  'globalFilter.admin.propagation':
    'A saved change applies at once on this server, and on every other instance of the web app within a minute.',
  'globalFilter.admin.toast.saved': 'Global filter saved. Every report applies it from now on.',
  'globalFilter.admin.toast.removed': 'Global filter removed. Reports cover everyone again.',
} as const;

export default globalFilter;
