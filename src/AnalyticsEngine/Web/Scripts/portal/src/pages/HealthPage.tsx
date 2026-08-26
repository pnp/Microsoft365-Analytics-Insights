import { useCallback, useMemo, useState, type ComponentType } from 'react';
import { Badge, Tab, TabList, Text, Title3, makeStyles, tokens, type SelectTabEventHandler } from '@fluentui/react-components';
import { fetchHealthSummary } from '../api/healthApi';
import { AUTO_REFRESH_MS, formatUtc, overallColor, useHealthSection } from '../components/health/healthShared';
import OverviewPanel from '../components/health/OverviewPanel';
import LivenessPanel from '../components/health/LivenessPanel';
import ExceptionsPanel from '../components/health/ExceptionsPanel';
import ComponentsPanel from '../components/health/ComponentsPanel';
import DataPanel from '../components/health/DataPanel';
import ConfigPanel from '../components/health/ConfigPanel';

const useStyles = makeStyles({
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
  },
  desc: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginTop: '8px',
    marginBottom: '4px',
  },
  tabBar: {
    marginTop: '8px',
    marginBottom: '16px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginTop: '16px',
  },
});

// The lazy-loaded detail sub-sections. Each panel fetches only its own endpoint, and only once its
// tab has been opened (see below). Overview is handled separately - it shares the summary fetch that
// also feeds the header badge.
const DETAIL_PANELS: { key: string; label: string; Panel: ComponentType<{ active: boolean }> }[] = [
  { key: 'liveness', label: 'Import liveness', Panel: LivenessPanel },
  { key: 'exceptions', label: 'Exceptions', Panel: ExceptionsPanel },
  { key: 'components', label: 'Component health', Panel: ComponentsPanel },
  { key: 'data', label: 'Data overview', Panel: DataPanel },
  { key: 'config', label: 'Configuration', Panel: ConfigPanel },
];

export default function HealthPage() {
  const styles = useStyles();
  const [selected, setSelected] = useState('overview');
  // Detail panels are mounted lazily on first open, then kept mounted (hidden) so switching back is
  // instant without re-fetching - but only the visible one auto-refreshes.
  const [activated, setActivated] = useState<Set<string>>(() => new Set());

  // The summary powers both the header badge and the Overview tab. It loads once on mount (for the
  // header) and auto-refreshes only while Overview is on top.
  const summary = useHealthSection(fetchHealthSummary, selected === 'overview');

  const openTab = useCallback((key: string) => {
    setSelected(key);
    if (key !== 'overview') {
      setActivated((prev) => (prev.has(key) ? prev : new Set(prev).add(key)));
    }
  }, []);

  const onTabSelect: SelectTabEventHandler = (_e, data) => openTab(String(data.value));

  const overallStatus = summary.data?.overallStatus ?? null;
  const buildLabel = summary.data?.buildLabel ?? null;

  const detailPanels = useMemo(
    () =>
      DETAIL_PANELS.map(({ key, Panel }) =>
        activated.has(key) ? (
          <div key={key} style={{ display: selected === key ? 'block' : 'none' }}>
            <Panel active={selected === key} />
          </div>
        ) : null,
      ),
    [activated, selected],
  );

  return (
    <div>
      <div className={styles.headerRow}>
        <Title3>System Health{buildLabel ? ` - ${buildLabel}` : ''}</Title3>
        <Badge appearance="filled" size="large" color={overallColor(overallStatus)}>
          {overallStatus ?? 'Checking...'}
        </Badge>
      </div>

      <Text className={styles.desc}>
        Pick a sub-section below - each one loads its own data on demand and auto-refreshes every {AUTO_REFRESH_MS / 1000}
        s while open (cached server-side). This complements the Azure Monitor alert rules (which push when something
        breaks) - it's the at-a-glance green board.
        {summary.data ? ` Overview loaded ${formatUtc(summary.data.loadedAtUtc)}.` : ''}
      </Text>

      <div className={styles.tabBar}>
        <TabList selectedValue={selected} onTabSelect={onTabSelect}>
          <Tab value="overview">Overview</Tab>
          {DETAIL_PANELS.map(({ key, label }) => (
            <Tab key={key} value={key}>
              {label}
            </Tab>
          ))}
        </TabList>
      </div>

      {/* Overview is always mounted (it's cheap and it's the default view + header source). */}
      <div style={{ display: selected === 'overview' ? 'block' : 'none' }}>
        <OverviewPanel state={summary} onOpenSection={openTab} />
      </div>

      {detailPanels}

      <Text size={200} className={styles.muted}>
        To be alerted (not just to look), set up the Azure Monitor / Application Insights alert rules in the Health
        Alerts wiki guide. The same custom events shown here back those alerts.
      </Text>
    </div>
  );
}
