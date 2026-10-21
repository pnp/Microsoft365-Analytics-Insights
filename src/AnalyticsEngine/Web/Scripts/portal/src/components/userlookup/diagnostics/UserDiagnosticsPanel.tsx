import { Card, CardHeader, MessageBar, MessageBarBody, Subtitle2, Text, makeStyles, tokens } from '@fluentui/react-components';
import type { UserDiagnostics, UserDiagnosticsContext } from '../../../types/userDiagnostics';
import { useT } from '../../../i18n';
import { findingText, formatUtc } from './diagnosticsText';
import LicenceEvidenceCard from './LicenceEvidenceCard';
import ReportVerdictCard from './ReportVerdictCard';
import CopilotRulesCard from './CopilotRulesCard';
import CoverageCard from './CoverageCard';
import UserRefreshCard from './UserRefreshCard';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  findings: { display: 'flex', flexDirection: 'column', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * The licence and data diagnostics for one person: what the evidence suggests, the licence evidence and live
 * comparison, a one-person refresh, why a report counts them as it does, the Copilot seat rules, and how fresh each
 * kind of data is. Only the refresh changes anything, and only when asked.
 */
export default function UserDiagnosticsPanel({
  diagnostics,
  context,
  onDataChanged,
}: {
  diagnostics: UserDiagnostics;
  context?: UserDiagnosticsContext | null;
  /** Called when a refresh finishes, so the page can reload what it may have changed. */
  onDataChanged?: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const upn = diagnostics.storedUser?.userPrincipalName ?? diagnostics.lookedUpUpn;
  const findings = diagnostics.findings
    .map((finding) => ({ finding, text: findingText(t, finding) }))
    .filter((entry): entry is { finding: typeof entry.finding; text: string } => entry.text !== null);

  return (
    <section className={styles.stack} aria-label={t('userDiagnostics.title')}>
      <Card>
        <CardHeader
          header={<Subtitle2>{t('userDiagnostics.title')}</Subtitle2>}
          description={
            <Text size={200} className={styles.muted}>
              {t('userDiagnostics.loadedAt', { when: formatUtc(diagnostics.loadedAtUtc) })}
            </Text>
          }
        />
        <Text size={200} className={styles.muted}>
          {t('userDiagnostics.intro')}
        </Text>
        <Text weight="semibold">{t('userDiagnostics.findings.title')}</Text>
        <div className={styles.findings}>
          {findings.length === 0 ? (
            <Text>{t('userDiagnostics.findings.none')}</Text>
          ) : (
            findings.map(({ finding, text }, index) => (
              <MessageBar key={`${finding.key}-${index}`} intent={finding.severity === 'warning' ? 'warning' : 'info'}>
                <MessageBarBody>{text}</MessageBarBody>
              </MessageBar>
            ))
          )}
        </div>
      </Card>

      <LicenceEvidenceCard diagnostics={diagnostics} />
      {diagnostics.refresh && <UserRefreshCard upn={upn} capability={diagnostics.refresh} onFinished={onDataChanged} />}
      {diagnostics.storedUser && <ReportVerdictCard upn={upn} context={context} />}
      <CopilotRulesCard catalogue={diagnostics.catalogue} />
      {diagnostics.storedUser && <CoverageCard upn={upn} />}
    </section>
  );
}
