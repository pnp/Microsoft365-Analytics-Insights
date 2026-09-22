import { makeStyles, tokens, Text, Card, MessageBar, MessageBarBody } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import SqlPopover from '../SqlPopover';
import type { KpiTone } from '../shared/KpiGrid';
import { formatDateParts, formatNumber, useT, type TFunction, type TranslationKey } from '../../i18n';
import type {
  TeamsBucket,
  TeamsNamedCount,
  TeamsQueryInfo,
  TeamsWindow,
} from '../../types/teamsExplorer';
import type { ReportCategory } from '../../types/reports';

/**
 * The adoption band boundaries.
 *
 * These MUST match `TeamsExplorerScoring.NeedsAttentionUpperPct` / `ProgressingUpperPct` on the
 * server and `ADOPTION_BANDS` in `GaugeRing.tsx`. The server decides the tone of its own
 * judgements; the client decides the tone of the KPI cards. If the two drift, the page states a
 * figure is "healthy" next to a red card, which destroys trust in everything else on it.
 * `teamsShared.test.ts` asserts the values so a change on either side fails loudly.
 */
export const NEEDS_ATTENTION_UPPER_PCT = 40;
export const PROGRESSING_UPPER_PCT = 70;

/** The KPI tone for an adoption-style percentage. */
export function reachTone(percent: number): KpiTone {
  if (percent < NEEDS_ATTENTION_UPPER_PCT) return 'critical';
  return percent < PROGRESSING_UPPER_PCT ? 'warning' : 'good';
}

/** Whole number with thousands separators. */
export function formatCount(value: number): string {
  return formatNumber(Math.round(value));
}

/** One decimal place, dropping a trailing ".0". */
export function formatDecimal(value: number): string {
  const rounded = Math.round(value * 10) / 10;
  return formatNumber(rounded, {
    minimumFractionDigits: rounded % 1 === 0 ? 0 : 1,
    maximumFractionDigits: rounded % 1 === 0 ? 0 : 1,
  });
}

/** A percentage to one decimal place. */
export function formatPct(value: number): string {
  return `${formatDecimal(value)}%`;
}

/** Hours, to one decimal below 100 and whole above, so a board-pack figure is not eight digits. */
export function formatHours(value: number): string {
  return value >= 100 ? `${formatCount(value)} h` : `${formatDecimal(value)} h`;
}

/** A UTC ISO date as a short local-format date, or a dash when absent. */
export function formatDate(iso: string | null | undefined): string {
  if (!iso) return '\u2014';
  return formatDateParts(new Date(iso), {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  });
}

/** "1 Mar 2026 - 28 Mar 2026" for a window's own dates. */
export function formatRange(fromIso: string, toIso: string): string {
  return `${formatDate(fromIso)} \u2013 ${formatDate(toIso)}`;
}

/**
 * Channel sentiment in words and figures.
 *
 * Re-exported from the shared module so the scale is defined exactly once. See
 * `components/shared/SentimentLight.tsx` for why this is not a percentage - it is the single easiest
 * figure on this page to misread, so every caller goes through there.
 */
export { formatSentiment, sentimentLabel, sentimentScaleNote } from '../shared/SentimentLight';

/** Named counts -> bar/treemap/word-cloud categories. */
export function toCategories(rows: TeamsNamedCount[]): ReportCategory[] {
  return rows.map((row) => ({ label: row.name, value: row.count }));
}

/** Distribution buckets -> bar categories, preserving bucket order. */
export function bucketsToCategories(buckets: TeamsBucket[]): ReportCategory[] {
  return buckets.map((bucket) => ({ label: bucket.label, value: bucket.count }));
}

export const TEAMS_SEGMENT_TEXT_KEYS: Record<string, { labelKey: TranslationKey; descriptionKey: TranslationKey }> = {
  Power: {
    labelKey: 'teamsExplorer.segment.Power.label',
    descriptionKey: 'teamsExplorer.segment.Power.description',
  },
  Regular: {
    labelKey: 'teamsExplorer.segment.Regular.label',
    descriptionKey: 'teamsExplorer.segment.Regular.description',
  },
  Light: {
    labelKey: 'teamsExplorer.segment.Light.label',
    descriptionKey: 'teamsExplorer.segment.Light.description',
  },
  Dormant: {
    labelKey: 'teamsExplorer.segment.Dormant.label',
    descriptionKey: 'teamsExplorer.segment.Dormant.description',
  },
};

export function segmentLabel(t: TFunction, segment: string, fallback: string = segment): string {
  const keys = TEAMS_SEGMENT_TEXT_KEYS[segment];
  return keys ? t(keys.labelKey) : fallback;
}

export function segmentDescription(t: TFunction, segment: string, fallback: string): string {
  const keys = TEAMS_SEGMENT_TEXT_KEYS[segment];
  return keys ? t(keys.descriptionKey) : fallback;
}

export const TEAMS_MEETING_BUCKET_LABEL_KEYS = {
  size: {
    '1': 'teamsExplorer.bucket.meetingSize.1.label',
    '2': 'teamsExplorer.bucket.meetingSize.2.label',
    '3-5': 'teamsExplorer.bucket.meetingSize.3-5.label',
    '6-10': 'teamsExplorer.bucket.meetingSize.6-10.label',
    '11-25': 'teamsExplorer.bucket.meetingSize.11-25.label',
    '26-50': 'teamsExplorer.bucket.meetingSize.26-50.label',
    '50+': 'teamsExplorer.bucket.meetingSize.50+.label',
  },
  duration: {
    '<5': 'teamsExplorer.bucket.meetingDuration.<5.label',
    '5-15': 'teamsExplorer.bucket.meetingDuration.5-15.label',
    '15-30': 'teamsExplorer.bucket.meetingDuration.15-30.label',
    '30-60': 'teamsExplorer.bucket.meetingDuration.30-60.label',
    '60-120': 'teamsExplorer.bucket.meetingDuration.60-120.label',
    '120+': 'teamsExplorer.bucket.meetingDuration.120+.label',
  },
  period: {
    early: 'teamsExplorer.bucket.meetingPeriod.early.label',
    'late-morning': 'teamsExplorer.bucket.meetingPeriod.lateMorning.label',
    afternoon: 'teamsExplorer.bucket.meetingPeriod.afternoon.label',
    evening: 'teamsExplorer.bucket.meetingPeriod.evening.label',
    night: 'teamsExplorer.bucket.meetingPeriod.night.label',
  },
} as const satisfies Record<string, Record<string, TranslationKey>>;

type TeamsMeetingBucketGroup = keyof typeof TEAMS_MEETING_BUCKET_LABEL_KEYS;

export function translatedBucketsToCategories(
  t: TFunction,
  group: TeamsMeetingBucketGroup,
  buckets: TeamsBucket[],
): ReportCategory[] {
  const keys: Record<string, TranslationKey> = TEAMS_MEETING_BUCKET_LABEL_KEYS[group];
  return buckets.map((bucket) => {
    const key = keys[bucket.key];
    return { label: key ? t(key) : bucket.label, value: bucket.count };
  });
}

/** Looks up one section's query diagnostics by key. */
export function queryFor(queries: TeamsQueryInfo[], key: string): TeamsQueryInfo | undefined {
  return queries.find((q) => q.key === key);
}

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '14px 16px',
  },
  head: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
  },
  titleBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  body: {
    marginTop: '10px',
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))',
    gap: '16px',
    marginTop: '16px',
    alignItems: 'start',
  },
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    marginTop: '16px',
  },
  tableWrap: {
    overflowX: 'auto',
  },
  td: {
    fontSize: tokens.fontSizeBase200,
  },
  numeric: {
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
  },
});

/** Shared layout classes, so every panel lays out identically without repeating the styles. */
export function useTeamsStyles() {
  return useStyles();
}

type SectionCardProps = {
  title: string;
  description?: string;
  /** The query behind this section - drives the SQL popover and the error state. */
  query?: TeamsQueryInfo;
  /** Shown instead of the children when there is genuinely nothing to render. */
  emptyMessage?: string;
  /** True when the section has no rows. */
  isEmpty?: boolean;
  /** Extra explanation shown above the content, e.g. a data-availability caveat. */
  note?: string;
  children: ReactNode;
};

/**
 * One panel of a Teams Explorer tab: heading, the SQL that produced it, and either the content, an
 * error or an explicit "nothing here and why".
 *
 * A section that failed renders its error rather than disappearing. Silently omitting it would let
 * a reader conclude "there were no meetings" from what was actually a query timeout - and on a page
 * whose whole purpose is to support a decision, those two must never look the same.
 */
export function SectionCard({
  title,
  description,
  query,
  emptyMessage,
  isEmpty,
  note,
  children,
}: SectionCardProps) {
  const styles = useStyles();
  const t = useT();

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <div className={styles.titleBlock}>
          <Text weight="semibold">{title}</Text>
          {description && (
            <Text size={200} className={styles.muted}>
              {description}
            </Text>
          )}
        </div>
        {query?.sql && <SqlPopover sql={query.sql} title={t('teamsExplorer.shared.sqlBehind', { title })} />}
      </div>

      {note && (
        <Text size={200} className={styles.muted}>
          {note}
        </Text>
      )}

      <div className={styles.body}>
        {query?.error ? (
          <MessageBar intent="error">
            <MessageBarBody>{t('teamsExplorer.shared.sectionLoadError', { error: query.error })}</MessageBarBody>
          </MessageBar>
        ) : isEmpty ? (
          <Text size={200} className={styles.muted}>
            {emptyMessage ?? t('teamsExplorer.shared.noData')}
          </Text>
        ) : (
          children
        )}
      </div>
    </Card>
  );
}

/** The window caption every tab shows, spelling out the usage-report lag rather than hiding it. */
export function WindowNote({
  window,
  includeUsage = true,
}: {
  window: TeamsWindow;
  includeUsage?: boolean;
}) {
  const styles = useStyles();
  const t = useT();

  const usageDiffers =
    includeUsage && (window.usageFromUtc !== window.fromUtc || window.usageToUtc !== window.toUtc);

  return (
    <Text size={200} className={styles.muted}>
      {t('teamsExplorer.shared.callDataCovers', { range: formatRange(window.fromUtc, window.toUtc) })}
      {usageDiffers
        ? ` ${usageReportWindowNote(t, formatRange(window.usageFromUtc, window.usageToUtc))}`
        : ''}
    </Text>
  );
}

function usageReportWindowNote(t: TFunction, range: string): string {
  return t('teamsExplorer.shared.usageReportFiguresCover', { range });
}
