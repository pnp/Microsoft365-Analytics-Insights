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
import { formatNumber, useT } from '../../i18n';
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
  const t = useT();
  const styles = useHealthStyles();
  const state = useHealthSection(fetchHealthLiveness, active);

  return (
    <SectionFrame
      title={t('health.liveness.title')}
      description={t('health.liveness.description', { hours: formatNumber(CYCLE_SLA_HOURS) })}
      state={state}
    >
      {(data) =>
        !data.appInsightsConfigured ? (
          <MessageBar intent="info">
            <MessageBarBody>{t('health.liveness.appInsightsNotConfigured')}</MessageBarBody>
          </MessageBar>
        ) : data.livenessError ? (
          <MessageBar intent="warning">
            <MessageBarBody>{t('health.liveness.loadError', { error: data.livenessError })}</MessageBarBody>
          </MessageBar>
        ) : (
          <>
            <Text className={styles.subHeading}>{t('health.liveness.lastCycleHeading')}</Text>
            {data.lastCyclePerJob.length > 0 ? (
              <Table size="small" aria-label={t('health.liveness.lastCycleAriaLabel')}>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>{t('health.liveness.columnImporter')}</TableHeaderCell>
                    <TableHeaderCell>{t('health.liveness.columnLastCycleUtc')}</TableHeaderCell>
                    <TableHeaderCell>{t('health.liveness.columnFreshness')}</TableHeaderCell>
                    <TableHeaderCell>{t('health.liveness.columnDuration')}</TableHeaderCell>
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
                          {howLongAgo(job.lastCycleUtc, t)}
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
                <MessageBarBody>{t('health.liveness.noFinishedImportCycles')}</MessageBarBody>
              </MessageBar>
            )}

            <Text className={styles.subHeading}>{t('health.liveness.webTrackerHeading')}</Text>
            <div>
              <Badge appearance="filled" color={data.pageViewsLast24h > 0 ? 'success' : 'warning'}>
                {t('health.liveness.pageViewsBadge', { count: formatNumber(data.pageViewsLast24h) })}
              </Badge>{' '}
              <Text size={200}>
                {data.pageViewsLast24h > 0
                  ? t('health.liveness.lastSeen', { when: howLongAgo(data.newestPageViewUtc, t) })
                  : t('health.liveness.noPageViews')}
              </Text>
            </div>

            <Text className={styles.subHeading}>{t('health.liveness.lastRunHeading')}</Text>
            {data.lastSectionImports.length > 0 ? (
              <Table size="small" aria-label={t('health.liveness.lastSectionAriaLabel')}>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>{t('health.liveness.columnSection')}</TableHeaderCell>
                    <TableHeaderCell>{t('health.liveness.columnImporter')}</TableHeaderCell>
                    <TableHeaderCell>{t('health.liveness.columnLastRunUtc')}</TableHeaderCell>
                    <TableHeaderCell>{t('health.liveness.columnFreshness')}</TableHeaderCell>
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
                          {howLongAgo(s.lastRunUtc, t)}
                        </Badge>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            ) : (
              <Text>{t('health.liveness.noFinishedSectionImports')}</Text>
            )}

            {data.lastHeartbeats.length > 0 ? (
              <>
                <Text className={styles.subHeading}>{t('health.liveness.heartbeatsHeading')}</Text>
                <Table size="small" aria-label={t('health.liveness.heartbeatsAriaLabel')}>
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>{t('health.liveness.columnJob')}</TableHeaderCell>
                      <TableHeaderCell>{t('health.liveness.columnLastBeatUtc')}</TableHeaderCell>
                      <TableHeaderCell>{t('health.liveness.columnFreshness')}</TableHeaderCell>
                      <TableHeaderCell>{t('health.liveness.columnLastCycleSecs')}</TableHeaderCell>
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
                            {howLongAgo(b.lastBeatUtc, t)}
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
                {t('health.liveness.noHeartbeats')}
              </Text>
            )}
          </>
        )
      }
    </SectionFrame>
  );
}
