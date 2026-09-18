import type { ReactNode } from 'react';
import {
  Badge,
  Card,
  CardHeader,
  Link,
  MessageBar,
  MessageBarBody,
  Subtitle2,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { DataOverviewSection, HealthSummary } from '../../types/health';
import {
  CYCLE_SLA_HOURS,
  formatCount,
  freshnessColor,
  howLongAgo,
  overallColor,
  statusColor,
} from '../health/healthShared';

const useStyles = makeStyles({
  card: {
    gap: '10px',
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    flexWrap: 'wrap',
    width: '100%',
  },
  spacer: {
    flex: 1,
  },
  sections: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '8px',
  },
  chip: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 10px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  stats: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(190px, 1fr))',
    gap: '8px 24px',
  },
  stat: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '8px',
  },
  statLabel: {
    color: tokens.colorNeutralForeground3,
    flex: 1,
  },
  statValue: {
    fontVariantNumeric: 'tabular-nums',
    fontWeight: tokens.fontWeightSemibold,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  reasons: {
    margin: 0,
    paddingLeft: '20px',
  },
});

export interface HealthSnapshotProps {
  /** Cheap roll-up from api/Health/summary. Null until it lands (it is fetched without blocking the page). */
  summary: HealthSummary | null;
  /** Set when the roll-up couldn't be read - shown as a warning, never as a page-level failure. */
  summaryError: string | null;
  /** Heavy data section from api/Health/data. Null while in flight, or for good if its scan failed. */
  data: DataOverviewSection | null;
  /** Show audit-feed freshness (the activity import is switched on for this deployment). */
  showAuditFreshness: boolean;
  /** Show web-tracker freshness (the web traffic import is switched on for this deployment). */
  showWebFreshness: boolean;
}

/**
 * "Is the system healthy, and is data still arriving?" in one card.
 *
 * This deliberately duplicates a little of Administration -> Service health: the landing page is where
 * an admin first looks, and "we have 400k audit events" means nothing without "...and the newest one is
 * 10 minutes old". The detail, the per-section drill-down and the alerting guidance stay on the Health
 * page, which this links to.
 *
 * Both inputs are best-effort. The roll-up is cheap and server-cached; the freshness figures come from
 * the one genuinely heavy health endpoint, so a tenant whose bounded scan times out simply sees the
 * figures omitted rather than a broken home page.
 */
export default function HealthSnapshot({
  summary,
  summaryError,
  data,
  showAuditFreshness,
  showWebFreshness,
}: HealthSnapshotProps) {
  const styles = useStyles();
  const overall = summary?.overallStatus ?? null;

  // One flat list so each freshness figure sits next to the volume it explains, whatever the grid
  // wraps to - "newest audit event: 4 min ago" and "audit events (24h): 12,345" answer the same
  // question together and are close to meaningless apart.
  const stats: { key: string; label: string; content: ReactNode }[] = [];

  const addWorkload = (key: string, name: string, newestUtc: string | null, last24h: number | null) => {
    stats.push({
      key: `${key}-freshness`,
      label: `Newest ${name}`,
      content: (
        <Badge appearance="filled" color={freshnessColor(newestUtc, CYCLE_SLA_HOURS, CYCLE_SLA_HOURS * 2)}>
          {howLongAgo(newestUtc)}
        </Badge>
      ),
    });
    stats.push({
      key: `${key}-volume`,
      label: `${name.charAt(0).toUpperCase()}${name.slice(1)}s in the last 24h`,
      content: (
        <Text size={300} className={styles.statValue}>
          {formatCount(last24h)}
        </Text>
      ),
    });
  };

  if (showAuditFreshness) {
    addWorkload('audit', 'audit event', data?.newestAuditEventUtc ?? null, data?.auditEventsLast24h ?? null);
  }
  if (showWebFreshness) {
    addWorkload('hits', 'web page hit', data?.newestHitUtc ?? null, data?.hitsLast24h ?? null);
  }

  return (
    <Card className={styles.card}>
      <CardHeader
        header={
          <div className={styles.headerRow}>
            <Subtitle2 as="h2">System health</Subtitle2>
            <Badge appearance="filled" color={overallColor(overall)}>
              {overall ?? 'Checking...'}
            </Badge>
            <span className={styles.spacer} />
            <Link href="#/admin/health">Open Service health</Link>
          </div>
        }
      />

      {summaryError && (
        <MessageBar intent="warning">
          <MessageBarBody>
            The health summary couldn't be read ({summaryError}). The figures above come straight from the
            database and are unaffected.
          </MessageBarBody>
        </MessageBar>
      )}

      {summary && summary.sections.length > 0 && (
        <div className={styles.sections}>
          {summary.sections.map((s) => (
            <span key={s.key} className={styles.chip}>
              <Text size={200}>{s.label}</Text>
              <Badge appearance="filled" size="small" color={statusColor(s.status)}>
                {s.status}
              </Badge>
            </span>
          ))}
        </div>
      )}

      {summary && summary.overallReasons.length > 0 && (
        <ul className={styles.reasons}>
          {summary.overallReasons.slice(0, 4).map((r, i) => (
            <li key={i}>
              <Text size={200}>{r}</Text>
            </li>
          ))}
        </ul>
      )}

      {stats.length > 0 &&
        (data ? (
          <>
            <div className={styles.stats}>
              {stats.map((s) => (
                <div key={s.key} className={styles.stat}>
                  <Text size={200} className={styles.statLabel}>
                    {s.label}
                  </Text>
                  {s.content}
                </div>
              ))}
            </div>
            {data.recentVolumeError && (
              <Text size={200} className={styles.muted}>
                The freshness and 24h volume scan didn't finish on this database, so those figures show "-".
                This is expected on very large tenants.
              </Text>
            )}
          </>
        ) : (
          <Text size={200} className={styles.muted}>
            Checking how recent the imported data is...
          </Text>
        ))}
    </Card>
  );
}
