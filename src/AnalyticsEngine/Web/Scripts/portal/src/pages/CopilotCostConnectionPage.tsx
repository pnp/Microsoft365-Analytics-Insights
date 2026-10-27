import { useCallback, useEffect, useState } from 'react';
import {
  Button, Card, CardHeader, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface,
  DialogTitle, Link, MessageBar, MessageBarBody, Subtitle2, Text, Title3, makeStyles, tokens,
} from '@fluentui/react-components';
import { CheckmarkCircle24Filled } from '@fluentui/react-icons';
import {
  changeCostConnection, costConnectionText, costCollectionPhaseText, fetchCostCollection, fetchCostConnection,
  type CostCollectionStatus, type CostConnectionStatus,
} from '../api/copilotCostConnectionApi';
import { formatNumber, useT } from '../i18n';
import { formatUtc } from '../components/health/healthShared';

const useStyles = makeStyles({
  card: { minWidth: 0 },
  actions: { display: 'flex', flexWrap: 'wrap', gap: '8px', '> button': { maxWidth: '100%', whiteSpace: 'normal' } },
  content: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0 },
  permissions: { display: 'flex', flexDirection: 'column', gap: '8px', margin: 0, paddingLeft: '20px', overflowWrap: 'anywhere' },
  connected: {
    backgroundColor: tokens.colorStatusSuccessBackground1,
    border: `1px solid ${tokens.colorStatusSuccessBorder1}`,
    borderRadius: tokens.borderRadiusMedium, padding: '16px',
    display: 'flex', alignItems: 'flex-start', gap: '12px',
  },
  success: { color: tokens.colorStatusSuccessForeground1, flexShrink: 0 },
});

export default function CopilotCostConnectionPage() {
  const t = useT();
  const styles = useStyles();
  const [state, setState] = useState<CostConnectionStatus | null>(null);
  const [busy, setBusy] = useState(true);
  const [running, setRunning] = useState(false);
  const [confirmation, setConfirmation] = useState<'probe' | 'disconnect' | 'resetProbe' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [collection, setCollection] = useState<CostCollectionStatus | null>(null);
  const [collectionBusy, setCollectionBusy] = useState(false);
  const [collectionError, setCollectionError] = useState<string | null>(null);

  useEffect(() => {
    const callback = new URLSearchParams(window.location.hash.split('?')[1]).get('costConnection');
    if (callback && callback !== 'connected') setError(costConnectionText(callback));
  }, []);

  const refresh = useCallback(async (clearError = true) => {
    setBusy(true);
    if (clearError) setError(null);
    try { setState(await fetchCostConnection()); }
    catch (e) {
      setState(null);
      setError(e instanceof Error ? e.message : t('admin.costConnection.requestFailed'));
    }
    finally { setBusy(false); }
  }, [t]);

  useEffect(() => { void refresh(false); }, [refresh]);

  async function refreshCollection() {
    setCollectionBusy(true);
    setCollectionError(null);
    try { setCollection(await fetchCostCollection()); }
    catch (e) { setCollectionError(e instanceof Error ? e.message : t('admin.costConnection.requestFailed')); }
    finally { setCollectionBusy(false); }
  }

  async function change(action: 'connect' | 'disconnect' | 'resetProbe') {
    if (!state?.csrfToken) return;
    setBusy(true);
    setError(null);
    try {
      const result = await changeCostConnection(action, state.csrfToken);
      if (result.authorizationUrl) {
        const destination = new URL(result.authorizationUrl);
        if (destination.protocol !== 'https:' || destination.hostname !== 'login.microsoftonline.com')
          throw new Error(t('admin.costConnection.invalidCallback'));
        window.location.assign(destination.href);
      } else setState(await fetchCostConnection());
    } catch (e) { setError(e instanceof Error ? e.message : t('admin.costConnection.requestFailed')); }
    finally { setBusy(false); }
  }

  useEffect(() => {
    if (!running) return;
    let active = true;
    async function run() {
      try {
        let current = await fetchCostConnection();
        if (active) setState(current);
        while (active && current.status === 'connected' && current.csrfToken && current.probe?.phase !== 'observed') {
          await changeCostConnection('probe', current.csrfToken);
          current = await fetchCostConnection();
          if (!active) break;
          setState(current);
          if (current.probe?.lastOutcome && current.probe.lastOutcome !== 'sliceCompleted')
            throw new Error(costConnectionText(current.probe.lastOutcome));
          await new Promise<void>((resolve) => setTimeout(resolve, 500));
        }
      } catch (e) {
        if (active) {
          setError(e instanceof Error ? e.message : t('admin.costConnection.requestFailed'));
          // A failed POST may still have saved progress and its failure outcome.
          try {
            const latest = await fetchCostConnection();
            if (active) setState(latest);
          } catch { /* Keep the original failure rather than replacing it with a status-read error. */ }
        }
      } finally { if (active) setRunning(false); }
    }
    void run();
    return () => { active = false; };
  }, [running, t]);

  const manageable = !!state?.csrfToken;
  const probe = state?.probe;
  const knownPhase = probe && ['context', 'inventory', 'users', 'observed'].includes(probe.phase);
  return (
    <div className={styles.content}>
      <Title3 as="h1">{t('admin.costConnection.title')}</Title3>
      <Text>{t('admin.costConnection.prerequisites')}</Text>
      <Card className={styles.card}>
        <CardHeader header={<Subtitle2>{t('admin.costConnection.statusTitle')}</Subtitle2>} />
        {busy ? <Text role="status">{t('admin.costConnection.working')}</Text> : state?.status === 'connected' ? (
          <div role="status" className={styles.connected}>
            <CheckmarkCircle24Filled aria-hidden="true" className={styles.success} />
            <div className={styles.content}>
              <Text weight="semibold" size={400} className={styles.success}>{t('admin.costConnection.connectedTitle')}</Text>
              <Text>{costConnectionText(state.status)}</Text>
              <Text>{t('admin.costConnection.connectionScope')}</Text>
              {state.connectedAt && <Text>{t('admin.costConnection.connectedAt', { at: formatUtc(state.connectedAt) })}</Text>}
              {state.tokenAcquiredAt && <Text>{t('admin.costConnection.tokenAcquired', { at: formatUtc(state.tokenAcquiredAt) })}</Text>}
            </div>
          </div>
        ) : <MessageBar intent={state?.status === 'disconnected' || !state ? 'info' : 'warning'}>
          <MessageBarBody><span role="status">{state ? costConnectionText(state.status) : t('admin.costConnection.notChecked')}</span></MessageBarBody>
        </MessageBar>}
        {error && <MessageBar intent="error"><MessageBarBody><span role="alert">{error}</span></MessageBarBody></MessageBar>}
        <div className={styles.actions}>
          <Button onClick={() => void refresh()} disabled={busy || running}>{t('admin.costConnection.check')}</Button>
          {manageable && (
            <>
              <Button onClick={() => void change('connect')} disabled={busy || running}>
                {state?.status === 'disconnected' ? t('admin.costConnection.connect') : t('admin.costConnection.reconnect')}
              </Button>
              <Button onClick={() => setConfirmation('disconnect')} disabled={busy || running || state?.status === 'disconnected'}>
                {t('admin.costConnection.disconnect')}
              </Button>
            </>
          )}
        </div>
      </Card>
      <Card className={styles.card}>
        <CardHeader header={<Subtitle2>{t('admin.costConnection.testTitle')}</Subtitle2>} />
        <Text>{t('admin.costConnection.testExplanation')}</Text>
        <div className={styles.actions}>
          <Button onClick={() => setConfirmation(probe?.phase === 'observed' ? 'resetProbe' : 'probe')} disabled={!manageable || busy || running || state?.status !== 'connected'}>
            {probe?.phase === 'observed' ? t('admin.costConnection.newProbe') : t('admin.costConnection.runProbe')}
          </Button>
          {running && <Button onClick={() => setRunning(false)}>{t('admin.costConnection.stop')}</Button>}
        </div>
        <Subtitle2>{t('admin.costConnection.checksTitle')}</Subtitle2>
        <ul className={styles.permissions}>
          <li><Text>{t(manageable ? 'admin.costConnection.storageChecked' : 'admin.costConnection.storageNotChecked')}</Text></li>
          <li><Text>{t(!knownPhase ? 'admin.costConnection.contextNotChecked' : probe.phase === 'context' ? 'admin.costConnection.contextPending' : 'admin.costConnection.contextChecked')}</Text></li>
          <li><Text>{t(!knownPhase || probe.phase === 'context' ? 'admin.costConnection.directoryNotChecked' : probe.inventoryComplete ? 'admin.costConnection.directoryChecked' : 'admin.costConnection.directoryPending')}</Text></li>
        </ul>
        <Text>{t('admin.costConnection.checksLimit')}</Text>
        {probe && (
          <div role="status">
            <Text block>{running ? t('admin.costConnection.testRunning') : probe.phase === 'observed' ? t('admin.costConnection.observedNotApproved') : t('admin.costConnection.inProgress')}</Text>
            <Text block>{t('admin.costConnection.testStage', { stage: costCollectionPhaseText(probe.phase === 'observed' ? 'complete' : probe.phase) })}</Text>
            {probe.lastOutcome && error !== costConnectionText(probe.lastOutcome) && <Text block>{t('admin.costConnection.testOutcome', { outcome: costConnectionText(probe.lastOutcome) })}</Text>}
            <Text block>{t('admin.costConnection.coverage', {
              users: formatNumber(probe.directoryUsers), observed: formatNumber(probe.observed), measured: formatNumber(probe.measured),
              empty: formatNumber(probe.empty), partial: formatNumber(probe.partial), unevaluated: formatNumber(probe.unevaluated),
              failed: formatNumber(probe.failed), denied: formatNumber(probe.denied),
            })}</Text>
            {!probe.inventoryComplete && <Text>{t('admin.costConnection.incompleteInventory')}</Text>}
            {(probe.failed > 0 || probe.partial > 0 || probe.unevaluated > 0) && <Text block>{t('admin.costConnection.coverageHelp')}</Text>}
          </div>
        )}
      </Card>
      <Card className={styles.card}>
        <Subtitle2>{t('admin.costConnection.collectionTitle')}</Subtitle2>
        <Text>{t('admin.costConnection.previewCollection')}</Text>
        <Button onClick={() => void refreshCollection()} disabled={collectionBusy || busy || running}>
          {t('admin.costConnection.collectionCheck')}
        </Button>
        {collectionError && <MessageBar intent="error"><MessageBarBody><span role="alert">{collectionError}</span></MessageBarBody></MessageBar>}
        <div role="status">
          {collectionBusy ? <Text>{t('admin.costConnection.working')}</Text> : !collection ? <Text>{t('admin.costConnection.collectionNotChecked')}</Text> : (
            <>
              <Text block>{collection.status !== 'available' ? costConnectionText(collection.status) : costCollectionPhaseText(collection.phase)}</Text>
              {collection.status === 'available' && (
                <>
                  <Text block>{t('admin.costConnection.collectionCoverage', {
                    users: formatNumber(collection.directoryUsers), observed: formatNumber(collection.observed),
                    pending: formatNumber(collection.pending), retrying: formatNumber(collection.retrying),
                    measured: formatNumber(collection.measured), empty: formatNumber(collection.empty),
                    partial: formatNumber(collection.partial), unevaluated: formatNumber(collection.unevaluated),
                    failed: formatNumber(collection.failed), denied: formatNumber(collection.denied),
                  })}</Text>
                  {!collection.inventoryComplete && <Text block>{t('admin.costConnection.incompleteInventory')}</Text>}
                  {collection.lastAttempt && <Text block>{t('admin.costConnection.collectionAttempt', { at: formatUtc(collection.lastAttempt) })}</Text>}
                  {collection.lastCompleted && <Text block>{t('admin.costConnection.collectionCompletedAt', { at: formatUtc(collection.lastCompleted) })}</Text>}
                  {collection.lastOutcome && <Text block>{t('admin.costConnection.collectionOutcome', { outcome: costConnectionText(collection.lastOutcome) })}</Text>}
                </>
              )}
            </>
          )}
        </div>
        <Text>{t('admin.costConnection.historyRetention')}</Text>
      </Card>
      <details>
        <summary>{t('admin.costConnection.permissionsTitle')}</summary>
        <ul className={styles.permissions}>
          <li><Text>{t('admin.costConnection.portalPermission')}</Text></li>
          <li><Text>{t('admin.costConnection.delegatedPermissions')}</Text></li>
          <li><Text>{t('admin.costConnection.readerPermission')}</Text></li>
          <li><Text>{t('admin.costConnection.runtimePermissions')}</Text></li>
        </ul>
        <Link href="https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Experimental-Copilot-Credit-Connection" target="_blank" rel="noopener noreferrer">
          {t('admin.costConnection.help')}
        </Link>
      </details>
      <Dialog open={confirmation !== null} onOpenChange={(_, data) => { if (!data.open) setConfirmation(null); }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>{confirmation === 'disconnect' ? t('admin.costConnection.disconnect') : confirmation === 'resetProbe' ? t('admin.costConnection.newProbe') : t('admin.costConnection.runProbe')}</DialogTitle>
            <DialogContent>{confirmation === 'disconnect' ? t('admin.costConnection.disconnectWarning') : confirmation === 'resetProbe' ? t('admin.costConnection.resetWarning') : t('admin.costConnection.probeWarning')}</DialogContent>
            <DialogActions>
              <Button onClick={() => setConfirmation(null)}>{t('admin.costConnection.cancel')}</Button>
              <Button appearance="primary" onClick={() => {
                const action = confirmation;
                setConfirmation(null);
                if (action === 'disconnect') void change('disconnect');
                else if (action === 'resetProbe') void change('resetProbe');
                else {
                  setError(null);
                  setRunning(true);
                }
              }}>{t('admin.costConnection.confirm')}</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
