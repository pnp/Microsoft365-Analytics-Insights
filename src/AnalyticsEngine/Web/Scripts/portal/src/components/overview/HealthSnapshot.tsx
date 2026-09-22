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
import { useT, type TFunction, type TranslationKey } from '../../i18n';
import type { DataOverviewSection, HealthSummary } from '../../types/health';
import {
  CYCLE_SLA_HOURS,
  formatCount,
  freshnessColor,
  howLongAgo,
  overallColor,
  statusColor,
  translateHealthReasonText,
} from '../health/healthShared';


function sectionLabel(t: TFunction, key: string, fallback: string): string {
  if (!key) return fallback;
  const catalogKey = `health.section.${key}.label` as TranslationKey;
  const translated = t(catalogKey);
  return translated === catalogKey ? fallback : translated;
}

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
  const t = useT();
  const styles = useStyles();
  const overall = summary?.overallStatus ?? null;

  // One flat list so each freshness figure sits next to the volume it explains, whatever the grid
  // wraps to - "newest audit event: 4 min ago" and "audit events (24h): 12,345" answer the same
  // question together and are close to meaningless apart.
  const stats: { key: string; labelKey: TranslationKey; labelValues?: Record<string, string>; content: ReactNode }[] = [];

  const addWorkload = (key: string, name: string, labelKey: TranslationKey, newestUtc: string | null, last24h: number | null) => {
    stats.push({
      key: `${key}-freshness`,
      labelKey: 'overview.healthSnapshot.newest',
      labelValues: { name },
      content: (
        <Badge appearance="filled" color={freshnessColor(newestUtc, CYCLE_SLA_HOURS, CYCLE_SLA_HOURS * 2)}>
          {howLongAgo(newestUtc, t)}
        </Badge>
      ),
    });
    stats.push({
      key: `${key}-volume`,
      labelKey,
      content: (
        <Text size={300} className={styles.statValue}>
          {formatCount(last24h)}
        </Text>
      ),
    });
  };

  if (showAuditFreshness) {
    addWorkload('audit', t('overview.healthSnapshot.auditEventName'), 'overview.healthSnapshot.auditEventsLast24h', data?.newestAuditEventUtc ?? null, data?.auditEventsLast24h ?? null);
  }
  if (showWebFreshness) {
    addWorkload('hits', t('overview.healthSnapshot.webPageHitName'), 'overview.healthSnapshot.webPageHitsLast24h', data?.newestHitUtc ?? null, data?.hitsLast24h ?? null);
  }

  return (
    <Card className={styles.card}>
      <CardHeader
        header={
          <div className={styles.headerRow}>
            <Subtitle2 as="h2">{t('overview.healthSnapshot.title')}</Subtitle2>
            <Badge appearance="filled" color={overallColor(overall)}>
              {overall ? overviewStatusText(overall, t) : t('overview.healthSnapshot.checking')}
            </Badge>
            <span className={styles.spacer} />
            <Link href="#/admin/health">{t('overview.healthSnapshot.openServiceHealth')}</Link>
          </div>
        }
      />

      {summaryError && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {t('overview.healthSnapshot.summaryError', { error: summaryError })}
          </MessageBarBody>
        </MessageBar>
      )}

      {summary && summary.sections.length > 0 && (
        <div className={styles.sections}>
          {summary.sections.map((s) => (
            <span key={s.key} className={styles.chip}>
              <Text size={200}>{sectionLabel(t, s.key, s.label)}</Text>
              <Badge appearance="filled" size="small" color={statusColor(s.status)}>
                {overviewStatusText(s.status, t)}
              </Badge>
            </span>
          ))}
        </div>
      )}

      {summary && summary.overallReasons.length > 0 && (
        <ul className={styles.reasons}>
          {summary.overallReasons.slice(0, 4).map((r, i) => (
            <li key={i}>
              <Text size={200}>{translateHealthReasonText(r, t)}</Text>
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
                    {t(s.labelKey, s.labelValues)}
                  </Text>
                  {s.content}
                </div>
              ))}
            </div>
            {data.recentVolumeError && (
              <Text size={200} className={styles.muted}>
                {t('overview.healthSnapshot.recentVolumeWarning')}
              </Text>
            )}
          </>
        ) : (
          <Text size={200} className={styles.muted}>
            {t('overview.healthSnapshot.checkingFreshness')}
          </Text>
        ))}
    </Card>
  );
}

function overviewStatusText(status: string | null, t: TFunction): string {
  switch ((status ?? '').toLowerCase()) {
    case 'healthy':
      return t('overview.status.healthy');
    case 'degraded':
      return t('overview.status.degraded');
    case 'unhealthy':
      return t('overview.status.unhealthy');
    default:
      return t('overview.status.unknown');
  }
}
