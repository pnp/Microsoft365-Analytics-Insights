import { apiFetch } from './http';
import type { SystemStatus } from '../types/systemStatus';

const baseUrl = (): string =>
  window.o365AnalyticsSystemStatusAPI ?? `${window.location.origin}/api/SystemStatus`;

/** Fetch the system status (counts + configuration) shown on the Home page. */
export async function fetchSystemStatus(): Promise<SystemStatus> {
  const response = await apiFetch(baseUrl(), {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`Couldn't load system status (${response.status}).`);
  }

  return response.json() as Promise<SystemStatus>;
}

/**
 * Tests the Graph call-records webhook endpoint by POSTing the validation token, mirroring the
 * old home page's "test webhook" link. Resolves to the echoed token (should be "test").
 */
export async function testWebhook(webhookEndpointUrl: string): Promise<string> {
  const response = await fetch(`${webhookEndpointUrl}?validationToken=test`, {
    method: 'POST',
    credentials: 'same-origin',
  });
  return response.text();
}
