import { useEffect, useState } from 'react';
import {
  Card,
  CardHeader,
  Title3,
  Subtitle2,
  Text,
  Badge,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  MessageBar,
  MessageBarBody,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { fetchHealth } from '../api/healthApi';
import type { HealthDashboard } from '../types/health';
import Spinner from '../components/Spinner';

type BadgeColor = 'success' | 'warning' | 'danger' | 'informative' | 'subtle';

// A full activity import cycle should complete at least this often (see HEALTH-MONITORING-DESIGN.md).
const CYCLE_SLA_HOURS = 24;

function minutesAgo(iso: string | null): number | null {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return null;
  return (Date.now() - t) / 60000;
}

function howLongAgo(iso: string | null): string {
  const m = minutesAgo(iso);
  if (m === null) return 'never';
  if (m < 1) return 'just now';
  if (m < 60) return `${Math.round(m)} min ago`;
  if (m < 60 * 24) return `${(m / 60).toFixed(1)} hours ago`;
  return `${(m / 60 / 24).toFixed(1)} days ago`;
}

function freshnessColor(iso: string | null, greenHours: number, amberHours: number): BadgeColor {
  const m = minutesAgo(iso);
  if (m === null) return 'subtle';
  const h = m / 60;
  if (h <= greenHours) return 'success';
  if (h <= amberHours) return 'warning';
  return 'danger';
}

function statusColor(status: string | null): BadgeColor {
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

function formatUtc(iso: string | null): string {
  if (!iso) return '-';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '-';
  return `${d.toISOString().slice(0, 19).replace('T', ' ')} UTC`;
}

const useStyles = makeStyles({
  cards: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    marginTop: '16px',
  },
  desc: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginBottom: '8px',
  },
  subHeading: {
    display: 'block',
    marginTop: '12px',
    marginBottom: '4px',
    fontWeight: tokens.fontWeightSemibold,
  },
  bigNumber: {
    fontSize: '40px',
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: '1',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginTop: '8px',
  },
});

export default function HealthPage() {
  const styles = useStyles();
  const [data, setData] = useState<HealthDashboard | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    fetchHealth()
      .then((d) => {
        if (!cancelled) setData(d);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load system health.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (loading) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={100} label="Loading system health..." />
      </div>
    );
  }

  if (error || !data) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error ?? 'No health data available.'}</MessageBarBody>
      </MessageBar>
    );
  }

  const maxHourCount = Math.max(1, ...data.exceptionsPerHour.map((h) => h.count));

  return (
    <div>
      <Title3 block>System Health - {data.buildLabel}</Title3>
      <Text className={styles.desc} style={{ marginTop: 8 }}>
        A single "is it working?" view. All values are read-only and best-effort - a data-source hiccup greys out one
        card, it never breaks the page. Data is cached for ~60s (loaded {formatUtc(data.loadedAtUtc)}). This complements
        the Azure Monitor alert rules (which push when something breaks) - it's the at-a-glance green board.
      </Text>

      {!data.appInsightsConfigured && (
        <div style={{ marginTop: 12 }}>
          <MessageBar intent="warning">
            <MessageBarBody>
              Application Insights is not configured for this web app, so the Import liveness, Exceptions and
              Component-health cards are unavailable. The Data overview card (from SQL) still works.
            </MessageBarBody>
          </MessageBar>
        </div>
      )}

      <div className={styles.cards}>
        {/* Import liveness */}
        <Card>
          <CardHeader header={<Subtitle2>Import liveness</Subtitle2>} />
          <Text className={styles.desc}>
            Is each importer still looping and finishing? A full activity import cycle should complete at least once
            every {CYCLE_SLA_HOURS} hours. "Last confirmed cycle" is the FinishedImportCycle event (emitted today); the
            per-section rows are the FinishedSectionImport events.
          </Text>

          {data.livenessError ? (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load import liveness: {data.livenessError}</MessageBarBody>
            </MessageBar>
          ) : data.appInsightsConfigured ? (
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
          ) : null}
        </Card>

        {/* Exceptions overview */}
        <Card>
          <CardHeader header={<Subtitle2>Exceptions overview (last 24h)</Subtitle2>} />
          <Text className={styles.desc}>
            A cheap catch-all: every web-job logs errors into Application Insights, so a rising count is an early warning
            of failures no specific check anticipates.
          </Text>

          {data.exceptionsError ? (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load exceptions: {data.exceptionsError}</MessageBarBody>
            </MessageBar>
          ) : data.appInsightsConfigured ? (
            <>
              <div>
                <span className={styles.bigNumber}>{data.exceptionsLast24h.toLocaleString()}</span>{' '}
                <Text>exceptions in the last 24 hours</Text>
              </div>

              {data.exceptionsPerHour.length > 0 && (
                <>
                  <Text className={styles.subHeading}>Per hour</Text>
                  <div
                    style={{
                      display: 'flex',
                      alignItems: 'flex-end',
                      height: '90px',
                      gap: '2px',
                      borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
                      marginBottom: '12px',
                    }}
                  >
                    {data.exceptionsPerHour.map((h, i) => {
                      const pct = Math.round((100 * h.count) / maxHourCount);
                      const label = `${h.hourUtc ? new Date(h.hourUtc).toISOString().slice(11, 16) : '?'} UTC: ${h.count}`;
                      return (
                        <div
                          key={h.hourUtc ?? i}
                          title={label}
                          style={{
                            flex: 1,
                            minWidth: '4px',
                            height: `${Math.max(pct, 2)}%`,
                            backgroundColor: h.count > 0 ? '#c50f1f' : '#e0e0e0',
                          }}
                        />
                      );
                    })}
                  </div>
                </>
              )}

              <Text className={styles.subHeading}>Top exception types</Text>
              {data.topExceptionTypes.length > 0 ? (
                <Table size="small" aria-label="Top exception types">
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>Type</TableHeaderCell>
                      <TableHeaderCell>Problem id</TableHeaderCell>
                      <TableHeaderCell>Count</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {data.topExceptionTypes.map((t, i) => (
                      <TableRow key={(t.type ?? '') + (t.problemId ?? '') + i}>
                        <TableCell>
                          <Text font="monospace">{t.type}</Text>
                        </TableCell>
                        <TableCell>
                          <Text size={200}>{t.problemId}</Text>
                        </TableCell>
                        <TableCell>{t.count.toLocaleString()}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : (
                <Text style={{ color: tokens.colorPaletteGreenForeground1 }}>No exceptions recorded in the last 24 hours.</Text>
              )}
            </>
          ) : null}
        </Card>

        {/* Component health */}
        <Card>
          <CardHeader header={<Subtitle2>Component health</Subtitle2>} />
          <Text className={styles.desc}>
            Latest runtime HealthCheck per component (SQL, Activity API, Graph, Key Vault, Redis, Service Bus, runtime
            credential, DNS), including the credential days-to-expiry warning.
          </Text>

          {data.componentHealthError ? (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load component health: {data.componentHealthError}</MessageBarBody>
            </MessageBar>
          ) : data.appInsightsConfigured ? (
            data.componentHealth.length > 0 ? (
              <Table size="small" aria-label="Component health">
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Component</TableHeaderCell>
                    <TableHeaderCell>Status</TableHeaderCell>
                    <TableHeaderCell>Detail</TableHeaderCell>
                    <TableHeaderCell>Days to expiry</TableHeaderCell>
                    <TableHeaderCell>Last checked</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.componentHealth.map((c, i) => (
                    <TableRow key={(c.component ?? '') + i}>
                      <TableCell>{c.component}</TableCell>
                      <TableCell>
                        <Badge appearance="filled" color={statusColor(c.status)}>
                          {c.status}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <Text size={200}>{c.detail}</Text>
                      </TableCell>
                      <TableCell>{c.daysToExpiry ?? ''}</TableCell>
                      <TableCell>
                        <Text size={200}>{howLongAgo(c.lastSeenUtc)}</Text>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            ) : (
              <MessageBar intent="info">
                <MessageBarBody>
                  No HealthCheck telemetry yet. Runtime health checks are emitted by a later phase (the independent-timer
                  heartbeat host). Until then this card is empty by design - use Import liveness and Exceptions above,
                  which are populated today.
                </MessageBarBody>
              </MessageBar>
            )
          ) : null}
        </Card>

        {/* Data overview */}
        <Card>
          <CardHeader header={<Subtitle2>Data overview</Subtitle2>} />
          <Text className={styles.desc}>Row counts and freshness straight from the database.</Text>

          {data.dataError ? (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load data overview: {data.dataError}</MessageBarBody>
            </MessageBar>
          ) : (
            <Table size="small" aria-label="Data overview">
              <TableBody>
                <TableRow>
                  <TableCell>Hits</TableCell>
                  <TableCell>{data.hitCount.toLocaleString()}</TableCell>
                </TableRow>
                <TableRow>
                  <TableCell>Activity imports (audit events)</TableCell>
                  <TableCell>{data.activityCount.toLocaleString()}</TableCell>
                </TableRow>
                <TableRow>
                  <TableCell>Teams discovered</TableCell>
                  <TableCell>{data.teamsCount.toLocaleString()}</TableCell>
                </TableRow>
                <TableRow>
                  <TableCell>Teams with tracking enabled</TableCell>
                  <TableCell>{data.teamsBeingTrackedCount.toLocaleString()}</TableCell>
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
                  <TableCell>Newest audit event</TableCell>
                  <TableCell>
                    {formatUtc(data.newestAuditEventUtc)}{' '}
                    <Badge appearance="filled" color={freshnessColor(data.newestAuditEventUtc, CYCLE_SLA_HOURS, CYCLE_SLA_HOURS * 2)}>
                      {howLongAgo(data.newestAuditEventUtc)}
                    </Badge>
                  </TableCell>
                </TableRow>
              </TableBody>
            </Table>
          )}
        </Card>
      </div>

      <Text size={200} className={styles.muted}>
        To be alerted (not just to look), set up the Azure Monitor / Application Insights alert rules in the Health
        Alerts wiki guide. The same custom events shown here back those alerts.
      </Text>
    </div>
  );
}
