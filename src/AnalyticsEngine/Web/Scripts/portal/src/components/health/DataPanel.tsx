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
  const styles = useHealthStyles();
  const state = useHealthSection(fetchHealthData, active);

  return (
    <SectionFrame
      title="Data overview"
      description="Volume and freshness from the database. Row counts are approximate (read from index metadata, so a large tenant isn't hit with a COUNT(*) on every load); the last 24h / 7d columns show what's actually flowing in."
      state={state}
    >
      {(data) => (
        <>
          {data.recentVolumeError && (
            <MessageBar intent="warning">
              <MessageBarBody>
                The 24h/7d volume and freshness scan didn't finish in time on this database, so those columns show "-".
                The approximate totals still load. (This is expected on very large tenants - the timestamp columns
                aren't indexed.)
              </MessageBarBody>
            </MessageBar>
          )}

          <Table size="small" aria-label="Data overview">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Workload</TableHeaderCell>
                <TableHeaderCell>Rows{data.countsAreApproximate ? ' (approx)' : ''}</TableHeaderCell>
                <TableHeaderCell>Last 24h</TableHeaderCell>
                <TableHeaderCell>Last 7d</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow>
                <TableCell>Activity imports (audit events)</TableCell>
                <TableCell>{data.activityCount.toLocaleString()}</TableCell>
                <TableCell>{formatCount(data.auditEventsLast24h)}</TableCell>
                <TableCell>{formatCount(data.auditEventsLast7d)}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Web hits</TableCell>
                <TableCell>{data.hitCount.toLocaleString()}</TableCell>
                <TableCell>{formatCount(data.hitsLast24h)}</TableCell>
                <TableCell>{formatCount(data.hitsLast7d)}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Copilot interactions</TableCell>
                <TableCell>{data.copilotChatCount.toLocaleString()}</TableCell>
                <TableCell colSpan={2}>
                  <Text size={200} className={styles.muted}>
                    see audit-event freshness
                  </Text>
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Sent emails</TableCell>
                <TableCell>{data.sentEmailCount.toLocaleString()}</TableCell>
                <TableCell colSpan={2} />
              </TableRow>
              <TableRow>
                <TableCell>Teams call records</TableCell>
                <TableCell>{data.callRecordCount.toLocaleString()}</TableCell>
                <TableCell colSpan={2} />
              </TableRow>
              <TableRow>
                <TableCell>Teams discovered / tracked</TableCell>
                <TableCell>
                  {data.teamsCount.toLocaleString()} / {data.teamsBeingTrackedCount.toLocaleString()}
                </TableCell>
                <TableCell colSpan={2} />
              </TableRow>
              <TableRow>
                <TableCell>Users</TableCell>
                <TableCell>{data.userCount.toLocaleString()}</TableCell>
                <TableCell colSpan={2} />
              </TableRow>
            </TableBody>
          </Table>

          <Text className={styles.subHeading}>Freshness</Text>
          <Table size="small" aria-label="Data freshness">
            <TableBody>
              <TableRow>
                <TableCell>Newest audit event</TableCell>
                <TableCell>
                  {formatUtc(data.newestAuditEventUtc)}{' '}
                  <Badge appearance="filled" color={freshnessColor(data.newestAuditEventUtc, CYCLE_SLA_HOURS, CYCLE_SLA_HOURS * 2)}>
                    {howLongAgo(data.newestAuditEventUtc)}
                  </Badge>
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Newest hit</TableCell>
                <TableCell>
                  {formatUtc(data.newestHitUtc)}{' '}
                  <Badge appearance="filled" color={freshnessColor(data.newestHitUtc, CYCLE_SLA_HOURS, CYCLE_SLA_HOURS * 2)}>
                    {howLongAgo(data.newestHitUtc)}
                  </Badge>
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Database size (data files)</TableCell>
                <TableCell>{formatSize(data.databaseSizeMb)}</TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </>
      )}
    </SectionFrame>
  );
}
