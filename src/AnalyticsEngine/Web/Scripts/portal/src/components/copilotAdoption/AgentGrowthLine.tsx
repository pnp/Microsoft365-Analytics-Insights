import { makeStyles, tokens, Text, Card } from '@fluentui/react-components';
import type { AgentEstateSummary, AgentGrowthWindow } from '../../types/copilotAdoption';
import SqlPopover from '../SqlPopover';
import InfoTip from '../shared/InfoTip';
import { formatCount, formatDate } from '../shared/KpiGrid';
import { formatList, formatNumber, useT, type TFunction, type TranslationKey } from '../../i18n';

/** The window that covers the same 28 days as window 0, a year (364 days) earlier. */
export const YEAR_AGO_WINDOW = 13;

const useStyles = makeStyles({
  head: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
  },
  tools: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    flexShrink: 0,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  line: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: '8px 24px',
    marginTop: '10px',
  },
  stat: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: '120px',
  },
  value: {
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  // The autonomous-run evidence is a different measurement from a different source, so it is set
  // apart rather than sitting in the same run of figures as the user-initiated counts.
  evidence: {
    display: 'flex',
    flexDirection: 'column',
    maxWidth: '320px',
    paddingLeft: '16px',
    borderLeftWidth: '2px',
    borderLeftStyle: 'dashed',
    borderLeftColor: tokens.colorNeutralStroke1,
  },
  notes: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    marginTop: '10px',
  },
  caveat: {
    color: tokens.colorNeutralForeground2,
    fontStyle: 'italic',
  },
});

type Stat = {
  key: string;
  labelKey: TranslationKey;
  value: (w: AgentGrowthWindow | undefined) => number | null | undefined;
  format: (v: number) => string;
};

/** The user-initiated figures, in reading order. Keys, not text: resolved where they are rendered. */
const STATS: Stat[] = [
  { key: 'agents', labelKey: 'copilotAdoptionAgents.agents.growth.stat.activeAgents', value: (w) => w?.activeAgents, format: formatCount },
  { key: 'users', labelKey: 'copilotAdoptionAgents.agents.growth.stat.agentUsers', value: (w) => w?.agentUsers, format: formatCount },
  { key: 'interactions', labelKey: 'copilotAdoptionAgents.agents.growth.stat.interactions', value: (w) => w?.agentInteractions, format: formatCount },
  { key: 'perUser', labelKey: 'copilotAdoptionAgents.agents.growth.stat.perUser', value: (w) => w?.interactionsPerAgentUser, format: formatTenths },
];

function formatTenths(value: number): string {
  return formatNumber(value, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

/** A blank figure is "not measured" - shown as a dash, never as zero. */
function show(value: number | null | undefined, format: (v: number) => string): string {
  return value === null || value === undefined ? '\u2014' : format(value);
}

/**
 * Year-on-year agent growth, as one line on the Agents tab (#645): the most recent closed 28-day window
 * against the same 28 days a year earlier, a sparkline of all fourteen, and Copilot Studio billing kept
 * apart as evidence of autonomous runs.
 *
 * One line rather than another chart card on purpose (#552). And the caveat is on screen, not behind the
 * info tip: the series compares the organisation with itself, and the Work Trend Index's headline growth
 * describes Microsoft's whole customer base - it is not a target (#547).
 */
export default function AgentGrowthLine({
  estate,
  lagDays,
  sql,
  billingSql,
}: {
  estate: AgentEstateSummary;
  /** The settled-day margin, so the definition can say how many days before today the windows stop. */
  lagDays: number;
  sql?: string;
  billingSql?: string;
}) {
  const styles = useStyles();
  const t = useT();

  const growth = estate.growth ?? [];
  if (growth.length === 0) return null;

  const customerBuilt = estate.growthScope === 'customerBuilt';
  const latest = growth.find((w) => w.windowsAgo === 0);
  const yearAgo = growth.find((w) => w.windowsAgo === YEAR_AGO_WINDOW);
  const chronological = [...growth].sort((a, b) => b.windowsAgo - a.windowsAgo);
  const unmeasured = [latest, yearAgo].some((w) => !w || w.activeAgents === null);
  const historyStart = estate.growthAuditHistoryStartUtc ?? null;
  const historyStartsLate = !!historyStart && !!yearAgo && historyStart.slice(0, 10) > yearAgo.fromUtc.slice(0, 10);

  return (
    <Card>
      <div className={styles.head}>
        <div>
          <Text weight="semibold" size={400}>
            {t('copilotAdoptionAgents.agents.growth.title')}
          </Text>
          <Text size={200} block className={styles.muted}>
            {t(
              customerBuilt
                ? 'copilotAdoptionAgents.agents.growth.subtitle.customerBuilt'
                : 'copilotAdoptionAgents.agents.growth.subtitle.allAgents',
              { to: formatDate(latest?.toUtc), yearAgoTo: formatDate(yearAgo?.toUtc) },
            )}
          </Text>
        </div>
        <div className={styles.tools}>
          <InfoTip
            title={t('copilotAdoptionAgents.agents.growth.title')}
            content={{
              what: t('copilotAdoptionAgents.agents.growth.what'),
              how: t('copilotAdoptionAgents.agents.growth.how', { lagDays }),
              source: t('copilotAdoptionAgents.agents.growth.source'),
            }}
          />
          {sql && <SqlPopover sql={sql} title={t('copilotAdoptionAgents.agents.growth.sqlTitle')} />}
          {billingSql && <SqlPopover sql={billingSql} title={t('copilotAdoptionAgents.agents.growth.billingSqlTitle')} />}
        </div>
      </div>

      <div className={styles.line}>
        <GrowthSparkline windows={chronological} t={t} />
        {STATS.map((stat) => (
          <div key={stat.key} className={styles.stat}>
            <Text size={200} className={styles.muted}>
              {t(stat.labelKey)}
            </Text>
            <span className={styles.value}>{show(stat.value(latest), stat.format)}</span>
            <Text size={100} className={styles.muted}>
              {yearAgoText(stat.value(yearAgo), stat.format, t)}
            </Text>
          </div>
        ))}
        <div className={styles.evidence}>
          <Text size={200} className={styles.muted}>
            {t('copilotAdoptionAgents.agents.growth.evidence.label')}
          </Text>
          <span className={styles.value}>{show(latest?.copilotStudioBilledAgents, formatCount)}</span>
          <Text size={100} className={styles.muted}>
            {growth.every((w) => w.copilotStudioBilledAgents === null)
              ? t('copilotAdoptionAgents.agents.growth.evidence.unavailable')
              : yearAgoText(yearAgo?.copilotStudioBilledAgents, formatCount, t)}
          </Text>
          <Text size={100} className={styles.muted}>
            {t('copilotAdoptionAgents.agents.growth.evidence.note')}
          </Text>
        </div>
      </div>

      <div className={styles.notes}>
        {unmeasured && (
          <Text size={200} className={styles.muted}>
            {historyStartsLate
              ? t('copilotAdoptionAgents.agents.growth.notMeasured.historyStart', { date: formatDate(historyStart) })
              : t('copilotAdoptionAgents.agents.growth.notMeasured.noData')}
          </Text>
        )}
        <Text size={200} className={styles.muted}>
          {t('copilotAdoptionAgents.agents.growth.sources')}
        </Text>
        <Text size={200} className={styles.caveat}>
          {t('copilotAdoptionAgents.agents.growth.caveat')}
        </Text>
      </div>
    </Card>
  );
}

function yearAgoText(value: number | null | undefined, format: (v: number) => string, t: TFunction): string {
  return value === null || value === undefined
    ? t('copilotAdoptionAgents.agents.growth.yearAgo.notMeasured')
    : t('copilotAdoptionAgents.agents.growth.yearAgo', { value: format(value) });
}

/**
 * Active agents in every window, oldest first, as a small inline line. An unmeasured window is a gap in
 * the line, not a dip to zero - drawing it at zero would show a collapse that never happened.
 */
function GrowthSparkline({ windows, t }: { windows: AgentGrowthWindow[]; t: TFunction }) {
  const width = 150;
  const height = 36;
  const pad = 4;

  const values = windows.map((w) => w.activeAgents);
  const max = Math.max(1, ...values.filter((v): v is number => v !== null));
  const step = values.length > 1 ? (width - 2 * pad) / (values.length - 1) : 0;
  const point = (v: number, i: number) => ({ x: pad + i * step, y: height - pad - (v / max) * (height - 2 * pad) });

  const segments: { x: number; y: number }[][] = [];
  let current: { x: number; y: number }[] = [];
  values.forEach((v, i) => {
    if (v === null) {
      if (current.length > 0) segments.push(current);
      current = [];
      return;
    }
    current.push(point(v, i));
  });
  if (current.length > 0) segments.push(current);

  const lastIndex = values.length - 1;
  const last = values[lastIndex];
  const label = t('copilotAdoptionAgents.agents.growth.sparkline.ariaLabel', {
    count: values.length,
    values: formatList(
      values.map((v) => (v === null ? t('copilotAdoptionAgents.agents.growth.sparkline.notMeasured') : formatCount(v))),
    ),
  });

  return (
    <svg role="img" aria-label={label} width={width} height={height} viewBox={`0 0 ${width} ${height}`}>
      {segments.map((segment, i) =>
        segment.length > 1 ? (
          <polyline
            key={i}
            points={segment.map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ')}
            fill="none"
            stroke={tokens.colorBrandForeground1}
            strokeWidth={2}
            strokeLinejoin="round"
            strokeLinecap="round"
          />
        ) : (
          <circle key={i} cx={segment[0].x} cy={segment[0].y} r={1.5} fill={tokens.colorBrandForeground1} />
        ),
      )}
      {last !== null && last !== undefined && (
        <circle cx={point(last, lastIndex).x} cy={point(last, lastIndex).y} r={3} fill={tokens.colorBrandForeground1} />
      )}
    </svg>
  );
}
