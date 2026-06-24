import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import toast, { Toaster } from 'react-hot-toast';
import { fetchSystemStatus, testWebhook } from '../api/systemStatusApi';
import type { SystemStatus } from '../types/systemStatus';
import Spinner from '../components/Spinner';

function WebhookSubscriptionStatus({ status }: { status: SystemStatus }) {
  switch (status.callWebhookState) {
    case 'Active':
      return (
        <span>
          <span style={{ color: 'green', fontWeight: 'bold' }}>Active</span>
          {status.callWebhookExpiry && (
            <> &ndash; renews automatically; current subscription expires {new Date(status.callWebhookExpiry).toUTCString()}</>
          )}
        </span>
      );
    case 'Missing':
      return (
        <div>
          <span style={{ color: '#b00', fontWeight: 'bold' }}>No active subscription found</span>
          <div>
            The importer web-job registers and renews this on every import cycle. If it stays missing, check the importer
            web-job is running and that its app registration has the <code>CallRecords.Read.All</code> Microsoft Graph
            application permission.
          </div>
        </div>
      );
    case 'Error':
      return (
        <div>
          <span style={{ color: '#b00', fontWeight: 'bold' }}>Couldn't check</span>
          <div>{status.callWebhookStatusDetail}</div>
        </div>
      );
    default:
      return <span>Not applicable - Teams calls import is disabled</span>;
  }
}

/**
 * Home page: the system status that used to be the server-rendered home page, now part of the SPA.
 * Data comes from api/SystemStatus.
 */
export default function HomePage() {
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
      <div className="text-center">
        <Spinner size={100} />
      </div>
    );
  }

  if (error || !status) {
    return <p className="aa-error">Error: {error ?? 'No status available.'}</p>;
  }

  return (
    <div>
      <Toaster />
      <h5 className="card-header text-center">
        Microsoft 365 Advanced Analytics Engine - {status.buildLabel} - Service Home
      </h5>
      <br />

      <h3>Tracking Data Overview</h3>
      <p>Here's a summary of the data in your database:</p>
      <table className="table" style={{ width: 500 }}>
        <tbody>
          <tr>
            <td>Hits</td>
            <td>{status.hitCount.toLocaleString()}</td>
          </tr>
          <tr>
            <td>Activity Imports</td>
            <td>{status.activityCount.toLocaleString()}</td>
          </tr>
          <tr>
            <td>Teams Discovered</td>
            <td>{status.teamsCount.toLocaleString()}</td>
          </tr>
          <tr>
            <td>Teams with Tracking Enabled</td>
            <td>{status.teamsBeingTrackedCount.toLocaleString()}</td>
          </tr>
        </tbody>
      </table>
      <p>
        Enable Teams analytics on the <Link to="/teams">Teams Permissions</Link> page.
      </p>
      <hr />

      <h3>System Configuration</h3>
      <p>These are the basics of your system configuration:</p>
      <table className="table">
        <tbody>
          <tr>
            <td className="header">Graph Call Webhook Endpoint</td>
            <td>
              {status.webhookEndpointUrl}
              {status.webhookEndpointUrl && (
                <>
                  {' - '}
                  <a
                    href={`${status.webhookEndpointUrl}?validationToken=test`}
                    onClick={(e) => {
                      e.preventDefault();
                      onTestWebhook(status.webhookEndpointUrl!);
                    }}
                  >
                    test webhook with validation POST
                  </a>
                </>
              )}
            </td>
          </tr>
          <tr>
            <td className="header">Teams Calls Import</td>
            <td>{status.callsImportEnabled ? <span>Enabled</span> : <span>Disabled - Teams call records are not being imported</span>}</td>
          </tr>
          <tr>
            <td className="header">Calls Webhook Subscription</td>
            <td>
              <WebhookSubscriptionStatus status={status} />
            </td>
          </tr>
          <tr>
            <td className="header">SQL Server</td>
            <td>{status.webAppConfigSQL}</td>
          </tr>
          <tr>
            <td>Redis SSL Endpoint</td>
            <td>{status.webAppConfigRedis}</td>
          </tr>
          <tr>
            <td>Cognitive Services Endpoint</td>
            <td>{status.webAppConfigCognitive}</td>
          </tr>
          <tr>
            <td>Cognitive Services Enabled</td>
            <td>
              {status.cognitiveServiceEnabled ? (
                <p>Yes - cognitive analytics will be available</p>
              ) : (
                <div>No - cognitive analytics are disabled</div>
              )}
            </td>
          </tr>
          <tr>
            <td>Service Bus</td>
            <td>{status.webAppConfigServiceBus}</td>
          </tr>
        </tbody>
      </table>

      <p>Last applied configuration JSon:</p>
      <code>{status.configJson}</code>
    </div>
  );
}
