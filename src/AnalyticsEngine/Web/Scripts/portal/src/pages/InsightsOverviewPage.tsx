import { useEffect, useMemo, useState } from 'react';
import {
  Badge,
  Subtitle2,
  Text,
  Title3,
  MessageBar,
  MessageBarBody,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useT, type TFunction, type TranslationKey } from '../i18n';
import { fetchSystemStatus } from '../api/systemStatusApi';
import { fetchHealthData, fetchHealthSummary } from '../api/healthApi';
import type { SystemStatus } from '../types/systemStatus';
import type { DataOverviewSection, HealthSummary } from '../types/health';
import DataKpiTiles from '../components/overview/DataKpiTiles';
import HealthSnapshot from '../components/overview/HealthSnapshot';
import WhereToNext from '../components/overview/WhereToNext';
import Spinner from '../components/Spinner';

const useStyles = makeStyles({
  header: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '10px',
    flexWrap: 'wrap',
  },
  lede: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginTop: '4px',
  },
  sections: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
    marginTop: '20px',
  },
  sectionHeading: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '10px',
    flexWrap: 'wrap',
    marginBottom: '8px',
  },
  sectionNote: {
    color: tokens.colorNeutralForeground3,
  },
  banner: {
    marginTop: '12px',
  },
  imports: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '6px',
    marginTop: '12px',
  },
});

const phrase = (...parts: string[]) => parts.join(' ');

export const ENABLED_IMPORT_LABELS_BY_SETTING_PROPERTY: Record<string, { english: string; key: TranslationKey }> = {
  ActivityLog: { english: 'Activity/audit', key: 'overview.enabledImport.activityLog' },
  Copilot: { english: 'Copilot', key: 'overview.enabledImport.copilot' },
  CopilotInteractionHistory: {
    english: phrase('Copilot', 'AI', 'interaction', 'history', '(tenant-wide', 'unless', 'scoped)'),
    key: 'overview.enabledImport.copilotInteractionHistory',
  },
  ImportPowerPlatform: { english: 'Power Platform', key: 'overview.enabledImport.powerPlatform' },
  ImportDlp: { english: phrase('DLP', 'policy', 'events'), key: 'overview.enabledImport.dlpPolicyEvents' },
  GraphUsersMetadata: { english: 'User metadata', key: 'overview.enabledImport.userMetadata' },
  GraphUsageReports: { english: 'Usage reports', key: 'overview.enabledImport.usageReports' },
  GraphCopilotUsageReports: {
    english: phrase('Copilot', 'usage', 'reports', '(Graph)'),
    key: 'overview.enabledImport.copilotUsageReportsGraph',
  },
  GraphTeams: { english: 'Teams', key: 'overview.enabledImport.teams' },
  WebTraffic: { english: 'Web traffic', key: 'overview.enabledImport.webTraffic' },
  SentEmails: { english: 'Sent emails', key: 'overview.enabledImport.sentEmails' },
  Calls: { english: 'Teams calls', key: 'overview.enabledImport.teamsCalls' },
  CopilotStudioCredits: { english: phrase('Copilot', 'Studio', 'credits', '(billed)'), key: 'overview.enabledImport.copilotStudioCredits' },
  AzureCostManagement: { english: phrase('Azure', 'costs', '(Cost', 'Management)'), key: 'overview.enabledImport.azureCosts' },
};

const ENABLED_IMPORT_KEY_BY_ENGLISH = new Map(
  Object.values(ENABLED_IMPORT_LABELS_BY_SETTING_PROPERTY).map((entry) => [entry.english, entry.key]),
);

export function enabledImportLabelText(t: TFunction, serverLabel: string): string {
  const key = ENABLED_IMPORT_KEY_BY_ENGLISH.get(serverLabel);
  return key ? t(key) : serverLabel;
}

/**
 * Insights landing page: what data the solution holds, whether it is still arriving and healthy, and
 * where to go next.
 *
 * Three questions, in the order an admin asks them:
 *  1. "Do we actually have data?" - headline figures, limited to the imports this deployment runs.
 *  2. "Is it healthy and still flowing?" - the cheap health roll-up plus data freshness.
 *  3. "What can I do with it?" - a short tour of the pages that apply to this deployment.
 *
 * The fetch strategy matters because this is the first screen everyone sees, on tenants with very
 * large fact tables. Only api/SystemStatus blocks the first paint (it is cheap and server-cached, and
 * now counts only the tables whose import is switched on). The two health calls are best-effort and
 * fill in afterwards: api/Health/summary is cheap but reaches App Insights, and api/Health/data runs
 * the only genuinely heavy scan. Either can fail or time out without degrading the page, and both are
 * 60s-cached and single-flight server-side, so repeat visits are effectively free.
 */
export default function InsightsOverviewPage() {
  const t = useT();
  const styles = useStyles();
  const [status, setStatus] = useState<SystemStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [health, setHealth] = useState<HealthSummary | null>(null);
  const [healthError, setHealthError] = useState<string | null>(null);
  const [dataSection, setDataSection] = useState<DataOverviewSection | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetchSystemStatus()
      .then((s) => {
        if (!cancelled) setStatus(s);
      })
      .catch((e: any) => {
        if (!cancelled) setError(e instanceof Error ? e.message : t('overview.page.loadError'));
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
    // Best-effort only - a slow or failing health roll-up must not degrade the landing page.
    fetchHealthSummary()
      .then((s) => {
        if (!cancelled) setHealth(s);
      })
      .catch((e: any) => {
        if (!cancelled) setHealthError(e instanceof Error ? e.message : t('overview.page.unknownError'));
      });
    return () => {
      cancelled = true;
    };
  }, [t]);

  useEffect(() => {
    let cancelled = false;
    // The heavy one. Silently omitted on failure/timeout - see the note above.
    fetchHealthData()
      .then((d) => {
        if (!cancelled) setDataSection(d);
      })
      .catch(() => {
        /* ignored on purpose */
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const counts = useMemo(() => status?.dataCounts ?? [], [status]);
  const countKeys = useMemo(() => counts.map((c) => c.key), [counts]);
  const hasAnyData = useMemo(() => counts.some((c) => c.count > 0), [counts]);

  if (loading) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={100} label={t('overview.page.loading')} />
      </div>
    );
  }

  if (error || !status) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error ?? t('overview.page.noDataAvailable')}</MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <div className={styles.header}>
        <Title3 as="h1">{t('overview.page.title')}</Title3>
        {status.buildLabel && (
          <Badge appearance="tint" color="informative">
            {status.buildLabel}
          </Badge>
        )}
      </div>
      <Text className={styles.lede}>
        {t('overview.page.lede')}
      </Text>

      <div className={styles.sections}>
        <section>
          <div className={styles.sectionHeading}>
            <Subtitle2 as="h2">{t('overview.page.yourDataHeading')}</Subtitle2>
            <Text size={200} className={styles.sectionNote}>
              {status.importSettingsKnown
                ? t('overview.page.importSettingsKnown')
                : t('overview.page.importSettingsUnknown')}
            </Text>
          </div>

          {counts.length === 0 ? (
            <MessageBar intent="info">
              <MessageBarBody>
                {t('overview.page.noImportsPrefix')}{' '}
                <a href="#/admin/health">{t('overview.page.serviceHealthLink')}</a>.
              </MessageBarBody>
            </MessageBar>
          ) : (
            <>
              <DataKpiTiles counts={counts} />
              {!hasAnyData && (
                <div className={styles.banner}>
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      {t('overview.page.zeroFiguresPrefix')} <a href="#/admin/health">{t('overview.page.serviceHealthLink')}</a> {t('overview.page.zeroFiguresSuffix')}
                    </MessageBarBody>
                  </MessageBar>
                </div>
              )}
            </>
          )}

          {status.enabledImports.length > 0 && (
            <div className={styles.imports}>
              <Text size={200} className={styles.sectionNote}>
                {t('overview.page.importsSwitchedOn')}
              </Text>
              {status.enabledImports.map((name) => (
                <Badge key={name} appearance="outline" color="informative">
                  {enabledImportLabelText(t, name)}
                </Badge>
              ))}
            </div>
          )}
        </section>

        <section>
          <HealthSnapshot
            summary={health}
            summaryError={healthError}
            data={dataSection}
            showAuditFreshness={countKeys.includes('auditEvents')}
            showWebFreshness={countKeys.includes('webHits')}
          />
        </section>

        <section>
          <div className={styles.sectionHeading}>
            <Subtitle2 as="h2">{t('overview.page.whereToNextHeading')}</Subtitle2>
            <Text size={200} className={styles.sectionNote}>
              {t('overview.page.whereToNextNote')}
            </Text>
          </div>
          <WhereToNext availableKeys={countKeys} />
        </section>
      </div>
    </div>
  );
}
