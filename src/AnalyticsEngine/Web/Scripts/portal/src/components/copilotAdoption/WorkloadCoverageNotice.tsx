import { MessageBar, MessageBarBody } from '@fluentui/react-components';
import { useT, formatNumber } from '../../i18n';
import type { TranslationKey } from '../../i18n';
import type { WorkloadCoverage } from '../../types/copilotAdoption';
import { formatDate } from '../shared/KpiGrid';

const sourceKeys: Record<WorkloadCoverage['source'], TranslationKey> = {
  teams: 'copilotAdoption.workload.teams',
  outlook: 'copilotAdoption.workload.outlook',
  sharepoint: 'copilotAdoption.workload.sharepoint',
  onedrive: 'copilotAdoption.workload.onedrive',
};
const statusKeys: Record<WorkloadCoverage['status'], TranslationKey> = {
  complete: 'copilotAdoption.workload.complete',
  empty: 'copilotAdoption.workload.empty',
  partial: 'copilotAdoption.workload.partial',
  unverified: 'copilotAdoption.workload.unverified',
};

/** Persistent, printable provenance: dismissing general warnings must not turn unknown into zero. */
export default function WorkloadCoverageNotice({ coverage }: { coverage?: WorkloadCoverage[] }) {
  const t = useT();
  if (!coverage?.length) return null;
  const incomplete = coverage.some(source => source.status !== 'complete' && source.status !== 'empty');
  return (
    <MessageBar intent={incomplete ? 'warning' : 'info'}>
      <MessageBarBody>
        <strong>{t('copilotAdoption.workload.title')}</strong>
        <p>{t('copilotAdoption.workload.window', {
          from: formatDate(coverage[0].fromUtc),
          through: formatDate(coverage[0].throughUtc),
        })}</p>
        {incomplete && <p>{t('copilotAdoption.workload.warning')}</p>}
        <ul>
          {coverage.map(source => (
            <li key={source.source}>{t('copilotAdoption.workload.coverage', {
              source: t(sourceKeys[source.source]),
              status: t(statusKeys[source.status]),
              verified: formatNumber(source.verifiedDays),
              expected: formatNumber(source.expectedDays),
            })}</li>
          ))}
        </ul>
        {incomplete && <details>
          <summary>{t('copilotAdoption.workload.details')}</summary>
          <p>{t('copilotAdoption.workload.migration')}</p>
        </details>}
      </MessageBarBody>
    </MessageBar>
  );
}
