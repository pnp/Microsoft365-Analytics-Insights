import { useEffect, useState } from 'react';
import toast from '../components/toast';
import {
  Badge,
  Card,
  CardHeader,
  Title3,
  Subtitle2,
  Text,
  Body1,
  Button,
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
import { fetchSystemStatus, testWebhook } from '../api/systemStatusApi';
import { fetchHealthConfig } from '../api/healthApi';
import { fetchUpdateCheck } from '../api/updateCheckApi';
import type { SystemStatus } from '../types/systemStatus';
import type { ConfigSection } from '../types/health';
import type { UpdateCheck } from '../types/updateCheck';
import { formatUtc } from '../components/health/healthShared';
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
  value: {
    // Endpoints and connection targets are long unbroken tokens; let them wrap instead of
    // overflowing the cell (Fluent's Table is table-layout: fixed).
    overflowWrap: 'anywhere',
  },
  chips: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '6px',
    marginTop: '4px',
  },
  updateRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
    marginTop: '8px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

function WebhookSubscriptionBadge({ status }: { status: SystemStatus }) {
  switch (status.callWebhookState) {
    case 'Active':
      return (
        <span>
          <Badge appearance="filled" color="success">
            Active
          </Badge>
          {status.callWebhookExpiry && (
            <Text size={200} style={{ marginLeft: 8 }}>
              renews automatically; expires {new Date(status.callWebhookExpiry).toUTCString()}
            </Text>
          )}
        </span>
      );
    case 'Missing':
      return (
        <div>
          <Badge appearance="filled" color="danger">
            No active subscription found
          </Badge>
          <Text size={200} block style={{ marginTop: 4 }}>
            The importer web-job registers and renews this on every import cycle. If it stays missing, check the importer
            web-job is running and that its app registration has the <code>CallRecords.Read.All</code> Microsoft Graph
            application permission.
          </Text>
        </div>
      );
    case 'Error':
      return (
        <div>
          <Badge appearance="filled" color="warning">
            Couldn't check
          </Badge>
          <Text size={200} block style={{ marginTop: 4 }}>
            {status.callWebhookStatusDetail}
          </Text>
        </div>
      );
    default:
      return <Text>Not applicable - Teams calls import is disabled</Text>;
  }
}

/**
 * The "check for updates" card. Deliberately on demand: nothing is fetched until the admin presses
 * the button, so a deployment that never opens this page never makes an outbound call to GitHub -
 * which matters because plenty of these deployments have no outbound internet at all.
 */
function UpdateCheckCard({ styles }: { styles: ReturnType<typeof useStyles> }) {
  const [checking, setChecking] = useState(false);
  const [result, setResult] = useState<UpdateCheck | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const onCheck = async () => {
    setChecking(true);
    setFailure(null);
    try {
      setResult(await fetchUpdateCheck());
    } catch (e) {
      setFailure(e instanceof Error ? e.message : 'Update check failed.');
    } finally {
      setChecking(false);
    }
  };

  return (
    <Card>
      <CardHeader header={<Subtitle2>Software updates</Subtitle2>} />
      <Body1>
        Compares the build this site is running against the latest published release on GitHub. Nothing is
        sent to GitHub until you press the button.
      </Body1>

      <div className={styles.updateRow}>
        <Button appearance="primary" onClick={onCheck} disabled={checking}>
          {checking ? 'Checking...' : 'Check for updates'}
        </Button>
        {result && (
          <Text size={200} className={styles.muted}>
            Checked {formatUtc(result.checkedAtUtc)}
          </Text>
        )}
      </div>

      {failure && (
        <MessageBar intent="error">
          <MessageBarBody>{failure}</MessageBarBody>
        </MessageBar>
      )}

      {result && (
        <>
          <Table aria-label="Update check" size="small">
            <TableBody>
              <TableRow>
                <TableCell className={styles.label}>This site is running</TableCell>
                <TableCell className={styles.value}>{result.currentBuildLabel ?? 'Unknown'}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Latest published release</TableCell>
                <TableCell className={styles.value}>
                  {result.latestReleaseName ?? (result.latestBuild != null ? `Build ${result.latestBuild}` : 'Unknown')}
                  {result.latestPublishedUtc && (
                    <Text size={200} block className={styles.muted}>
                      Published {formatUtc(result.latestPublishedUtc)}
                    </Text>
                  )}
                </TableCell>
              </TableRow>
            </TableBody>
          </Table>

          {result.updateAvailable ? (
            <MessageBar intent="warning">
              <MessageBarBody>
                <strong>An update is available.</strong> This site is on build {result.currentBuild}; build{' '}
                {result.latestBuild} has been released.{' '}
                {result.latestReleaseUrl && (
                  <Link href={result.latestReleaseUrl} target="_blank" rel="noreferrer">
                    Open the release notes and downloads
                  </Link>
                )}
                . Read the release notes before upgrading - they call out any database migrations and
                configuration changes.
              </MessageBarBody>
            </MessageBar>
          ) : result.checkError ? (
            <MessageBar intent="info">
              <MessageBarBody>
                {result.checkError}{' '}
                {result.latestReleaseUrl && (
                  <Link href={result.latestReleaseUrl} target="_blank" rel="noreferrer">
                    Open the latest release
                  </Link>
                )}
              </MessageBarBody>
            </MessageBar>
          ) : (
            <MessageBar intent="success">
              <MessageBarBody>
                This site is up to date - no newer release has been published.
                {result.latestReleaseUrl && (
                  <>
                    {' '}
                    <Link href={result.latestReleaseUrl} target="_blank" rel="noreferrer">
                      View the current release
                    </Link>
                  </>
                )}
              </MessageBarBody>
            </MessageBar>
          )}
        </>
      )}
    </Card>
  );
}

/**
 * Administration -> Service configuration: the single place that answers "what is this deployment
 * pointed at, what is turned on, and is the database up to date?".
 *
 * This used to be three overlapping views - the old Service Home "System Configuration" card, the
 * Health "Configuration" sub-tab and (partly) Component health - reading from two different APIs
 * with different labels and different redaction. They are merged here:
 *
 *  - api/SystemStatus supplies the resolved resource hosts (it extracts only the host/DataSource
 *    from each connection string, never the connection string itself) and the webhook endpoint
 *    needed for the live test action.
 *  - api/Health/config supplies what api/SystemStatus doesn't: which imports are enabled and
 *    whether the database schema matches this build.
 */
export default function ServiceConfigurationPage() {
  const styles = useStyles();
  const [status, setStatus] = useState<SystemStatus | null>(null);
  const [health, setHealth] = useState<ConfigSection | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    fetchSystemStatus()
      .then((s) => {
        if (!cancelled) setStatus(s);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load the service configuration.');
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
    // Best-effort: the imports/schema card is hidden if this fails, rather than failing the page.
    fetchHealthConfig()
      .then((c) => {
        if (!cancelled) setHealth(c);
      })
      .catch(() => {
        /* ignored on purpose - see the note above */
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const onTestWebhook = async (url: string) => {
    try {
      const token = await testWebhook(url);
      if (token === 'test') {
        toast.success(`Success. Got back test-token "${token}"`);
      } else {
        toast.error(`Unexpected response. Got back response body "${token}"`);
      }
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Webhook test failed.');
    }
  };

  if (loading) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={100} label="Loading service configuration..." />
      </div>
    );
  }

  if (error || !status) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error ?? 'No configuration available.'}</MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <Title3 block>Service configuration{status.buildLabel ? ` - ${status.buildLabel}` : ''}</Title3>

      <div className={styles.cards}>
        <UpdateCheckCard styles={styles} />

        <Card>
          <CardHeader header={<Subtitle2>Azure resources</Subtitle2>} />
          <Body1>These are the resources this deployment is configured to use:</Body1>
          <Table aria-label="Azure resources" size="small">
            <TableBody>
              <TableRow>
                <TableCell className={styles.label}>SQL Server</TableCell>
                <TableCell className={styles.value}>{status.webAppConfigSQL}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Redis SSL Endpoint</TableCell>
                <TableCell className={styles.value}>{status.webAppConfigRedis}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Cognitive Services Endpoint</TableCell>
                <TableCell className={styles.value}>{status.webAppConfigCognitive}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Cognitive Services Enabled</TableCell>
                <TableCell className={styles.value}>
                  {status.cognitiveServiceEnabled ? (
                    <Text>Yes - cognitive analytics will be available</Text>
                  ) : (
                    <Text>No - cognitive analytics are disabled</Text>
                  )}
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Service Bus</TableCell>
                <TableCell className={styles.value}>{status.webAppConfigServiceBus}</TableCell>
              </TableRow>
              {health?.webAppUrl && (
                <TableRow>
                  <TableCell className={styles.label}>Web app URL</TableCell>
                  <TableCell className={styles.value}>{health.webAppUrl}</TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </Card>

        {health && (
          <Card>
            <CardHeader header={<Subtitle2>Imports and schema</Subtitle2>} />
            {health.configError && (
              <MessageBar intent="warning">
                <MessageBarBody>Couldn't load configuration: {health.configError}</MessageBarBody>
              </MessageBar>
            )}

            <Body1>
              Which import workloads are turned on - so an empty report reads as "feature off", not "broken".
            </Body1>
            {health.enabledImports.length > 0 ? (
              <div className={styles.chips}>
                {health.enabledImports.map((f) => (
                  <Badge key={f} appearance="tint" color="brand">
                    {f}
                  </Badge>
                ))}
              </div>
            ) : (
              <Text size={200}>None enabled in this app's config.</Text>
            )}

            <Table aria-label="Schema state" size="small">
              <TableBody>
                <TableRow>
                  <TableCell className={styles.label}>Schema / migration version</TableCell>
                  <TableCell className={styles.value}>
                    {health.schemaError ? (
                      <Text size={200}>Couldn't check: {health.schemaError}</Text>
                    ) : health.schemaUpToDate === true ? (
                      <Badge appearance="filled" color="success">
                        Up to date with this build
                      </Badge>
                    ) : health.schemaUpToDate === false ? (
                      <div>
                        <Badge appearance="filled" color="danger">
                          {health.pendingMigrations.length} migration(s) pending
                        </Badge>{' '}
                        <Text size={200}>
                          The database is behind this build - run the upgrader. ({health.pendingMigrations.join(', ')})
                        </Text>
                      </div>
                    ) : (
                      <Text size={200}>Unknown.</Text>
                    )}
                  </TableCell>
                </TableRow>
              </TableBody>
            </Table>
          </Card>
        )}

        <Card>
          <CardHeader header={<Subtitle2>Teams calls</Subtitle2>} />
          <Table aria-label="Teams calls configuration" size="small">
            <TableBody>
              <TableRow>
                <TableCell className={styles.label}>Teams Calls Import</TableCell>
                <TableCell className={styles.value}>
                  {status.callsImportEnabled ? (
                    <Badge appearance="tint" color="success">
                      Enabled
                    </Badge>
                  ) : (
                    <Badge appearance="tint" color="informative">
                      Disabled - Teams call records are not being imported
                    </Badge>
                  )}
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Graph Call Webhook Endpoint</TableCell>
                <TableCell className={styles.value}>
                  <Text>{status.webhookEndpointUrl}</Text>
                  {status.webhookEndpointUrl && (
                    <Button
                      appearance="transparent"
                      size="small"
                      onClick={() => onTestWebhook(status.webhookEndpointUrl!)}
                    >
                      test webhook with validation POST
                    </Button>
                  )}
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Calls Webhook Subscription</TableCell>
                <TableCell className={styles.value}>
                  <WebhookSubscriptionBadge status={status} />
                  {health?.webhookExpiryUtc && (
                    <Text size={200} block>
                      Health check last saw it expiring {formatUtc(health.webhookExpiryUtc)}.
                    </Text>
                  )}
                </TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </Card>
      </div>
    </div>
  );
}
