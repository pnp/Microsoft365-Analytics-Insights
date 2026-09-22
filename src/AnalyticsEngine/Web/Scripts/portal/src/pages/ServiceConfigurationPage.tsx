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
import { useT, useTNode } from '../i18n';

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
  const t = useT();
  const tNode = useTNode();
  switch (status.callWebhookState) {
    case 'Active':
      return (
        <span>
          <Badge appearance="filled" color="success">
            {t('admin.serviceConfiguration.webhook.active')}
          </Badge>
          {status.callWebhookExpiry && (
            <Text size={200} style={{ marginLeft: 8 }}>
              {t('admin.serviceConfiguration.webhook.renewsAutomatically', {
                expiry: new Date(status.callWebhookExpiry).toUTCString(),
              })}
            </Text>
          )}
        </span>
      );
    case 'Missing':
      return (
        <div>
          <Badge appearance="filled" color="danger">
            {t('admin.serviceConfiguration.webhook.noActiveSubscription')}
          </Badge>
          <Text size={200} block style={{ marginTop: 4 }}>
            {tNode('admin.serviceConfiguration.webhook.missingHelp', {
              permission: <code>{t('admin.serviceConfiguration.webhook.callRecordsPermission')}</code>,
            })}
          </Text>
        </div>
      );
    case 'Error':
      return (
        <div>
          <Badge appearance="filled" color="warning">
            {t('admin.serviceConfiguration.webhook.couldNotCheck')}
          </Badge>
          <Text size={200} block style={{ marginTop: 4 }}>
            {status.callWebhookStatusDetail}
          </Text>
        </div>
      );
    default:
      return <Text>{t('admin.serviceConfiguration.webhook.notApplicable')}</Text>;
  }
}

/**
 * The "check for updates" card. Deliberately on demand: nothing is fetched until the admin presses
 * the button, so a deployment that never opens this page never makes an outbound call to GitHub -
 * which matters because plenty of these deployments have no outbound internet at all.
 */
function UpdateCheckCard({ styles }: { styles: ReturnType<typeof useStyles> }) {
  const t = useT();
  const tNode = useTNode();
  const [checking, setChecking] = useState(false);
  const [result, setResult] = useState<UpdateCheck | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const onCheck = async () => {
    setChecking(true);
    setFailure(null);
    try {
      setResult(await fetchUpdateCheck());
    } catch (e) {
      setFailure(e instanceof Error ? e.message : t('admin.serviceConfiguration.updates.checkFailed'));
    } finally {
      setChecking(false);
    }
  };

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('admin.serviceConfiguration.updates.title')}</Subtitle2>} />
      <Body1>
        {t('admin.serviceConfiguration.updates.description')}
      </Body1>

      <div className={styles.updateRow}>
        <Button appearance="primary" onClick={onCheck} disabled={checking}>
          {checking
            ? t('admin.serviceConfiguration.updates.checking')
            : t('admin.serviceConfiguration.updates.checkForUpdates')}
        </Button>
        {result && (
          <Text size={200} className={styles.muted}>
            {t('admin.serviceConfiguration.updates.checked', { checkedAt: formatUtc(result.checkedAtUtc) })}
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
          <Table aria-label={t('admin.serviceConfiguration.updates.ariaLabel')} size="small">
            <TableBody>
              <TableRow>
                <TableCell className={styles.label}>{t('admin.serviceConfiguration.updates.currentBuildLabel')}</TableCell>
                <TableCell className={styles.value}>{result.currentBuildLabel ?? t('admin.common.unknown')}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>{t('admin.serviceConfiguration.updates.latestReleaseLabel')}</TableCell>
                <TableCell className={styles.value}>
                  {result.latestReleaseName ??
                    (result.latestBuild != null
                      ? t('admin.serviceConfiguration.updates.build', { build: result.latestBuild })
                      : t('admin.common.unknown'))}
                  {result.latestPublishedUtc && (
                    <Text size={200} block className={styles.muted}>
                      {t('admin.serviceConfiguration.updates.published', {
                        publishedAt: formatUtc(result.latestPublishedUtc),
                      })}
                    </Text>
                  )}
                </TableCell>
              </TableRow>
            </TableBody>
          </Table>

          {result.updateAvailable ? (
            <MessageBar intent="warning">
              <MessageBarBody>
                {tNode(
                  result.latestReleaseUrl
                    ? 'admin.serviceConfiguration.updates.updateAvailableWithLink'
                    : 'admin.serviceConfiguration.updates.updateAvailableNoLink',
                  {
                    lead: <strong>{t('admin.serviceConfiguration.updates.updateAvailableLead')}</strong>,
                    currentBuild: result.currentBuild,
                    latestBuild: result.latestBuild,
                    releaseLink: result.latestReleaseUrl ? (
                      <Link href={result.latestReleaseUrl} target="_blank" rel="noreferrer">
                        {t('admin.serviceConfiguration.updates.openReleaseNotes')}
                      </Link>
                    ) : null,
                  },
                )}
              </MessageBarBody>
            </MessageBar>
          ) : result.checkError ? (
            <MessageBar intent="info">
              <MessageBarBody>
                {result.checkError}{' '}
                {result.latestReleaseUrl && (
                  <Link href={result.latestReleaseUrl} target="_blank" rel="noreferrer">
                    {t('admin.serviceConfiguration.updates.openLatestRelease')}
                  </Link>
                )}
              </MessageBarBody>
            </MessageBar>
          ) : (
            <MessageBar intent="success">
              <MessageBarBody>
                {t('admin.serviceConfiguration.updates.upToDate')}
                {result.latestReleaseUrl && (
                  <>
                    {' '}
                    <Link href={result.latestReleaseUrl} target="_blank" rel="noreferrer">
                      {t('admin.serviceConfiguration.updates.viewCurrentRelease')}
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
  const t = useT();
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
      .catch((e: any) => {
        if (!cancelled) setError(e instanceof Error ? e.message : t('admin.serviceConfiguration.loadFailed'));
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
        toast.success(t('admin.serviceConfiguration.webhook.testSuccess', { token }));
      } else {
        toast.error(t('admin.serviceConfiguration.webhook.testUnexpectedResponse', { token }));
      }
    } catch (e) {
      toast.error(e instanceof Error ? e.message : t('admin.serviceConfiguration.webhook.testFailed'));
    }
  };

  if (loading) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={100} label={t('admin.serviceConfiguration.loading')} />
      </div>
    );
  }

  if (error || !status) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error ?? t('admin.serviceConfiguration.noConfiguration')}</MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <Title3 block>
        {status.buildLabel
          ? t('admin.serviceConfiguration.titleWithBuild', { buildLabel: status.buildLabel })
          : t('admin.serviceConfiguration.title')}
      </Title3>

      <div className={styles.cards}>
        <UpdateCheckCard styles={styles} />

        <Card>
          <CardHeader header={<Subtitle2>{t('admin.serviceConfiguration.azureResources.title')}</Subtitle2>} />
          <Body1>{t('admin.serviceConfiguration.azureResources.description')}</Body1>
          <Table aria-label={t('admin.serviceConfiguration.azureResources.ariaLabel')} size="small">
            <TableBody>
              <TableRow>
                <TableCell className={styles.label}>SQL Server</TableCell>
                <TableCell className={styles.value}>{status.webAppConfigSQL}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>{t('admin.serviceConfiguration.azureResources.redisSslEndpoint')}</TableCell>
                <TableCell className={styles.value}>{status.webAppConfigRedis}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>{t('admin.serviceConfiguration.azureResources.cognitiveServicesEndpoint')}</TableCell>
                <TableCell className={styles.value}>{status.webAppConfigCognitive}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>{t('admin.serviceConfiguration.azureResources.cognitiveServicesEnabled')}</TableCell>
                <TableCell className={styles.value}>
                  {status.cognitiveServiceEnabled ? (
                    <Text>{t('admin.serviceConfiguration.azureResources.cognitiveAnalyticsAvailable')}</Text>
                  ) : (
                    <Text>{t('admin.serviceConfiguration.azureResources.cognitiveAnalyticsDisabled')}</Text>
                  )}
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Service Bus</TableCell>
                <TableCell className={styles.value}>{status.webAppConfigServiceBus}</TableCell>
              </TableRow>
              {health?.webAppUrl && (
                <TableRow>
                  <TableCell className={styles.label}>{t('admin.serviceConfiguration.azureResources.webAppUrl')}</TableCell>
                  <TableCell className={styles.value}>{health.webAppUrl}</TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </Card>

        {health && (
          <Card>
            <CardHeader header={<Subtitle2>{t('admin.serviceConfiguration.importsAndSchema.title')}</Subtitle2>} />
            {health.configError && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  {t('admin.serviceConfiguration.importsAndSchema.configLoadFailed', { error: health.configError })}
                </MessageBarBody>
              </MessageBar>
            )}

            <Body1>
              {t('admin.serviceConfiguration.importsAndSchema.description')}
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
              <Text size={200}>{t('admin.serviceConfiguration.importsAndSchema.noneEnabled')}</Text>
            )}

            <Table aria-label={t('admin.serviceConfiguration.importsAndSchema.schemaStateAriaLabel')} size="small">
              <TableBody>
                <TableRow>
                  <TableCell className={styles.label}>{t('admin.serviceConfiguration.importsAndSchema.schemaVersion')}</TableCell>
                  <TableCell className={styles.value}>
                    {health.schemaError ? (
                      <Text size={200}>
                        {t('admin.serviceConfiguration.importsAndSchema.schemaCheckFailed', {
                          error: health.schemaError,
                        })}
                      </Text>
                    ) : health.schemaUpToDate === true ? (
                      <Badge appearance="filled" color="success">
                        {t('admin.serviceConfiguration.importsAndSchema.upToDate')}
                      </Badge>
                    ) : health.schemaUpToDate === false ? (
                      <div>
                        <Badge appearance="filled" color="danger">
                          {t('admin.serviceConfiguration.importsAndSchema.pendingMigrations', {
                            count: health.pendingMigrations.length,
                          })}
                        </Badge>{' '}
                        <Text size={200}>
                          {t('admin.serviceConfiguration.importsAndSchema.databaseBehind', {
                            migrations: health.pendingMigrations.join(', '),
                          })}
                        </Text>
                      </div>
                    ) : (
                      <Text size={200}>{t('admin.common.unknownWithPeriod')}</Text>
                    )}
                  </TableCell>
                </TableRow>
              </TableBody>
            </Table>
          </Card>
        )}

        <Card>
          <CardHeader header={<Subtitle2>{t('admin.serviceConfiguration.teamsCalls.title')}</Subtitle2>} />
          <Table aria-label={t('admin.serviceConfiguration.teamsCalls.ariaLabel')} size="small">
            <TableBody>
              <TableRow>
                <TableCell className={styles.label}>{t('admin.serviceConfiguration.teamsCalls.importLabel')}</TableCell>
                <TableCell className={styles.value}>
                  {status.callsImportEnabled ? (
                    <Badge appearance="tint" color="success">
                      {t('admin.common.enabled')}
                    </Badge>
                  ) : (
                    <Badge appearance="tint" color="informative">
                      {t('admin.serviceConfiguration.teamsCalls.disabled')}
                    </Badge>
                  )}
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>{t('admin.serviceConfiguration.teamsCalls.webhookEndpoint')}</TableCell>
                <TableCell className={styles.value}>
                  <Text>{status.webhookEndpointUrl}</Text>
                  {status.webhookEndpointUrl && (
                    <Button
                      appearance="transparent"
                      size="small"
                      onClick={() => onTestWebhook(status.webhookEndpointUrl!)}
                    >
                      {t('admin.serviceConfiguration.teamsCalls.testWebhook')}
                    </Button>
                  )}
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>{t('admin.serviceConfiguration.teamsCalls.webhookSubscription')}</TableCell>
                <TableCell className={styles.value}>
                  <WebhookSubscriptionBadge status={status} />
                  {health?.webhookExpiryUtc && (
                    <Text size={200} block>
                      {t('admin.serviceConfiguration.teamsCalls.healthCheckExpiry', {
                        expiry: formatUtc(health.webhookExpiryUtc),
                      })}
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
