import {
  MessageBar,
  MessageBarBody,
  Table,
  TableHeader,
  TableRow,
  TableHeaderCell,
  TableBody,
  TableCell,
  Text,
  Badge,
} from '@fluentui/react-components';
import { fetchHealthData } from '../../api/healthApi';
import { formatNumber, useT } from '../../i18n';
import {
  CYCLE_SLA_HOURS,
  SectionFrame,
  formatCount,
  formatSize,
  formatUtc,
  freshnessColor,
  howLongAgo,
  useHealthSection,
  useHealthStyles,
} from './healthShared';

/**
 * Data overview (SQL). Approximate row counts + DB size are cheap DMV reads; the 24h/7d volume and
 * freshness come from bounded, timeout-capped scans that show "-" (with a note) rather than hanging on
 * a very large tenant. This is the only heavy sub-section, so it loads only when its tab is opened.
 */
export default function DataPanel({ active }: { active: boolean }) {
  const t = useT();
  const styles = useHealthStyles();
  const state = useHealthSection(fetchHealthData, active);

  return (
    <SectionFrame
      title={t('health.data.title')}
      description={t('health.data.description')}
      state={state}
    >
      {(data) => (
        <>
          {data.recentVolumeError && (
            <MessageBar intent="warning">
              <MessageBarBody>
                {t('health.data.recentVolumeWarning')}
              </MessageBarBody>
            </MessageBar>
          )}

          <Table size="small" aria-label={t('health.data.ariaLabel')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('health.data.columnWorkload')}</TableHeaderCell>
                <TableHeaderCell>
                  {t(
                    data.countsAreApproximate
                      ? 'health.data.columnRowsApproximate'
                      : 'health.data.columnRows',
                  )}
                </TableHeaderCell>
                <TableHeaderCell>{t('health.data.columnLast24h')}</TableHeaderCell>
                <TableHeaderCell>{t('health.data.columnLast7d')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow>
                <TableCell>{t('health.data.workloadActivityImports')}</TableCell>
                <TableCell>{formatNumber(data.activityCount)}</TableCell>
                <TableCell>{formatCount(data.auditEventsLast24h)}</TableCell>
                <TableCell>{formatCount(data.auditEventsLast7d)}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell>{t('health.data.workloadWebHits')}</TableCell>
                <TableCell>{formatNumber(data.hitCount)}</TableCell>
                <TableCell>{formatCount(data.hitsLast24h)}</TableCell>
                <TableCell>{formatCount(data.hitsLast7d)}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell>{t('health.data.workloadCopilotInteractions')}</TableCell>
                <TableCell>{formatNumber(data.copilotChatCount)}</TableCell>
                <TableCell colSpan={2}>
                  <Text size={200} className={styles.muted}>
                    {t('health.data.seeAuditEventFreshness')}
                  </Text>
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell>{t('health.data.workloadSentEmails')}</TableCell>
                <TableCell>{formatNumber(data.sentEmailCount)}</TableCell>
                <TableCell colSpan={2} />
              </TableRow>
              <TableRow>
                <TableCell>{t('health.data.workloadTeamsCallRecords')}</TableCell>
                <TableCell>{formatNumber(data.callRecordCount)}</TableCell>
                <TableCell colSpan={2} />
              </TableRow>
              <TableRow>
                <TableCell>{t('health.data.workloadTeamsDiscoveredTracked')}</TableCell>
                <TableCell>
                  {formatNumber(data.teamsCount)} / {formatNumber(data.teamsBeingTrackedCount)}
                </TableCell>
                <TableCell colSpan={2} />
              </TableRow>
              <TableRow>
                <TableCell>{t('health.data.workloadUsers')}</TableCell>
                <TableCell>{formatNumber(data.userCount)}</TableCell>
                <TableCell colSpan={2} />
              </TableRow>
            </TableBody>
          </Table>

          <Text className={styles.subHeading}>{t('health.data.freshnessHeading')}</Text>
          <Table size="small" aria-label={t('health.data.freshnessAriaLabel')}>
            <TableBody>
              <TableRow>
                <TableCell>{t('health.data.newestAuditEvent')}</TableCell>
                <TableCell>
                  {formatUtc(data.newestAuditEventUtc)}{' '}
                  <Badge appearance="filled" color={freshnessColor(data.newestAuditEventUtc, CYCLE_SLA_HOURS, CYCLE_SLA_HOURS * 2)}>
                    {howLongAgo(data.newestAuditEventUtc, t)}
                  </Badge>
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell>{t('health.data.newestHit')}</TableCell>
                <TableCell>
                  {formatUtc(data.newestHitUtc)}{' '}
                  <Badge appearance="filled" color={freshnessColor(data.newestHitUtc, CYCLE_SLA_HOURS, CYCLE_SLA_HOURS * 2)}>
                    {howLongAgo(data.newestHitUtc, t)}
                  </Badge>
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell>{t('health.data.databaseSize')}</TableCell>
                <TableCell>{formatSize(data.databaseSizeMb)}</TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </>
      )}
    </SectionFrame>
  );
}
