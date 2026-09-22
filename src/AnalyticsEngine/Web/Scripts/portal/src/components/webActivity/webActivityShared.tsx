import { makeStyles, tokens, Text, Card, MessageBar, MessageBarBody } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { formatDateParts, formatNumber, plural, useT, type TFunction, type TranslationKey } from '../../i18n';
import SqlPopover from '../SqlPopover';
import type { KpiTone } from '../shared/KpiGrid';
import type { ReportCategory, ReportSeries } from '../../types/reports';
import type {
  WebActivityBucket,
  WebActivityJudgement,
  WebActivityNamedCount,
  WebActivityQueryInfo,
  WebActivityStackPoint,
  WebActivityWindow,
} from '../../types/webActivity';

/**
 * The band boundaries the KPI cards colour by.
 *
 * These MUST match `WebActivityScoring` on the server. The server decides the tone of its own
 * judgements; the client decides the tone of the KPI cards. If the two drift, the page states a
 * figure is healthy next to a red card, which destroys trust in everything else on it.
 * `webActivityShared.test.ts` asserts the values so a change on either side fails loudly.
 */
export const HIGH_BOUNCE_PCT = 60;
export const HEALTHY_BOUNCE_PCT = 40;
export const LOW_REACH_PCT = 25;
export const GOOD_REACH_PCT = 60;
export const SLOW_LOAD_SECONDS = 3.0;
export const FAST_LOAD_SECONDS = 1.5;
export const HIGH_SEARCH_RELIANCE_PCT = 35;

/** The KPI tone for a reach-style percentage (higher is better). */
export function reachTone(percent: number): KpiTone {
  if (percent < LOW_REACH_PCT) return 'critical';
  return percent < GOOD_REACH_PCT ? 'warning' : 'good';
}

/** The KPI tone for a bounce rate (lower is better). */
export function bounceTone(percent: number): KpiTone {
  if (percent >= HIGH_BOUNCE_PCT) return 'critical';
  return percent > HEALTHY_BOUNCE_PCT ? 'warning' : 'good';
}

/** The KPI tone for an average page load in seconds (lower is better). */
export function loadTone(seconds: number | null | undefined): KpiTone {
  if (seconds === null || seconds === undefined || seconds <= 0) return 'neutral';
  if (seconds >= SLOW_LOAD_SECONDS) return 'critical';
  return seconds > FAST_LOAD_SECONDS ? 'warning' : 'good';
}

/** The KPI tone for a reach-style percentage that may not be measurable at all. */
export function reachToneOrNeutral(percent: number | null | undefined): KpiTone {
  return percent === null || percent === undefined ? 'neutral' : reachTone(percent);
}

/** The dwell-time caveat, shown wherever an average time on page is. */
export const DWELL_CAVEAT_KEY = 'webActivity.shared.dwellCaveat' satisfies TranslationKey;

export function dwellCaveat(t: TFunction): string {
  return t(DWELL_CAVEAT_KEY);
}

export function dwellCaveatLower(t: TFunction): string {
  return t('webActivity.shared.dwellCaveatLower');
}

/**
 * The KPI tone for how much of the intranet relies on search.
 *
 * High search reliance is a navigation finding rather than a failure, so the worst tone it can
 * reach is a warning. Colouring it red would put it beside genuinely broken figures and invite an
 * intranet team to "fix" something that may be entirely healthy.
 */
export function searchRelianceTone(percent: number): KpiTone {
  return percent >= HIGH_SEARCH_RELIANCE_PCT ? 'warning' : 'neutral';
}

/** Maps a server judgement tone to a Fluent MessageBar intent. */
export function judgementIntent(tone: WebActivityJudgement['tone']): 'success' | 'info' | 'warning' | 'error' {
  switch (tone) {
    case 'good':
      return 'success';
    case 'warning':
      return 'warning';
    case 'critical':
      return 'error';
    default:
      return 'info';
  }
}

/** Whole number with thousands separators. */
export function formatCount(value: number): string {
  return formatNumber(Math.round(value));
}

/** One decimal place, dropping a trailing ".0". */
export function formatDecimal(value: number): string {
  const rounded = Math.round(value * 10) / 10;
  return formatNumber(rounded, { minimumFractionDigits: 0, maximumFractionDigits: 1 });
}

/** A percentage to one decimal place. */
export function formatPct(value: number | null | undefined): string {
  if (value === null || value === undefined) return '\u2014';
  return `${formatDecimal(value)}%`;
}

/** A duration in seconds as a human-readable string ("42s", "3m 05s"). */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined) return '\u2014';
  if (seconds < 60) return `${formatDecimal(seconds)}s`;

  const whole = Math.round(seconds);
  const minutes = Math.floor(whole / 60);
  const remainder = whole % 60;
  return `${minutes}m ${remainder.toString().padStart(2, '0')}s`;
}

/** A page load time, which is small enough that two decimals are the readable choice. */
export function formatSeconds(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined) return '\u2014';
  return `${formatNumber(Math.round(seconds * 100) / 100, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}s`;
}

/** An hour-of-day index as "14:00 UTC", or a dash when there is none. */
export function formatHour(hour: number | null | undefined): string {
  if (hour === null || hour === undefined) return '\u2014';
  return `${hour.toString().padStart(2, '0')}:00`;
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
 * Trims a URL to its path, so a table of pages is readable at a glance.
 *
 * The path is percent-DECODED before it is shown. `URL.pathname` returns the encoded form, so a
 * SharePoint page in a non-Latin folder comes back as `%CE%9A%CE%B1%CE%BB...` - which in a table of
 * page URLs is indistinguishable from a database encoding bug, and is exactly what an admin would
 * raise as one.
 */
export function shortenUrl(url: string | null | undefined): string {
  if (!url) return '\u2014';
  try {
    const parsed = new URL(url);
    return decodePath(parsed.pathname) + parsed.search;
  } catch {
    // Not an absolute URL (or not one this browser will parse). Showing it verbatim is better than
    // showing nothing - this is a display aid, not validation.
    return url;
  }
}

/** Percent-decodes a path, falling back to the raw value on a malformed escape sequence. */
function decodePath(path: string): string {
  try {
    return decodeURIComponent(path);
  } catch {
    return path;
  }
}

/**
 * Appends an explicit remainder when the listed rows do not account for the whole population.
 *
 * Every ranked list on this page is truncated to the top N. A donut or a share chart built from
 * only those rows normalises to them, so it always totals 100% however much traffic the tail held -
 * a tenant with 40 countries would see 15 of them redistributed to cover everything. The remainder
 * makes the omission visible instead.
 */
export function withRemainder(
  categories: ReportCategory[],
  total: number,
  label: string,
): ReportCategory[] {
  const listed = categories.reduce((sum, c) => sum + c.value, 0);
  const remainder = total - listed;

  // A negative remainder means the caller passed a total that is not this list's population. Adding
  // it would draw a nonsensical slice, so the list is returned untouched.
  if (remainder <= 0) return categories;

  return [...categories, { label, value: remainder }];
}

/** Named counts -> bar/treemap/word-cloud categories. */
export function toCategories(rows: WebActivityNamedCount[]): ReportCategory[] {
  return rows.map((row) => ({ label: row.name, value: row.count }));
}

/** Distribution buckets -> bar categories, preserving bucket order. */
export function bucketsToCategories(buckets: WebActivityBucket[]): ReportCategory[] {
  return buckets.map((bucket) => ({ label: bucket.label, value: bucket.count }));
}

export function translatedBucketLabel(t: TFunction, group: string, bucket: WebActivityBucket): string {
  const catalogKey = (`webActivity.bucket.` + `${group}.${bucket.key}.label`) as TranslationKey;
  const translated = t(catalogKey);
  return translated === catalogKey ? bucket.label : translated;
}

export function translatedBucketsToCategories(t: TFunction, group: string, buckets: WebActivityBucket[]): ReportCategory[] {
  return buckets.map((bucket) => ({ label: translatedBucketLabel(t, group, bucket), value: bucket.count }));
}

/**
 * Stacked weekly points -> one series per name, over the union of every week present.
 *
 * Every series is padded to the full set of weeks with zeros rather than left sparse. A stacked
 * chart whose bands have different x positions does not stack - it interleaves - and the resulting
 * picture is wrong in a way that looks plausible.
 */
export function toStackedSeries(points: WebActivityStackPoint[]): ReportSeries[] {
  const weeks = Array.from(new Set(points.map((p) => p.weekStart))).sort();
  const names = Array.from(new Set(points.map((p) => p.name)));

  const byKey = new Map<string, number>();
  for (const point of points) {
    byKey.set(`${point.name}\u0000${point.weekStart}`, point.count);
  }

  return names.map((name) => ({
    name,
    points: weeks.map((weekStart) => ({
      weekStart,
      value: byKey.get(`${name}\u0000${weekStart}`) ?? 0,
    })),
  }));
}

/** Looks up one section's query diagnostics by key. */
export function queryFor(
  queries: WebActivityQueryInfo[],
  key: string,
): WebActivityQueryInfo | undefined {
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
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    flexShrink: 0,
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
  wideGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(460px, 1fr))',
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
  ellipsis: {
    maxWidth: '320px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  judgements: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    marginTop: '16px',
  },
});

/** Shared layout classes, so every panel lays out identically without repeating the styles. */
export function useWebActivityStyles() {
  return useStyles();
}

type SectionCardProps = {
  title: string;
  description?: string;
  /** The query behind this section - drives the SQL popover and the error state. */
  query?: WebActivityQueryInfo;
  /** Shown instead of the children when there is genuinely nothing to render. */
  emptyMessage?: string;
  /** True when the section has no rows. */
  isEmpty?: boolean;
  /** Extra explanation shown above the content, e.g. a data-availability caveat. */
  note?: string;
  /** Buttons rendered beside the SQL popover, e.g. an export. */
  actions?: ReactNode;
  children: ReactNode;
};

/**
 * One panel of a web-activity tab: heading, the SQL that produced it, and either the content, an
 * error or an explicit "nothing here and why".
 *
 * A section that failed renders its error rather than disappearing. Silently omitting it would let
 * a reader conclude "nobody searched for anything" from what was actually a query timeout - and on
 * a page whose whole purpose is to support a content decision, those two must never look the same.
 */
export function SectionCard({
  title,
  description,
  query,
  emptyMessage,
  isEmpty,
  note,
  actions,
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
        <div className={styles.actions}>
          {actions}
          {query?.sql && <SqlPopover sql={query.sql} title={t('webActivity.shared.sqlBehindTitle', { title })} />}
        </div>
      </div>

      {note && (
        <Text size={200} className={styles.muted}>
          {note}
        </Text>
      )}

      <div className={styles.body}>
        {query?.error ? (
          <MessageBar intent="error">
            <MessageBarBody>{t('webActivity.shared.sectionLoadFailed', { error: query.error })}</MessageBarBody>
          </MessageBar>
        ) : isEmpty ? (
          <Text size={200} className={styles.muted}>
            {emptyMessage ?? t('webActivity.shared.noDataForPeriod')}
          </Text>
        ) : (
          children
        )}
      </div>
    </Card>
  );
}

/**
 * The window caption every tab shows.
 *
 * It states that times are UTC rather than leaving it implied. Every hour-of-day and day-of-week
 * figure on this page is UTC because `hits.hit_timestamp` is stored in UTC and a multi-region
 * intranet has no single local clock - a reader in Sydney comparing "popular hours" against their
 * own working day needs to be told that before they draw a conclusion from it.
 */
export function WindowNote({ window: reportWindow }: { window: WebActivityWindow }) {
  const styles = useStyles();
  const t = useT();

  return (
    <Text size={200} className={styles.muted}>
      {t('webActivity.shared.windowNote', {
        range: formatRange(reportWindow.fromUtc, reportWindow.toUtc),
        days: formatCount(reportWindow.days),
        workingDays: formatCount(reportWindow.workingDays),
      })}
    </Text>
  );
}

/** The judgement message bars shown under the Overview KPIs. */
export function JudgementList({ judgements }: { judgements: WebActivityJudgement[] }) {
  const styles = useStyles();

  if (judgements.length === 0) return null;

  return (
    <div className={styles.judgements}>
      {judgements.map((judgement) => (
        <MessageBar key={judgement.key} intent={judgementIntent(judgement.tone)}>
          <MessageBarBody>
            <Text weight="semibold">{judgement.headline}</Text>
            <br />
            <Text size={200}>{judgement.detail}</Text>
          </MessageBarBody>
        </MessageBar>
      ))}
    </div>
  );
}

/**
 * A warning shown when any of a tab's queries failed.
 *
 * Headline figures have nowhere to put a per-query error: a failed aggregate returns no rows, the
 * figure derived from it is zero, and "0.00s average load" reads as a perfect result rather than as
 * missing data. The charts below say which section failed; this says the numbers above them may be
 * incomplete, which is the part a reader would otherwise never learn.
 */
export function FailedQueryNote({ queries }: { queries: WebActivityQueryInfo[] }) {
  const t = useT();
  const failed = queries.filter((q) => q.error);
  if (failed.length === 0) return null;

  return (
    <MessageBar intent="warning" style={{ marginTop: '12px' }}>
      <MessageBarBody>
        {t(plural(failed.length, 'webActivity.shared.failedQuery.one', 'webActivity.shared.failedQuery.other'), { count: formatCount(failed.length) })}
      </MessageBarBody>
    </MessageBar>
  );
}
