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
import type { HealthSummary } from '../../types/health';
import { type SectionState, SectionReasons, statusColor, useHealthStyles } from './healthShared';

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
  const shared = useHealthStyles();
  const styles = useStyles();
  const { data, loading, error } = state;

  if (loading && !data) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={80} label="Loading system health..." />
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
        A single "is it working?" view. All values are read-only and best-effort - a data-source hiccup greys out one
        sub-section, it never breaks the page. This Overview rolls up every section but skips the heavy database scans
        (those load only when you open the Data tab), so it stays cheap even on a large tenant.
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
              Application Insights is not configured for this web app, so the Import liveness, Exceptions and
              Component-health (App Insights) sub-sections are unavailable. The Data overview, Configuration and runtime
              credential / Service Bus checks still work.
            </MessageBarBody>
          </MessageBar>
        </div>
      )}

      <Text className={shared.subHeading}>Sub-sections</Text>
      <Table size="small" aria-label="Section status">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Sub-section</TableHeaderCell>
            <TableHeaderCell>Status</TableHeaderCell>
            <TableHeaderCell>Notes</TableHeaderCell>
            <TableHeaderCell />
          </TableRow>
        </TableHeader>
        <TableBody>
          {data.sections.map((s) => (
            <TableRow key={s.key}>
              <TableCell>{s.label}</TableCell>
              <TableCell>
                <Badge appearance="filled" color={statusColor(s.status)}>
                  {s.status}
                </Badge>
              </TableCell>
              <TableCell>
                <SectionReasons reasons={s.reasons} />
              </TableCell>
              <TableCell>
                <Button size="small" appearance="subtle" onClick={() => onOpenSection(s.key)}>
                  Open
                </Button>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
