import { useEffect, useState } from 'react';
import {
  Card,
  CardHeader,
  Title3,
  Subtitle2,
  Text,
  Body1,
  Link,
  Table,
  TableBody,
  TableRow,
  TableCell,
  MessageBar,
  MessageBarBody,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { fetchSystemStatus } from '../api/systemStatusApi';
import { fetchHealthData } from '../api/healthApi';
import type { SystemStatus } from '../types/systemStatus';
import { formatUtc, howLongAgo } from '../components/health/healthShared';
import Spinner from '../components/Spinner';

const useStyles = makeStyles({
  cards: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    marginTop: '16px',
  },
  label: {
    fontWeight: tokens.fontWeightSemibold,
    width: '260px',
    verticalAlign: 'top',
  },
  freshness: {
    color: tokens.colorNeutralForeground3,
  },
});

/**
 * Insights landing page: how much data the solution holds and how fresh it is.
 *
 * Deliberately light. It answers "is the data flowing, and how much is there" for a business
 * reader - anything an operator needs to act on lives in Administration -> Service health.
 *
 * The counts come from api/SystemStatus, which is cheap. Freshness comes from the Health data
 * section, which is the only *heavy* health endpoint (it scans the fact tables), so it is
 * fetched separately and never blocks the first paint: the counts render immediately and the
 * freshness line fills in when it arrives, or stays hidden if it fails or times out. That
 * matters because this is the first screen everyone sees, on tenants with very large fact
 * tables. The endpoint is 60s-cached and single-flight server-side, so repeat visits are free.
 */
export default function InsightsOverviewPage() {
  const styles = useStyles();
  const [status, setStatus] = useState<SystemStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [freshness, setFreshness] = useState<{ audit: string | null; hit: string | null } | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetchSystemStatus()
      .then((s) => {
        if (!cancelled) setStatus(s);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load the data overview.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    // Best-effort only - a slow or failing freshness scan must not degrade the landing page.
    fetchHealthData()
      .then((d) => {
        if (!cancelled) setFreshness({ audit: d.newestAuditEventUtc, hit: d.newestHitUtc });
      })
      .catch(() => {
        /* ignored on purpose - see the note above */
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (loading) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={100} label="Loading data overview..." />
      </div>
    );
  }

  if (error || !status) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error ?? 'No data overview available.'}</MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <Title3 block>Overview{status.buildLabel ? ` - ${status.buildLabel}` : ''}</Title3>

      <div className={styles.cards}>
        <Card>
          <CardHeader header={<Subtitle2>Tracking data</Subtitle2>} />
          <Body1>Here's a summary of the data in your database:</Body1>
          <Table aria-label="Tracking data overview" size="small">
            <TableBody>
              {status.dataCounts.map((c) => (
                <TableRow key={c.name}>
                  <TableCell className={styles.label}>{c.name}</TableCell>
                  <TableCell>{c.count.toLocaleString()}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>

          {freshness && (freshness.audit || freshness.hit) && (
            <Text size={200} block className={styles.freshness}>
              {freshness.audit && `Newest audit event: ${formatUtc(freshness.audit)} (${howLongAgo(freshness.audit)}).`}
              {freshness.audit && freshness.hit ? ' ' : ''}
              {freshness.hit && `Newest web hit: ${formatUtc(freshness.hit)} (${howLongAgo(freshness.hit)}).`}
            </Text>
          )}
        </Card>

        <Card>
          <CardHeader header={<Subtitle2>Where to next</Subtitle2>} />
          <Body1>
            Chart this data over time in <Link href="#/insights/reports">Reports</Link>, or see who is getting
            value from their Copilot licence in <Link href="#/insights/copilot-adoption">Copilot Adoption</Link>.
          </Body1>
          <Body1>
            Teams analytics are enabled per team in{' '}
            <Link href="#/admin/teams-permissions">Administration &rarr; Teams permissions</Link>. To check
            whether imports are running, see{' '}
            <Link href="#/admin/health">Administration &rarr; Service health</Link>.
          </Body1>
        </Card>
      </div>
    </div>
  );
}
