import { useEffect, useState } from 'react';
import { Link, MessageBar, MessageBarBody, Text, makeStyles } from '@fluentui/react-components';
import { usePortalAccess } from '../../access';
import { fetchCostApiHealth, type CostApiConnectionHealth } from '../../api/costApiHealthApi';
import { useT } from '../../i18n';
import { formatUtc } from '../health/healthShared';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '12px' },
  body: { display: 'flex', flexDirection: 'column', gap: '4px' },
});

export default function CostApiWarnings() {
  const t = useT();
  const { administration } = usePortalAccess();
  const styles = useStyles();
  const [connections, setConnections] = useState<CostApiConnectionHealth[]>([]);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    let timer: number | undefined;
    async function refresh() {
      try {
        const result = await fetchCostApiHealth(controller.signal);
        if (!controller.signal.aborted) {
          setConnections(result);
          setFailed(false);
        }
      } catch {
        if (!controller.signal.aborted) setFailed(true);
      } finally {
        if (!controller.signal.aborted) timer = window.setTimeout(refresh, 60_000);
      }
    }
    void refresh();
    return () => { controller.abort(); window.clearTimeout(timer); };
  }, []);

  const warnings = connections.filter((item) => !['disabled', 'disconnected', 'healthy'].includes(item.status));
  if (!failed && warnings.length === 0) return null;
  return (
    <div className={styles.stack}>
      {failed && <MessageBar intent="warning"><MessageBarBody>{t('health.costApi.checkFailed')}</MessageBarBody></MessageBar>}
      {warnings.map((item) => (
        <MessageBar key={item.source} intent={item.status === 'pending' ? 'info' : 'warning'}>
          <MessageBarBody>
            <div className={styles.body}>
              <Text weight="semibold">{t(item.source === 'graph' ? 'health.costApi.graphTitle' : 'health.costApi.studioTitle')}</Text>
              <Text>{t(item.status === 'pending' ? 'health.reason.costApiPending'
                : item.status === 'reconnectRequired' ? 'health.reason.costApiReconnectRequired'
                : item.status === 'permissionDenied' ? 'health.reason.costApiPermissionDenied'
                : item.status === 'failing' ? 'health.reason.costApiFailing' : 'health.reason.costApiUnavailable')}</Text>
              <Text>{t('health.costApi.lastRead', {
                when: item.lastSuccessfulRead ? formatUtc(item.lastSuccessfulRead) : t('health.time.never'),
              })}</Text>
              <Text>{t(item.source === 'graph' ? 'health.costApi.graphRecovery' : 'health.costApi.studioRecovery')}</Text>
              {administration
                ? <Link href={item.source === 'graph' ? '#/admin/copilot-cost-connection' : '#/admin/agent-cost-connection'}>{t('health.costApi.manage')}</Link>
                : <Text>{t('health.costApi.askAdmin')}</Text>}
            </div>
          </MessageBarBody>
        </MessageBar>
      ))}
    </div>
  );
}
