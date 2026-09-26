import { EN_CATALOG, type TFunction, type TranslationKey } from '../../i18n';
import { WORKLOADS, type LicenceActivityCoverage } from '../../types/licenceActivity';
import { coverageMessage } from './sources';

// Licence Activity's notes are English sentences the server writes: five from the availability check in
// LicenceActivityAPIController, and seven from LicenceActivityRules.Notes for the overview and the users
// drill-down. Each note's English catalog entry is the server's sentence verbatim, so a note is recognised
// by exact match and shown from the catalog in the reader's language. Anything unrecognised is shown as the
// server sent it, so a new note degrades to English rather than disappearing.
//
// serverAuthoredText.test.ts reads the C# and fails when a note, or the set of notes, drifts from this list.
export const LICENCE_ACTIVITY_NOTE_KEYS: readonly TranslationKey[] = [
  'licenceActivity.note.userMetadataRequired',
  'licenceActivity.note.privacy',
  'licenceActivity.note.assignmentCaveat',
  'licenceActivity.note.interpretationCaveat',
  'licenceActivity.note.activityMethod',
  'licenceActivity.note.noLicences',
  'licenceActivity.note.nobodyHoldsALicence',
  'licenceActivity.note.noDisplayNames',
  'licenceActivity.note.demographicsCapped',
  'licenceActivity.note.usageReportsGroupFiltered',
  'licenceActivity.note.rankingMethod',
  'licenceActivity.note.nobodyRankable',
];

function workloadDisplayName(workload: string): string {
  return WORKLOADS.find((w) => w.key === workload)?.label ?? workload;
}

/**
 * A server-authored Licence Activity note in the reader's language.
 *
 * `coverage` lets the two coverage-derived notes be recognised as well: the overview's
 * `LicenceActivityRules.Notes.ForService` ("Teams: <coverage message>") and the users drill-down's bare
 * coverage message for the selected service. Both are rebuilt from the coverage entry's `messageKey`, so
 * the sentence comes from the catalog; the service name is a product name and is never translated.
 */
export function serverMessageText(
  t: TFunction,
  message: string,
  coverage: readonly LicenceActivityCoverage[] = [],
): string {
  const note = LICENCE_ACTIVITY_NOTE_KEYS.find((key) => EN_CATALOG[key] === message);
  if (note) return t(note);

  for (const entry of coverage) {
    if (!entry.message) continue;
    const translated = coverageMessage(entry.messageKey, entry.message, t) ?? entry.message;
    const service = workloadDisplayName(entry.workload);
    const englishForService = EN_CATALOG['licenceActivity.note.forService']
      .replace('{service}', () => service)
      .replace('{message}', () => entry.message as string);
    if (message === englishForService) return t('licenceActivity.note.forService', { service, message: translated });
    if (message === entry.message) return translated;
  }

  return message;
}
