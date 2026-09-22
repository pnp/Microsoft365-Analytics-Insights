import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Table,
  TableHeader,
  TableRow,
  TableHeaderCell,
  TableBody,
  TableCell,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import Spinner from '../Spinner';
import { useT } from '../../i18n';
import type { HealthSummary } from '../../types/health';
import { type SectionState, SectionReasons, healthStatusText, statusColor, useHealthStyles } from './healthShared';

const useStyles = makeStyles({
  reasons: {
    marginTop: '8px',
    marginBottom: 0,
    paddingLeft: '20px',
  },
  intro: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginBottom: '12px',
  },
});

/**
 * Overview: the overall traffic-light + an at-a-glance per-section grid. Fed by the parent's cached
 * summary fetch (which skips the heavy SQL scans), so the default view stays cheap. Each grid row links
 * to that sub-section's tab for the detail.
 */
export default function OverviewPanel({
  state,
  onOpenSection,
}: {
  state: SectionState<HealthSummary>;
  onOpenSection: (key: string) => void;
}) {
  const t = useT();
  const shared = useHealthStyles();
  const styles = useStyles();
  const { data, loading, error } = state;

  if (loading && !data) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={80} label={t('health.overview.loadingSystemHealth')} />
      </div>
    );
  }

  if (error && !data) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error}</MessageBarBody>
      </MessageBar>
    );
  }

  if (!data) return null;

  return (
    <div>
      <Text className={styles.intro}>
        {t('health.overview.intro')}
      </Text>

      {data.overallReasons.length > 0 && (
        <ul className={styles.reasons}>
          {data.overallReasons.map((r, i) => (
            <li key={i}>
              <Text size={200}>{r}</Text>
            </li>
          ))}
        </ul>
      )}

      {!data.appInsightsConfigured && (
        <div style={{ marginTop: 12 }}>
          <MessageBar intent="warning">
            <MessageBarBody>
              {t('health.overview.appInsightsNotConfigured')}
            </MessageBarBody>
          </MessageBar>
        </div>
      )}

      <Text className={shared.subHeading}>{t('health.overview.subSectionsHeading')}</Text>
      <Table size="small" aria-label={t('health.overview.sectionStatusAriaLabel')}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{t('health.overview.columnSubSection')}</TableHeaderCell>
            <TableHeaderCell>{t('health.overview.columnStatus')}</TableHeaderCell>
            <TableHeaderCell>{t('health.overview.columnNotes')}</TableHeaderCell>
            <TableHeaderCell />
          </TableRow>
        </TableHeader>
        <TableBody>
          {data.sections.map((s) => (
            <TableRow key={s.key}>
              <TableCell>{s.label}</TableCell>
              <TableCell>
                <Badge appearance="filled" color={statusColor(s.status)}>
                  {healthStatusText(s.status, t)}
                </Badge>
              </TableCell>
              <TableCell>
                <SectionReasons reasons={s.reasons} />
              </TableCell>
              <TableCell>
                <Button size="small" appearance="subtle" onClick={() => onOpenSection(s.key)}>
                  {t('health.overview.open')}
                </Button>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
