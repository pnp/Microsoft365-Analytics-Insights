import { useEffect, useState } from 'react';
import toast, { Toaster } from 'react-hot-toast';
import {
  Card,
  CardHeader,
  Title3,
  Subtitle2,
  Text,
  Body1,
  Badge,
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
import type { SystemStatus } from '../types/systemStatus';
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
 * Home page: the system status that used to be the server-rendered home page, now part of the SPA.
 * Data comes from api/SystemStatus.
 */
export default function HomePage() {
  const styles = useStyles();
  const [status, setStatus] = useState<SystemStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    fetchSystemStatus()
      .then((s) => {
        if (!cancelled) setStatus(s);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load system status.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
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
        <Spinner size={100} label="Loading system status..." />
      </div>
    );
  }

  if (error || !status) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error ?? 'No status available.'}</MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <Toaster />
      <Title3 block>Service Home - {status.buildLabel}</Title3>

      <div className={styles.cards}>
        <Card>
          <CardHeader header={<Subtitle2>Tracking Data Overview</Subtitle2>} />
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
          <Body1>
            Enable Teams analytics on the <Link href="#/teams">Teams Permissions</Link> page.
          </Body1>
        </Card>

        <Card>
          <CardHeader header={<Subtitle2>System Configuration</Subtitle2>} />
          <Body1>These are the basics of your system configuration:</Body1>
          <Table aria-label="System configuration" size="small">
            <TableBody>
              <TableRow>
                <TableCell className={styles.label}>Graph Call Webhook Endpoint</TableCell>
                <TableCell>
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
                <TableCell className={styles.label}>Teams Calls Import</TableCell>
                <TableCell>
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
                <TableCell className={styles.label}>Calls Webhook Subscription</TableCell>
                <TableCell>
                  <WebhookSubscriptionBadge status={status} />
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>SQL Server</TableCell>
                <TableCell>{status.webAppConfigSQL}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Redis SSL Endpoint</TableCell>
                <TableCell>{status.webAppConfigRedis}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Cognitive Services Endpoint</TableCell>
                <TableCell>{status.webAppConfigCognitive}</TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Cognitive Services Enabled</TableCell>
                <TableCell>
                  {status.cognitiveServiceEnabled ? (
                    <Text>Yes - cognitive analytics will be available</Text>
                  ) : (
                    <Text>No - cognitive analytics are disabled</Text>
                  )}
                </TableCell>
              </TableRow>
              <TableRow>
                <TableCell className={styles.label}>Service Bus</TableCell>
                <TableCell>{status.webAppConfigServiceBus}</TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </Card>
      </div>
    </div>
  );
}
