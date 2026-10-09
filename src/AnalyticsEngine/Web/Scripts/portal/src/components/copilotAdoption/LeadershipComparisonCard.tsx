import { Card, MessageBar, MessageBarBody, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, makeStyles, tokens } from '@fluentui/react-components';
import InfoTip from '../shared/InfoTip';
import { formatDate, formatPct } from '../shared/KpiGrid';
import { formatNumber, useT, type TFunction, type TranslationKey } from '../../i18n';
import type { LeadershipAdoptionComparison, LeadershipComparisonReason, LeadershipComparisonStatus } from '../../types/leadershipCohort';

/** `LeadershipComparisonStatuses` in Common.Entities, except `ok`, which shows figures rather than a message. */
export const LEADERSHIP_STATUS_KEYS: Record<Exclude<LeadershipComparisonStatus, 'ok'>, TranslationKey> = {
  notConfigured: 'copilotAdoption.leadership.status.notConfigured',
  suppressed: 'copilotAdoption.leadership.status.suppressed',
  pendingRefresh: 'copilotAdoption.leadership.status.pendingRefresh',
  unavailable: 'copilotAdoption.leadership.status.unavailable',
  stale: 'copilotAdoption.leadership.status.stale',
  scopedView: 'copilotAdoption.leadership.status.scopedView',
};

/** `LeadershipComparisonReasons` in Common.Entities. */
export const LEADERSHIP_REASON_KEYS: Record<LeadershipComparisonReason, TranslationKey> = {
  groupNotFound: 'copilotAdoption.leadership.reason.groupNotFound',
  permissionMissing: 'copilotAdoption.leadership.reason.permissionMissing',
  tooLarge: 'copilotAdoption.leadership.reason.tooLarge',
  refreshFailed: 'copilotAdoption.leadership.reason.refreshFailed',
  stateUnavailable: 'copilotAdoption.leadership.reason.stateUnavailable',
  membershipChanging: 'copilotAdoption.leadership.reason.membershipChanging',
  complementTooSmall: 'copilotAdoption.leadership.reason.complementTooSmall',
};

const useStyles = makeStyles({
  head: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: '8px' },
  body: { display: 'flex', flexDirection: 'column', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

function signed(value: number, format: (abs: number) => string): string {
  const sign = value > 0 ? '+' : value < 0 ? '\u2212' : '';
  return `${sign}${format(Math.abs(value))}`;
}

function points(t: TFunction, value: number | null): string {
  if (value === null) return '\u2014';
  return t('copilotAdoption.leadership.points', { value: signed(value, (v) => formatNumber(v, { minimumFractionDigits: 1, maximumFractionDigits: 1 })) });
}

function pct(value: number | null): string {
  return value === null ? '\u2014' : formatPct(value);
}

function score(value: number | null): string {
  return value === null ? '\u2014' : formatNumber(value, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

function scoreGap(value: number | null): string {
  return value === null ? '\u2014' : signed(value, (v) => formatNumber(v, { minimumFractionDigits: 1, maximumFractionDigits: 1 }));
}

function statusMessage(t: TFunction, comparison: LeadershipAdoptionComparison): string {
  // Suppressed because too few licensed people are outside the cohort: a sentence of its own, not a fragment.
  if (comparison.status === 'suppressed' && comparison.reason === 'complementTooSmall') {
    return t('copilotAdoption.leadership.reason.complementTooSmall', { min: formatNumber(comparison.minimumCohort) });
  }
  const key = LEADERSHIP_STATUS_KEYS[comparison.status as Exclude<LeadershipComparisonStatus, 'ok'>]
    ?? 'copilotAdoption.leadership.status.unavailable';
  const reasonKey = comparison.reason ? LEADERSHIP_REASON_KEYS[comparison.reason as LeadershipComparisonReason] : undefined;
  return t(key, {
    min: formatNumber(comparison.minimumCohort),
    date: formatDate(comparison.membershipRefreshedUtc),
    reason: t(reasonKey ?? 'copilotAdoption.leadership.reason.refreshFailed'),
  });
}

/**
 * How the configured leadership group's Copilot adoption compares with the whole tenant's (#654). Aggregates only:
 * the server never sends the group's name, id or members, and it withholds every figure below the minimum cohort.
 */
export default function LeadershipComparisonCard({ comparison }: { comparison: LeadershipAdoptionComparison | null | undefined }) {
  const t = useT();
  const styles = useStyles();
  if (!comparison) return null;
  const ok = comparison.status === 'ok';

  return <Card>
    <div className={styles.head}>
      <div>
        <Text weight="semibold" size={400}>{t('copilotAdoption.leadership.title')}</Text>
        <Text size={200} block className={styles.muted}>{t('copilotAdoption.leadership.subtitle')}</Text>
      </div>
      <InfoTip
        title={t('copilotAdoption.leadership.title')}
        content={{
          what: t('copilotAdoption.leadership.info.what'),
          how: t('copilotAdoption.leadership.info.how', { min: formatNumber(comparison.minimumCohort) }),
          source: t('copilotAdoption.leadership.info.source'),
        }}
      />
    </div>
    <div className={styles.body}>
      {!ok && <MessageBar intent={comparison.status === 'unavailable' || comparison.status === 'stale' ? 'warning' : 'info'}>
        <MessageBarBody>{statusMessage(t, comparison)}</MessageBarBody>
      </MessageBar>}
      {ok && <>
        <Text size={200}>
          {t('copilotAdoption.leadership.cohort', {
            licensed: formatNumber(comparison.licensedLeaders ?? 0),
            active: formatNumber(comparison.activeLeaders ?? 0),
            date: formatDate(comparison.membershipRefreshedUtc),
          })}
        </Text>
        <Table size="small" aria-label={t('copilotAdoption.leadership.title')}>
          <TableHeader><TableRow>
            <TableHeaderCell>{t('copilotAdoption.leadership.column.measure')}</TableHeaderCell>
            <TableHeaderCell>{t('copilotAdoption.leadership.column.leaders')}</TableHeaderCell>
            <TableHeaderCell>{t('copilotAdoption.leadership.column.tenant')}</TableHeaderCell>
            <TableHeaderCell>{t('copilotAdoption.leadership.column.gap')}</TableHeaderCell>
          </TableRow></TableHeader>
          <TableBody>
            <TableRow>
              <TableCell>{t('copilotAdoption.leadership.row.adoption')}</TableCell>
              <TableCell>{pct(comparison.leaderAdoptionRatePct)}</TableCell>
              <TableCell>{pct(comparison.tenantAdoptionRatePct)}</TableCell>
              <TableCell>{points(t, comparison.adoptionGapPts)}</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>{t('copilotAdoption.leadership.row.habit')}</TableCell>
              <TableCell>{pct(comparison.leaderHabitRatePct)}</TableCell>
              <TableCell>{pct(comparison.tenantHabitRatePct)}</TableCell>
              <TableCell>{points(t, comparison.habitGapPts)}</TableCell>
            </TableRow>
            <TableRow>
              <TableCell>{t('copilotAdoption.leadership.row.score')}</TableCell>
              <TableCell>{score(comparison.leaderAverageScore)}</TableCell>
              <TableCell>{score(comparison.tenantAverageScore)}</TableCell>
              <TableCell>{scoreGap(comparison.scoreGap)}</TableCell>
            </TableRow>
          </TableBody>
        </Table>
        {comparison.figuresIncomplete && <Text size={200} className={styles.muted}>{t('copilotAdoption.leadership.figuresIncomplete')}</Text>}
      </>}
    </div>
  </Card>;
}
