import { useCallback, useEffect, useState } from 'react';
import {
  Button,
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
import type { HealthDashboard, HourCount } from '../types/health';
import Spinner from '../components/Spinner';

type BadgeColor = 'success' | 'warning' | 'danger' | 'informative' | 'subtle';

// A full activity import cycle should complete at least this often (see HEALTH-MONITORING-DESIGN.md).
const CYCLE_SLA_HOURS = 24;
const AUTO_REFRESH_MS = 60_000;

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

function overallColor(status: string | null): BadgeColor {
  switch ((status ?? '').toLowerCase()) {
    case 'healthy':
      return 'success';
    case 'degraded':
      return 'warning';
    case 'unhealthy':
      return 'danger';
    default:
      return 'informative';
  }
}

function formatUtc(iso: string | null): string {
  if (!iso) return '-';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '-';
  return `${d.toISOString().slice(0, 19).replace('T', ' ')} UTC`;
}

function formatSize(mb: number): string {
  if (!mb || mb <= 0) return '-';
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${mb.toLocaleString()} MB`;
}

// KQL summarize-by-bin omits empty hours, so pad to a full 24-bar series for a readable sparkline.
function buildHourBuckets(perHour: HourCount[]): { hourUtc: string; count: number }[] {
  const byHour = new Map<number, number>();
  for (const h of perHour) {
    if (!h.hourUtc) continue;
    const t = new Date(h.hourUtc);
    if (Number.isNaN(t.getTime())) continue;
    t.setUTCMinutes(0, 0, 0);
    byHour.set(t.getTime(), (byHour.get(t.getTime()) ?? 0) + h.count);
  }
  const now = new Date();
  now.setUTCMinutes(0, 0, 0);
  const buckets: { hourUtc: string; count: number }[] = [];
  for (let i = 23; i >= 0; i--) {
    const d = new Date(now.getTime() - i * 3_600_000);
    buckets.push({ hourUtc: d.toISOString(), count: byHour.get(d.getTime()) ?? 0 });
  }
  return buckets;
}

const useStyles = makeStyles({
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
  },
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
  reasons: {
    marginTop: '8px',
    marginBottom: 0,
    paddingLeft: '20px',
  },
  chips: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '6px',
    marginTop: '4px',
  },
});

export default function HealthPage() {
  const styles = useStyles();
  const [data, setData] = useState<HealthDashboard | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  const load = useCallback(async () => {
    setRefreshing(true);
    try {
      const d = await fetchHealth();
      setData(d);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load system health.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      if (cancelled) return;
      await load();
    };
    tick();
    // Auto-refresh so this can be left open as a "green board". The API is cached ~60s server-side.
    const id = window.setInterval(tick, AUTO_REFRESH_MS);
    return () => {
      cancelled = true;
      window.clearInterval(id);
    };
  }, [load]);

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

  const hourBuckets = buildHourBuckets(data.exceptionsPerHour);
  const maxHourCount = Math.max(1, ...hourBuckets.map((h) => h.count));

  return (
    <div>
      <div className={styles.headerRow}>
        <Title3>System Health - {data.buildLabel}</Title3>
        <Badge appearance="filled" size="large" color={overallColor(data.overallStatus)}>
          {data.overallStatus}
        </Badge>
        <Button size="small" appearance="secondary" disabled={refreshing} onClick={() => void load()}>
          {refreshing ? 'Refreshing...' : 'Refresh'}
        </Button>
      </div>

      <Text className={styles.desc} style={{ marginTop: 8 }}>
        A single "is it working?" view. All values are read-only and best-effort - a data-source hiccup greys out one
        card, it never breaks the page. Auto-refreshes every 60s (cached server-side; loaded {formatUtc(data.loadedAtUtc)}).
        This complements the Azure Monitor alert rules (which push when something breaks) - it's the at-a-glance green board.
      </Text>

      {data.overallReasons.length > 0 && (
        <ul className={styles.reasons}>
          {data.overallReasons.map((r, i) => (
            <li key={i}>
              <Text size={200}>{r}</Text>
            </li>
          ))}
        </ul>
      )}

      {!data.appInsightsConfigured && (
        <div style={{ marginTop: 12 }}>
          <MessageBar intent="warning">
            <MessageBarBody>
              Application Insights is not configured for this web app, so the Import liveness, Exceptions and
              Component-health (App Insights) cards are unavailable. The Data overview, Configuration and runtime
              credential / Service Bus checks still work.
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

              {data.sqlCapacityExceptions24h > 0 && (
                <div style={{ marginTop: 8 }}>
                  <MessageBar intent="error">
                    <MessageBarBody>
                      {data.sqlCapacityExceptions24h.toLocaleString()} of these look like SQL capacity / read-only
                      failures - check the database storage / edition. This usually means data has stopped being written.
                    </MessageBarBody>
                  </MessageBar>
                </div>
              )}

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
                {hourBuckets.map((h, i) => {
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
            Latest health per component. The runtime credential (expiry) and Service Bus (Teams calls queue) checks run
            here today; SQL, Activity API, Graph, Key Vault, Redis and DNS fill in as the runtime HealthCheck emitter
            (a later phase) lands.
          </Text>

          {data.componentHealthError ? (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load component health: {data.componentHealthError}</MessageBarBody>
            </MessageBar>
          ) : data.componentHealth.length > 0 ? (
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
              <MessageBarBody>No component health available yet.</MessageBarBody>
            </MessageBar>
          )}
        </Card>

        {/* Data overview */}
        <Card>
          <CardHeader header={<Subtitle2>Data overview</Subtitle2>} />
          <Text className={styles.desc}>
            Volume and freshness from the database. Row counts are approximate (read from index metadata, so a large
            tenant isn't hit with a COUNT(*) on every load); "last 24h / 7d" show what's actually flowing in.
          </Text>

          {data.dataError ? (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load data overview: {data.dataError}</MessageBarBody>
            </MessageBar>
          ) : (
            <>
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
                    <TableCell>{data.auditEventsLast24h.toLocaleString()}</TableCell>
                    <TableCell>{data.auditEventsLast7d.toLocaleString()}</TableCell>
                  </TableRow>
                  <TableRow>
                    <TableCell>Web hits</TableCell>
                    <TableCell>{data.hitCount.toLocaleString()}</TableCell>
                    <TableCell>{data.hitsLast24h.toLocaleString()}</TableCell>
                    <TableCell>{data.hitsLast7d.toLocaleString()}</TableCell>
                  </TableRow>
                  <TableRow>
                    <TableCell>Copilot interactions</TableCell>
                    <TableCell>{data.copilotChatCount.toLocaleString()}</TableCell>
                    <TableCell colSpan={2}>
                      <Text size={200} className={styles.muted} style={{ marginTop: 0 }}>
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
        </Card>

        {/* Configuration */}
        <Card>
          <CardHeader header={<Subtitle2>Configuration</Subtitle2>} />
          <Text className={styles.desc}>
            What's turned on and what this app points at - so an empty card above reads as "feature off", not "broken".
          </Text>

          {data.configError && (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load configuration: {data.configError}</MessageBarBody>
            </MessageBar>
          )}

          <Text className={styles.subHeading}>Enabled imports</Text>
          {data.enabledImports.length > 0 ? (
            <div className={styles.chips}>
              {data.enabledImports.map((f) => (
                <Badge key={f} appearance="tint" color="brand">
                  {f}
                </Badge>
              ))}
            </div>
          ) : (
            <Text size={200}>None enabled in this app's config.</Text>
          )}

          <Text className={styles.subHeading}>Schema / migration version</Text>
          {data.schemaError ? (
            <Text size={200}>Couldn't check: {data.schemaError}</Text>
          ) : data.schemaUpToDate === true ? (
            <Badge appearance="filled" color="success">
              Up to date with this build
            </Badge>
          ) : data.schemaUpToDate === false ? (
            <div>
              <Badge appearance="filled" color="danger">
                {data.pendingMigrations.length} migration(s) pending
              </Badge>{' '}
              <Text size={200}>The database is behind this build - run the upgrader. ({data.pendingMigrations.join(', ')})</Text>
            </div>
          ) : (
            <Text size={200}>Unknown.</Text>
          )}

          <Text className={styles.subHeading}>Teams call-records webhook</Text>
          {data.callsImportEnabled ? (
            <div>
              <Badge
                appearance="filled"
                color={
                  data.webhookState === 'Active'
                    ? 'success'
                    : data.webhookState === 'Missing'
                      ? 'warning'
                      : data.webhookState === 'Error'
                        ? 'danger'
                        : 'subtle'
                }
              >
                {data.webhookState}
              </Badge>{' '}
              {data.webhookExpiryUtc && <Text size={200}>expires {formatUtc(data.webhookExpiryUtc)}</Text>}
              {data.webhookDetail && (
                <Text size={200} className={styles.muted}>
                  {data.webhookDetail}
                </Text>
              )}
            </div>
          ) : (
            <Text size={200}>Teams calls import is off.</Text>
          )}

          <Text className={styles.subHeading}>Resources</Text>
          <Table size="small" aria-label="Resources">
            <TableBody>
              <TableRow>
                <TableCell>SQL server</TableCell>
                <TableCell>
                  <Text font="monospace">{data.sqlServer}</Text>
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Redis</TableCell>
                <TableCell>{data.redisHost}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Service Bus</TableCell>
                <TableCell>{data.serviceBusEndpoint}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Cognitive / Language</TableCell>
                <TableCell>
                  <Text font="monospace">{data.cognitiveEndpoint}</Text>
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell>Web app URL</TableCell>
                <TableCell>
                  <Text font="monospace">{data.webAppUrl}</Text>
                </TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </Card>
      </div>

      <Text size={200} className={styles.muted}>
        To be alerted (not just to look), set up the Azure Monitor / Application Insights alert rules in the Health
        Alerts wiki guide. The same custom events shown here back those alerts.
      </Text>
    </div>
  );
}
