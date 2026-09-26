import { useCallback, useMemo, useState, type ComponentType } from 'react';
import { useNavigate } from 'react-router-dom';
import { Badge, Tab, TabList, Text, Title3, makeStyles, tokens, type SelectTabEventHandler } from '@fluentui/react-components';
import { fetchHealthSummary } from '../api/healthApi';
import { formatNumber, useT, type TranslationKey } from '../i18n';
import { buildLabelText } from '../product';
import { AUTO_REFRESH_MS, formatUtc, healthStatusText, overallColor, useHealthSection } from '../components/health/healthShared';
import OverviewPanel from '../components/health/OverviewPanel';
import LivenessPanel from '../components/health/LivenessPanel';
import ExceptionsPanel from '../components/health/ExceptionsPanel';
import ComponentsPanel from '../components/health/ComponentsPanel';
import DataPanel from '../components/health/DataPanel';

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
const DETAIL_PANELS: { key: string; labelKey: TranslationKey; Panel: ComponentType<{ active: boolean }> }[] = [
  { key: 'liveness', labelKey: 'health.tabs.importLiveness', Panel: LivenessPanel },
  { key: 'exceptions', labelKey: 'health.tabs.exceptions', Panel: ExceptionsPanel },
  { key: 'components', labelKey: 'health.tabs.componentHealth', Panel: ComponentsPanel },
  { key: 'data', labelKey: 'health.tabs.dataOverview', Panel: DataPanel },
];

/**
 * Summary sections that no longer have a tab here because they were consolidated into a page of
 * their own. Their status still rolls up into the Overview grid; opening one navigates instead.
 */
const RELOCATED_SECTIONS: Record<string, string> = {
  config: '/admin/configuration',
};

export default function HealthPage() {
  const t = useT();
  const styles = useStyles();
  const navigate = useNavigate();
  const [selected, setSelected] = useState('overview');
  // Detail panels are mounted lazily on first open, then kept mounted (hidden) so switching back is
  // instant without re-fetching - but only the visible one auto-refreshes.
  const [activated, setActivated] = useState<Set<string>>(() => new Set());

  // The summary powers both the header badge and the Overview tab. It loads once on mount (for the
  // header) and auto-refreshes only while Overview is on top.
  const summary = useHealthSection(fetchHealthSummary, selected === 'overview');

  const openTab = useCallback(
    (key: string) => {
      // Sections that moved to their own page navigate rather than selecting a (now absent) tab.
      const relocated = RELOCATED_SECTIONS[key];
      if (relocated) {
        navigate(relocated);
        return;
      }

      setSelected(key);
      if (key !== 'overview') {
        setActivated((prev) => (prev.has(key) ? prev : new Set(prev).add(key)));
      }
    },
    [navigate],
  );

  const onTabSelect: SelectTabEventHandler = (_e: unknown, data: { value: unknown }) => openTab(String(data.value));

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
        <Title3>{t('health.page.title', { buildLabel: buildLabel ? ` - ${buildLabelText(t, buildLabel)}` : '' })}</Title3>
        <Badge appearance="filled" size="large" color={overallColor(overallStatus)}>
          {overallStatus ? healthStatusText(overallStatus, t) : t('health.status.checking')}
        </Badge>
      </div>

      <Text className={styles.desc}>
        {t('health.page.description', { seconds: formatNumber(AUTO_REFRESH_MS / 1000) })}
        {summary.data ? ` ${t('health.page.overviewLoaded', { when: formatUtc(summary.data.loadedAtUtc) })}` : ''}
      </Text>

      <div className={styles.tabBar}>
        <TabList selectedValue={selected} onTabSelect={onTabSelect}>
          <Tab value="overview">{t('health.tabs.overview')}</Tab>
          {DETAIL_PANELS.map(({ key, labelKey }) => (
            <Tab key={key} value={key}>
              {t(labelKey)}
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
        {t('health.page.alertsGuidance')}
      </Text>
    </div>
  );
}
