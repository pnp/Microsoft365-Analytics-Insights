import { useCallback, useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Button, Card, MessageBar, MessageBarBody, Text, Title3, tokens } from '@fluentui/react-components';
import { CheckmarkCircle24Filled } from '@fluentui/react-icons';
import {
  beginAgentCostConnection, disconnectAgentCostConnection, fetchAgentCostConnection,
  type AgentCostConnectionStatus,
} from '../api/agentCostConnectionApi';
import { formatDateParts, useT, type TranslationKey } from '../i18n';

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
    catch (e) {
      setStatus(null);
      setError(e instanceof Error ? e.message : t('admin.agentCostConnection.failed'));
    }
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
      {outcomeKey && (outcome !== 'connected' || (status?.state === 'connected' && !error)) && (
        <MessageBar intent={outcome === 'connected' ? 'success' : 'error'}>
          <MessageBarBody>{t(outcomeKey)}</MessageBarBody>
        </MessageBar>
      )}
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      <Card style={{ marginBlock: 16 }}>
        <Text weight="semibold">{t('admin.agentCostConnection.status')}</Text>
        {status?.state === 'connected' ? (
          <div role="status" style={{
            backgroundColor: tokens.colorStatusSuccessBackground1,
            border: `1px solid ${tokens.colorStatusSuccessBorder1}`,
            borderRadius: tokens.borderRadiusMedium, padding: 16,
            display: 'flex', alignItems: 'flex-start', gap: 12,
          }}>
            <CheckmarkCircle24Filled aria-hidden="true" style={{ color: tokens.colorStatusSuccessForeground1, flexShrink: 0 }} />
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8, minWidth: 0, overflowWrap: 'anywhere' }}>
              <Text weight="semibold" size={400} style={{ color: tokens.colorStatusSuccessForeground1 }}>
                {t('admin.agentCostConnection.connectedTitle')}
              </Text>
              <Text weight="semibold">{status.details?.account
                ? t('admin.agentCostConnection.account', { account: status.details.account })
                : t('admin.agentCostConnection.accountUnavailable')}</Text>
              {status.details?.verifiedUtc && (
                <Text>{t('admin.agentCostConnection.verified', {
                  when: formatDateParts(new Date(status.details.verifiedUtc), {
                    year: 'numeric', month: 'short', day: 'numeric',
                    hour: '2-digit', minute: '2-digit', timeZone: 'UTC', timeZoneName: 'short',
                  }),
                })}</Text>
              )}
              <Text>{t('admin.agentCostConnection.connected')}</Text>
              <Text>{t('admin.agentCostConnection.importReminder')}</Text>
            </div>
          </div>
        ) : (
          <Text>{status ? t(states[status.state] ?? 'admin.agentCostConnection.failed')
            : t(error ? 'admin.agentCostConnection.failed' : 'admin.agentCostConnection.loading')}</Text>
        )}
        {status?.state !== 'connected' && <Text>{t('admin.agentCostConnection.prerequisites')}</Text>}
        <Text>{t('admin.agentCostConnection.persistence')}</Text>
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
          <Button appearance={status?.state === 'connected' ? 'secondary' : 'primary'} disabled={busy || !status || status.state === 'storageNotConfigured'} onClick={() => void connect()}>
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
