import { useCallback, useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Button, Card, MessageBar, MessageBarBody, Text, Title3 } from '@fluentui/react-components';
import {
  beginAgentCostConnection, disconnectAgentCostConnection, fetchAgentCostConnection,
  type AgentCostConnectionStatus,
} from '../api/agentCostConnectionApi';
import { useT, type TranslationKey } from '../i18n';

const outcomes = new Map<string, TranslationKey>([
  ['connected', 'admin.agentCostConnection.connectedOutcome'],
  ['storageNotConfigured', 'admin.agentCostConnection.storageNotConfigured'],
  ['consentOrPolicy', 'admin.agentCostConnection.consentOrPolicy'],
  ['identityMismatch', 'admin.agentCostConnection.identityMismatch'],
  ['accessDenied', 'admin.agentCostConnection.accessDenied'],
  ['failed', 'admin.agentCostConnection.failed'],
]);

const states: Record<AgentCostConnectionStatus['state'], TranslationKey> = {
  connected: 'admin.agentCostConnection.connected',
  disconnected: 'admin.agentCostConnection.disconnected',
  reconnectNeeded: 'admin.agentCostConnection.reconnectNeeded',
  storageNotConfigured: 'admin.agentCostConnection.storageNotConfigured',
};

export default function AgentCostConnectionPage() {
  const t = useT();
  const [params] = useSearchParams();
  const [status, setStatus] = useState<AgentCostConnectionStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const outcome = params.get('connection');
  const outcomeKey = outcome ? outcomes.get(outcome) : undefined;

  const load = useCallback(async () => {
    setBusy(true);
    setError(null);
    try { setStatus(await fetchAgentCostConnection()); }
    catch (e) { setError(e instanceof Error ? e.message : t('admin.agentCostConnection.failed')); }
    finally { setBusy(false); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);

  async function connect() {
    setBusy(true);
    setError(null);
    try { window.location.assign(await beginAgentCostConnection()); }
    catch (e) {
      setError(e instanceof Error ? e.message : t('admin.agentCostConnection.failed'));
      setBusy(false);
    }
  }

  async function disconnect() {
    if (!window.confirm(t('admin.agentCostConnection.disconnectConfirm'))) return;
    setBusy(true);
    setError(null);
    try { setStatus(await disconnectAgentCostConnection()); }
    catch (e) { setError(e instanceof Error ? e.message : t('admin.agentCostConnection.failed')); }
    finally { setBusy(false); }
  }

  return (
    <div>
      <Title3 block>{t('admin.agentCostConnection.title')}</Title3>
      <Text block style={{ marginBlock: 16 }}>{t('admin.agentCostConnection.description')}</Text>
      {outcomeKey && (
        <MessageBar intent={outcome === 'connected' ? 'success' : 'error'}>
          <MessageBarBody>{t(outcomeKey)}</MessageBarBody>
        </MessageBar>
      )}
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      <Card style={{ marginBlock: 16 }}>
        <Text weight="semibold">{t('admin.agentCostConnection.status')}</Text>
        <Text>{status ? t(states[status.state] ?? 'admin.agentCostConnection.failed') : t('admin.agentCostConnection.loading')}</Text>
        <Text>{t('admin.agentCostConnection.prerequisites')}</Text>
        <Text>{t('admin.agentCostConnection.persistence')}</Text>
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
          <Button appearance="primary" disabled={busy || !status || status.state === 'storageNotConfigured'} onClick={() => void connect()}>
            {t('admin.agentCostConnection.connect')}
          </Button>
          <Button disabled={busy || !status || !['connected', 'reconnectNeeded'].includes(status.state)} onClick={() => void disconnect()}>
            {t('admin.agentCostConnection.disconnect')}
          </Button>
          <Button disabled={busy} onClick={() => void load()}>{t('admin.agentCostConnection.refresh')}</Button>
        </div>
      </Card>
      <Text block>{t('admin.agentCostConnection.isolation')}</Text>
    </div>
  );
}
