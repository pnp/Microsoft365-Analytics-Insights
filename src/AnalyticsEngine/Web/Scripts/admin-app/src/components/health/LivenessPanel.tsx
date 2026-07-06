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
import { fetchHealthLiveness } from '../../api/healthApi';
import {
  CYCLE_SLA_HOURS,
  SectionFrame,
  formatUtc,
  freshnessColor,
  howLongAgo,
  useHealthSection,
  useHealthStyles,
} from './healthShared';

/** Import liveness (App Insights): is each importer still looping and finishing? */
export default function LivenessPanel({ active }: { active: boolean }) {
  const styles = useHealthStyles();
  const state = useHealthSection(fetchHealthLiveness, active);

  return (
    <SectionFrame
      title="Import liveness"
      description={`Is each importer still looping and finishing? A full activity import cycle should complete at least once every ${CYCLE_SLA_HOURS} hours. "Last confirmed cycle" is the FinishedImportCycle event; the per-section rows are the FinishedSectionImport events.`}
      state={state}
    >
      {(data) =>
        !data.appInsightsConfigured ? (
          <MessageBar intent="info">
            <MessageBarBody>Application Insights is not configured, so import liveness is unavailable.</MessageBarBody>
          </MessageBar>
        ) : data.livenessError ? (
          <MessageBar intent="warning">
            <MessageBarBody>Couldn't load import liveness: {data.livenessError}</MessageBarBody>
          </MessageBar>
        ) : (
          <>
            <Text className={styles.subHeading}>Last confirmed cycle per job</Text>
            {data.lastCyclePerJob.length > 0 ? (
              <Table size="small" aria-label="Last cycle per job">
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Importer</TableHeaderCell>
                    <TableHeaderCell>Last cycle (UTC)</TableHeaderCell>
                    <TableHeaderCell>Freshness</TableHeaderCell>
                    <TableHeaderCell>Duration</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.lastCyclePerJob.map((job, i) => (
                    <TableRow key={job.jobName ?? i}>
                      <TableCell>
                        <Text font="monospace">{job.jobName}</Text>
                      </TableCell>
                      <TableCell>{formatUtc(job.lastCycleUtc)}</TableCell>
                      <TableCell>
                        <Badge appearance="filled" color={freshnessColor(job.lastCycleUtc, CYCLE_SLA_HOURS, CYCLE_SLA_HOURS * 2)}>
                          {howLongAgo(job.lastCycleUtc)}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <Text size={200}>{job.duration}</Text>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            ) : (
              <MessageBar intent="info">
                <MessageBarBody>No FinishedImportCycle events in the retention window yet.</MessageBarBody>
              </MessageBar>
            )}

            <Text className={styles.subHeading}>Web tracker (pageViews in App Insights, last 24h)</Text>
            <div>
              <Badge appearance="filled" color={data.pageViewsLast24h > 0 ? 'success' : 'warning'}>
                {data.pageViewsLast24h.toLocaleString()} pageViews
              </Badge>{' '}
              <Text size={200}>
                {data.pageViewsLast24h > 0
                  ? `last seen ${howLongAgo(data.newestPageViewUtc)}`
                  : 'none - the web tracker may not be deployed on the site, or is not sending to App Insights'}
              </Text>
            </div>

            <Text className={styles.subHeading}>Last run per section</Text>
            {data.lastSectionImports.length > 0 ? (
              <Table size="small" aria-label="Last section imports">
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Section</TableHeaderCell>
                    <TableHeaderCell>Importer</TableHeaderCell>
                    <TableHeaderCell>Last run (UTC)</TableHeaderCell>
                    <TableHeaderCell>Freshness</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.lastSectionImports.map((s, i) => (
                    <TableRow key={(s.sectionName ?? '') + i}>
                      <TableCell>{s.sectionName}</TableCell>
                      <TableCell>
                        <Text font="monospace">{s.jobName}</Text>
                      </TableCell>
                      <TableCell>{formatUtc(s.lastRunUtc)}</TableCell>
                      <TableCell>
                        <Badge appearance="filled" color={freshnessColor(s.lastRunUtc, CYCLE_SLA_HOURS, CYCLE_SLA_HOURS * 3)}>
                          {howLongAgo(s.lastRunUtc)}
                        </Badge>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            ) : (
              <Text>No FinishedSectionImport events in the retention window yet.</Text>
            )}

            {data.lastHeartbeats.length > 0 ? (
              <>
                <Text className={styles.subHeading}>Importer heartbeats</Text>
                <Table size="small" aria-label="Importer heartbeats">
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>Job</TableHeaderCell>
                      <TableHeaderCell>Last beat (UTC)</TableHeaderCell>
                      <TableHeaderCell>Freshness</TableHeaderCell>
                      <TableHeaderCell>Last cycle secs</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {data.lastHeartbeats.map((b, i) => (
                      <TableRow key={(b.jobName ?? '') + i}>
                        <TableCell>
                          <Text font="monospace">{b.jobName}</Text>
                        </TableCell>
                        <TableCell>{formatUtc(b.lastBeatUtc)}</TableCell>
                        <TableCell>
                          <Badge appearance="filled" color={freshnessColor(b.lastBeatUtc, 0.5, 1)}>
                            {howLongAgo(b.lastBeatUtc)}
                          </Badge>
                        </TableCell>
                        <TableCell>{b.lastCycleDurationSeconds}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </>
            ) : (
              <Text size={200} className={styles.muted}>
                Independent-timer ImporterHeartbeat events are not being emitted yet (that host is a later phase).
                Until then, "Last confirmed cycle" above is the liveness signal - note it only fires when a cycle
                completes, so a job stuck mid-cycle would still look recent.
              </Text>
            )}
          </>
        )
      }
    </SectionFrame>
  );
}
