import type { ReactElement } from 'react';
import { Card, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import {
  Apps20Regular,
  Call20Regular,
  Cloud20Regular,
  Database20Regular,
  DataUsage20Regular,
  DocumentText20Regular,
  Globe20Regular,
  History20Regular,
  Link20Regular,
  Mail20Regular,
  Money20Regular,
  People20Regular,
  PeopleTeam20Regular,
  ShieldProhibited20Regular,
  Sparkle20Regular,
} from '@fluentui/react-icons';
import type { NamedCount } from '../../types/systemStatus';
import { formatNumber } from '../../i18n';

/**
 * Icon per figure, keyed off the server's stable `key` rather than the display label - renaming a
 * label is a copy edit and must not silently drop the icon. Anything unrecognised (a figure added
 * server-side before this map catches up) falls back to a generic database glyph rather than a gap.
 */
const ICONS: Record<string, ReactElement> = {
  users: <People20Regular />,
  auditEvents: <History20Regular />,
  copilotInteractions: <Sparkle20Regular />,
  copilotAiInteractions: <Sparkle20Regular />,
  webHits: <Globe20Regular />,
  trackedUrls: <Link20Regular />,
  sharePointSites: <DocumentText20Regular />,
  sentEmails: <Mail20Regular />,
  teams: <PeopleTeam20Regular />,
  teamsTracked: <PeopleTeam20Regular />,
  teamsCalls: <Call20Regular />,
  powerApps: <Apps20Regular />,
  dlpMatches: <ShieldProhibited20Regular />,
  licenceTypes: <DataUsage20Regular />,
  copilotStudioCreditDays: <Money20Regular />,
  azureCostDays: <Cloud20Regular />,
};

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))',
    gap: '12px',
  },
  tile: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '14px 16px',
  },
  head: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    color: tokens.colorNeutralForeground3,
  },
  label: {
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  value: {
    fontSize: '28px',
    lineHeight: '34px',
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorNeutralForeground1,
  },
  /** Nothing imported yet: muted, so a screen of real numbers reads at a glance. */
  valueEmpty: {
    color: tokens.colorNeutralForeground4,
  },
  hint: {
    color: tokens.colorNeutralForeground3,
  },
});

/**
 * The home page's headline figures, one tile per row count.
 *
 * The server decides *which* figures appear - it only returns the ones belonging to an import this
 * deployment actually runs (see SystemStatusAPIController). That matters: a permanent "Teams calls: 0"
 * for a tenant that never switched the calls import on is indistinguishable from a broken import, and
 * it is the first thing an admin sees.
 */
export default function DataKpiTiles({ counts }: { counts: NamedCount[] }) {
  const styles = useStyles();

  return (
    <div className={styles.grid}>
      {counts.map((c) => (
        <Card key={c.key || c.name} className={styles.tile}>
          <div className={styles.head}>
            {ICONS[c.key] ?? <Database20Regular />}
            <Text size={200} weight="semibold" className={styles.label}>
              {c.name}
            </Text>
          </div>
          {/* mergeClasses, not string concatenation: both rules set `color`, and only Griffel's
              merge resolves that deterministically. */}
          <span className={mergeClasses(styles.value, c.count === 0 && styles.valueEmpty)}>
            {formatNumber(c.count)}
          </span>
          {c.hint && (
            <Text size={200} className={styles.hint}>
              {c.hint}
            </Text>
          )}
        </Card>
      ))}
    </div>
  );
}
