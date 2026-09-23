import type { TranslationKey } from '../../i18n';
import type { TimeSavedActivity } from './coworkTimeSaved';

/**
 * The published evidence behind the time-saved defaults, and the studies the model is sense-checked
 * against.
 *
 * Every figure quoted in the catalog text these keys point at was checked against the primary source
 * before it was written down, and each item carries how the figure was obtained - because the first
 * thing a sceptical reader does with "Copilot saves 26 minutes a day" is ask whether anyone measured
 * it. Self-reported figures are labelled as such and never presented as measurements; Microsoft's own
 * lab found self-estimates overstate the measured saving threefold.
 *
 * <b>Nothing here is about Cowork specifically.</b> No study has yet measured Cowork's time savings;
 * the defaults are anchored to Microsoft 365 Copilot evidence, and the page says so.
 */

/** How a figure was obtained. The single most important thing to know about any time-saved claim. */
export type EvidenceMethod = 'measured' | 'selfReported' | 'vendorModel';

export interface EvidenceItem {
  id: string;
  url: string;
  /** Who published it, when, and at what scale. */
  sourceKey: TranslationKey;
  /** What it found, quoting the figure. */
  findingKey: TranslationKey;
  method: EvidenceMethod;
}

export const EVIDENCE_METHOD_LABEL: Record<EvidenceMethod, TranslationKey> = {
  measured: 'copilotAdoptionCowork.timeSaved.method.measured',
  selfReported: 'copilotAdoptionCowork.timeSaved.method.selfReported',
  vendorModel: 'copilotAdoptionCowork.timeSaved.method.vendorModel',
};

export const EVIDENCE_METHOD_TOOLTIP: Record<EvidenceMethod, TranslationKey> = {
  measured: 'copilotAdoptionCowork.timeSaved.method.measuredTooltip',
  selfReported: 'copilotAdoptionCowork.timeSaved.method.selfReportedTooltip',
  vendorModel: 'copilotAdoptionCowork.timeSaved.method.vendorModelTooltip',
};

const COPILOT_DASHBOARD_URL =
  'https://learn.microsoft.com/en-us/viva/insights/org-team-insights/copilot-dashboard#details-on-the-copilot-assisted-hours-metric';
const WORK_TREND_INDEX_2023_URL =
  'https://www.microsoft.com/en-us/worklab/work-trend-index/copilots-earliest-users-teach-us-about-generative-ai-at-work';
const MSR_PRODUCTIVITY_REPORT_URL =
  'https://www.microsoft.com/en-us/research/wp-content/uploads/2023/12/AI-and-Productivity-Report-First-Edition.pdf';
const NBER_FIELD_EXPERIMENT_URL = 'https://www.nber.org/papers/w33795';
const UK_GOVERNMENT_EXPERIMENT_URL =
  'https://www.gov.uk/government/publications/microsoft-365-copilot-experiment-cross-government-findings-report/microsoft-365-copilot-experiment-cross-government-findings-report-html';
const FORRESTER_TEI_2025_URL = 'https://tei.forrester.com/go/microsoft/M365Copilot/?lang=en-us';

/** Microsoft's own documentation of what Cowork does. Linked, never quoted as evidence of time saved. */
export const COWORK_OVERVIEW_URL = 'https://learn.microsoft.com/en-us/microsoft-365/copilot/cowork/';

/** What Copilot and Cowork do with one kind of work, why the default is what it is, and the evidence. */
export interface ActivityRationale {
  activity: TimeSavedActivity;
  /** The operations that actually save the time. */
  operationKeys: TranslationKey[];
  /** How the product default is derived from Microsoft's own published credits. */
  whyKey: TranslationKey;
  evidence: EvidenceItem[];
  /** How a customer can replace the default with a figure of their own. */
  testKey: TranslationKey;
}

const DASHBOARD_MEETINGS: EvidenceItem = {
  id: 'dashboard-meetings',
  url: COPILOT_DASHBOARD_URL,
  sourceKey: 'copilotAdoptionCowork.timeSaved.source.copilotDashboard',
  findingKey: 'copilotAdoptionCowork.timeSaved.finding.dashboardMeetings',
  method: 'vendorModel',
};

const DASHBOARD_ACTIONS: EvidenceItem = {
  id: 'dashboard-actions',
  url: COPILOT_DASHBOARD_URL,
  sourceKey: 'copilotAdoptionCowork.timeSaved.source.copilotDashboard',
  findingKey: 'copilotAdoptionCowork.timeSaved.finding.dashboardActions',
  method: 'vendorModel',
};

export const ACTIVITY_RATIONALE: ActivityRationale[] = [
  {
    activity: 'meetings',
    operationKeys: [
      'copilotAdoptionCowork.timeSaved.meetings.operation.prepare',
      'copilotAdoptionCowork.timeSaved.meetings.operation.recap',
      'copilotAdoptionCowork.timeSaved.meetings.operation.followUp',
    ],
    whyKey: 'copilotAdoptionCowork.timeSaved.meetings.why',
    evidence: [
      DASHBOARD_MEETINGS,
      {
        id: 'wti-missed-meeting',
        url: WORK_TREND_INDEX_2023_URL,
        sourceKey: 'copilotAdoptionCowork.timeSaved.source.workTrendIndex2023',
        findingKey: 'copilotAdoptionCowork.timeSaved.finding.missedMeeting',
        method: 'measured',
      },
      {
        id: 'nber-meetings',
        url: NBER_FIELD_EXPERIMENT_URL,
        sourceKey: 'copilotAdoptionCowork.timeSaved.source.nberFieldExperiment',
        findingKey: 'copilotAdoptionCowork.timeSaved.finding.nberMeetings',
        method: 'measured',
      },
    ],
    testKey: 'copilotAdoptionCowork.timeSaved.meetings.test',
  },
  {
    activity: 'email',
    operationKeys: [
      'copilotAdoptionCowork.timeSaved.email.operation.triage',
      'copilotAdoptionCowork.timeSaved.email.operation.draft',
      'copilotAdoptionCowork.timeSaved.email.operation.send',
    ],
    whyKey: 'copilotAdoptionCowork.timeSaved.email.why',
    evidence: [
      DASHBOARD_ACTIONS,
      {
        id: 'nber-email',
        url: NBER_FIELD_EXPERIMENT_URL,
        sourceKey: 'copilotAdoptionCowork.timeSaved.source.nberFieldExperiment',
        findingKey: 'copilotAdoptionCowork.timeSaved.finding.nberEmail',
        method: 'measured',
      },
      {
        id: 'wti-email',
        url: WORK_TREND_INDEX_2023_URL,
        sourceKey: 'copilotAdoptionCowork.timeSaved.source.workTrendIndex2023',
        findingKey: 'copilotAdoptionCowork.timeSaved.finding.wtiEmail',
        method: 'selfReported',
      },
    ],
    testKey: 'copilotAdoptionCowork.timeSaved.email.test',
  },
  {
    activity: 'documents',
    operationKeys: [
      'copilotAdoptionCowork.timeSaved.documents.operation.draft',
      'copilotAdoptionCowork.timeSaved.documents.operation.summarise',
      'copilotAdoptionCowork.timeSaved.documents.operation.create',
    ],
    whyKey: 'copilotAdoptionCowork.timeSaved.documents.why',
    evidence: [
      DASHBOARD_ACTIONS,
      {
        id: 'wti-first-draft',
        url: WORK_TREND_INDEX_2023_URL,
        sourceKey: 'copilotAdoptionCowork.timeSaved.source.workTrendIndex2023',
        findingKey: 'copilotAdoptionCowork.timeSaved.finding.firstDraft',
        method: 'measured',
      },
      {
        id: 'nber-documents',
        url: NBER_FIELD_EXPERIMENT_URL,
        sourceKey: 'copilotAdoptionCowork.timeSaved.source.nberFieldExperiment',
        findingKey: 'copilotAdoptionCowork.timeSaved.finding.nberDocuments',
        method: 'measured',
      },
    ],
    testKey: 'copilotAdoptionCowork.timeSaved.documents.test',
  },
];

/** Why the conservative end applies only part of the assumptions. */
export const CONSERVATIVE_EVIDENCE: EvidenceItem[] = [
  {
    id: 'forrester-recapture',
    url: FORRESTER_TEI_2025_URL,
    sourceKey: 'copilotAdoptionCowork.timeSaved.source.forresterTei2025',
    findingKey: 'copilotAdoptionCowork.timeSaved.finding.forresterRecapture',
    method: 'vendorModel',
  },
  {
    id: 'msr-lab-caveat',
    url: MSR_PRODUCTIVITY_REPORT_URL,
    sourceKey: 'copilotAdoptionCowork.timeSaved.source.msrProductivityReport',
    findingKey: 'copilotAdoptionCowork.timeSaved.finding.msrLabCaveat',
    method: 'measured',
  },
];

/** A published per-person figure the model is compared with, in minutes per working day. */
export interface Benchmark extends EvidenceItem {
  minutesPerDay: number;
  /** True when the figure covers only part of the work the model covers, so it is a floor. */
  partial?: boolean;
}

/**
 * Published per-person time savings, lowest first.
 *
 * Minutes per working day, restated from each source's own unit where needed: 9 hours a month is
 * 27 minutes over a 20-day working month, 1.4 hours a week is 17 minutes over five days.
 *
 * The Australian Government's 2024 trial is deliberately absent. Its "up to an hour" is a per-task
 * figure (summarising, drafting, searching - each), which its authors describe as an upper bound,
 * so plotting it as an hour a day would both misquote it and flatter the model.
 */
export const TIME_SAVED_BENCHMARKS: Benchmark[] = [
  {
    id: 'wti-daily',
    url: WORK_TREND_INDEX_2023_URL,
    sourceKey: 'copilotAdoptionCowork.timeSaved.source.workTrendIndex2023',
    findingKey: 'copilotAdoptionCowork.timeSaved.benchmark.wtiDaily',
    method: 'selfReported',
    minutesPerDay: 14,
  },
  {
    id: 'nber-email-daily',
    url: NBER_FIELD_EXPERIMENT_URL,
    sourceKey: 'copilotAdoptionCowork.timeSaved.source.nberFieldExperiment',
    findingKey: 'copilotAdoptionCowork.timeSaved.benchmark.nberEmailDaily',
    method: 'measured',
    minutesPerDay: 17,
    partial: true,
  },
  {
    id: 'uk-government',
    url: UK_GOVERNMENT_EXPERIMENT_URL,
    sourceKey: 'copilotAdoptionCowork.timeSaved.source.ukGovernment',
    findingKey: 'copilotAdoptionCowork.timeSaved.benchmark.ukGovernment',
    method: 'selfReported',
    minutesPerDay: 26,
  },
  {
    id: 'forrester-monthly',
    url: FORRESTER_TEI_2025_URL,
    sourceKey: 'copilotAdoptionCowork.timeSaved.source.forresterTei2025',
    findingKey: 'copilotAdoptionCowork.timeSaved.benchmark.forresterMonthly',
    method: 'selfReported',
    minutesPerDay: 27,
  },
];

/** The span of published whole-job figures, for the sense check. Partial figures are excluded. */
export function benchmarkRange(): { min: number; max: number } {
  const whole = TIME_SAVED_BENCHMARKS.filter((b) => !b.partial).map((b) => b.minutesPerDay);
  return { min: Math.min(...whole), max: Math.max(...whole) };
}

/** Where a modelled per-person figure sits against the published range. */
export type SenseCheckVerdict = 'below' | 'within' | 'above';

/**
 * Judges the CONSERVATIVE end of the model against the published figures.
 *
 * The published figures describe Copilot as people use it today - partly adopted, and without
 * Cowork - so they are the counterpart of the conservative end, not of the full one. A conservative
 * end inside that range is a model that agrees with the evidence; one above it is a model that has
 * to be defended line by line.
 */
export function senseCheck(minutesPerDayLow: number): SenseCheckVerdict {
  const { min, max } = benchmarkRange();
  if (minutesPerDayLow > max) return 'above';
  if (minutesPerDayLow < min) return 'below';
  return 'within';
}

/** The measured finding that self-estimates overstate - quoted wherever a self-reported figure is. */
export const SELF_REPORT_CAVEAT: EvidenceItem = {
  id: 'msr-self-report',
  url: MSR_PRODUCTIVITY_REPORT_URL,
  sourceKey: 'copilotAdoptionCowork.timeSaved.source.msrProductivityReport',
  findingKey: 'copilotAdoptionCowork.timeSaved.finding.msrSelfReport',
  method: 'measured',
};
