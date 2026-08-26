import { useCallback, useEffect, useState, type ReactNode } from 'react';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Subtitle2,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import Spinner from '../Spinner';
import type { HealthSectionBase, HealthStatusName, HourCount } from '../../types/health';

export type BadgeColor = 'success' | 'warning' | 'danger' | 'informative' | 'subtle';

// A full activity import cycle should complete at least this often (see HEALTH-MONITORING-DESIGN.md).
export const CYCLE_SLA_HOURS = 24;
export const AUTO_REFRESH_MS = 60_000;

// --- Time / format helpers ---

export function minutesAgo(iso: string | null): number | null {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return null;
  return (Date.now() - t) / 60000;
}

export function howLongAgo(iso: string | null): string {
  const m = minutesAgo(iso);
  if (m === null) return 'never';
  if (m < 1) return 'just now';
  if (m < 60) return `${Math.round(m)} min ago`;
  if (m < 60 * 24) return `${(m / 60).toFixed(1)} hours ago`;
  return `${(m / 60 / 24).toFixed(1)} days ago`;
}

export function freshnessColor(iso: string | null, greenHours: number, amberHours: number): BadgeColor {
  const m = minutesAgo(iso);
  if (m === null) return 'subtle';
  const h = m / 60;
  if (h <= greenHours) return 'success';
  if (h <= amberHours) return 'warning';
  return 'danger';
}

export function statusColor(status: string | null): BadgeColor {
  switch ((status ?? '').toLowerCase()) {
    case 'healthy':
      return 'success';
    case 'degraded':
      return 'warning';
    case 'unhealthy':
      return 'danger';
    default:
      return 'subtle';
  }
}

export function overallColor(status: string | null): BadgeColor {
  switch ((status ?? '').toLowerCase()) {
    case 'healthy':
      return 'success';
    case 'degraded':
      return 'warning';
    case 'unhealthy':
      return 'danger';
    default:
      return 'informative';
  }
}

export function formatUtc(iso: string | null): string {
  if (!iso) return '-';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '-';
  return `${d.toISOString().slice(0, 19).replace('T', ' ')} UTC`;
}

export function formatSize(mb: number): string {
  if (!mb || mb <= 0) return '-';
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${mb.toLocaleString()} MB`;
}

/** null (couldn't compute, e.g. the bounded scan timed out) renders as "-"; otherwise a localised count. */
export function formatCount(n: number | null): string {
  return n === null || n === undefined ? '-' : n.toLocaleString();
}

// KQL summarize-by-bin omits empty hours, so pad to a full 24-bar series for a readable sparkline.
export function buildHourBuckets(perHour: HourCount[]): { hourUtc: string; count: number }[] {
  const byHour = new Map<number, number>();
  for (const h of perHour) {
    if (!h.hourUtc) continue;
    const t = new Date(h.hourUtc);
    if (Number.isNaN(t.getTime())) continue;
    t.setUTCMinutes(0, 0, 0);
    byHour.set(t.getTime(), (byHour.get(t.getTime()) ?? 0) + h.count);
  }
  const now = new Date();
  now.setUTCMinutes(0, 0, 0);
  const buckets: { hourUtc: string; count: number }[] = [];
  for (let i = 23; i >= 0; i--) {
    const d = new Date(now.getTime() - i * 3_600_000);
    buckets.push({ hourUtc: d.toISOString(), count: byHour.get(d.getTime()) ?? 0 });
  }
  return buckets;
}

// --- Per-section data hook ---

export interface SectionState<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
  refreshing: boolean;
  reload: () => void;
}

/**
 * Fetches one Health sub-section. Loads once when the panel first mounts (lazy: a panel only mounts
 * when its tab is first opened) and auto-refreshes every 60s while `active` (the tab is on top) so an
 * open "green board" stays current without every tab polling in the background.
 */
export function useHealthSection<T>(fetcher: () => Promise<T>, active: boolean): SectionState<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  const load = useCallback(async () => {
    setRefreshing(true);
    try {
      const d = await fetcher();
      setData(d);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [fetcher]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!active) return;
    const id = window.setInterval(() => void load(), AUTO_REFRESH_MS);
    return () => window.clearInterval(id);
  }, [active, load]);

  return { data, error, loading, refreshing, reload: () => void load() };
}

// --- Shared presentational bits ---

const useSharedStyles = makeStyles({
  toolbar: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
    marginBottom: '4px',
  },
  spacer: { flex: 1 },
  desc: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginBottom: '8px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  subHeading: {
    display: 'block',
    marginTop: '12px',
    marginBottom: '4px',
    fontWeight: tokens.fontWeightSemibold,
  },
  reasons: {
    marginTop: '4px',
    marginBottom: '8px',
    paddingLeft: '20px',
  },
  loading: { textAlign: 'center', padding: '32px' },
  /**
   * Fluent's Table is `table-layout: fixed; width: 100%`, so a long unbroken token (a fully
   * qualified type name, an endpoint, a URL) overflows its cell and paints over the next column
   * instead of widening it. `anywhere` rather than `break-word` because it also reduces the
   * element's intrinsic min-content width, so it keeps working in an auto-layout or flex context.
   */
  breakAnywhere: {
    overflowWrap: 'anywhere',
  },
  /** Numeric column: right-aligned and never wrapped. */
  numeric: {
    textAlign: 'right',
    whiteSpace: 'nowrap',
  },
});

export function useHealthStyles() {
  return useSharedStyles();
}

export function HealthStatusBadge({ status }: { status: HealthStatusName | null }) {
  return (
    <Badge appearance="filled" color={statusColor(status)}>
      {status ?? 'Unknown'}
    </Badge>
  );
}

export function SectionReasons({ reasons }: { reasons: string[] }) {
  const styles = useSharedStyles();
  if (!reasons || reasons.length === 0) return null;
  return (
    <ul className={styles.reasons}>
      {reasons.map((r, i) => (
        <li key={i}>
          <Text size={200}>{r}</Text>
        </li>
      ))}
    </ul>
  );
}

/**
 * Frame shared by the five detail sub-sections: a toolbar (own status badge + refresh + loaded-at),
 * an optional description, this section's own reasons, and loading/error handling. The body is only
 * rendered once data is present.
 */
export function SectionFrame<T extends HealthSectionBase>({
  title,
  description,
  state,
  children,
}: {
  title: string;
  description?: ReactNode;
  state: SectionState<T>;
  children: (data: T) => ReactNode;
}) {
  const styles = useSharedStyles();
  const { data, loading, error, refreshing, reload } = state;

  return (
    <div>
      <div className={styles.toolbar}>
        <Subtitle2>{title}</Subtitle2>
        {data && <HealthStatusBadge status={data.status} />}
        <span className={styles.spacer} />
        {data && (
          <Text size={200} className={styles.muted}>
            loaded {formatUtc(data.loadedAtUtc)}
          </Text>
        )}
        <Button size="small" appearance="secondary" disabled={refreshing} onClick={reload}>
          {refreshing ? 'Refreshing...' : 'Refresh'}
        </Button>
      </div>

      {description && <Text className={styles.desc}>{description}</Text>}
      {data && <SectionReasons reasons={data.reasons} />}

      {loading && !data ? (
        <div className={styles.loading}>
          <Spinner size={60} label={`Loading ${title.toLowerCase()}...`} />
        </div>
      ) : error && !data ? (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      ) : data ? (
        children(data)
      ) : null}
    </div>
  );
}
