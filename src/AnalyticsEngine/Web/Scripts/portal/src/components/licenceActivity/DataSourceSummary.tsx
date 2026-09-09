import { useId, useState } from 'react';
import { makeStyles, tokens, Card, Text, Badge, Button } from '@fluentui/react-components';
import { ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons';
import type { LicenceActivityCoverage } from '../../types/licenceActivity';
import CoveragePanel from './CoveragePanel';
import { statusMeta, type StatusTone } from './statuses';
import { formatAge } from './format';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '10px 14px',
  },
  head: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  headText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  title: {
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  chips: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    flexWrap: 'wrap',
  },
  generated: {
    color: tokens.colorNeutralForeground3,
  },
  panelWrap: {
    marginTop: '2px',
  },
});

interface DataSourceSummaryProps {
  coverage: LicenceActivityCoverage[];
  generatedUtc: string;
  expiresUtc: string;
  /** Injectable "now" for deterministic "prepared N days ago" captions in tests. */
  now?: Date;
}

/** Group the per-workload coverage by status, in a fixed worst-last order, so the collapsed chip row
 *  reads "3 Available · 1 Partial · 1 Not imported" rather than one badge per service. */
const STATUS_ORDER: string[] = [
  'available',
  'partial',
  'missingCoverage',
  'unmatchableIdentity',
  'notImported',
  'disabled',
];

interface StatusCount {
  status: string;
  label: string;
  tone: StatusTone;
  count: number;
}

function summariseStatuses(coverage: LicenceActivityCoverage[]): StatusCount[] {
  const counts = new Map<string, number>();
  for (const entry of coverage) counts.set(entry.status, (counts.get(entry.status) ?? 0) + 1);

  const ordered = [...counts.keys()].sort((a, b) => {
    const ai = STATUS_ORDER.indexOf(a);
    const bi = STATUS_ORDER.indexOf(b);
    return (ai < 0 ? STATUS_ORDER.length : ai) - (bi < 0 ? STATUS_ORDER.length : bi);
  });

  return ordered.map((status) => {
    const meta = statusMeta(status);
    return { status, label: meta.label, tone: meta.tone, count: counts.get(status) ?? 0 };
  });
}

/**
 * A demoted, collapsible summary of where the figures came from.
 *
 * The full per-service provenance ({@link CoveragePanel}) is load-bearing for trust but is not the
 * headline, so it is collapsed by default and kept discoverable behind a status chip row: an
 * at-a-glance count of services per coverage status, plus how fresh the figures are. Expanding
 * reveals the complete panel in place, without leaving the current tab.
 */
export default function DataSourceSummary({ coverage, generatedUtc, expiresUtc, now }: DataSourceSummaryProps) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const panelId = useId();

  const statuses = summariseStatuses(coverage);
  const available = statuses.find((s) => s.status === 'available')?.count ?? 0;
  const total = coverage.length;

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <div className={styles.headText}>
          <Text size={200} weight="semibold" className={styles.title}>
            Where these figures come from
          </Text>
          {total > 0 ? (
            <div className={styles.chips} aria-label={`${available} of ${total} services measured in full`}>
              {statuses.map((s) => (
                <Badge key={s.status} appearance="tint" color={s.tone} size="small">
                  {s.count} {s.label}
                </Badge>
              ))}
              <Text size={200} className={styles.generated}>
                &middot; prepared {formatAge(generatedUtc, now)}
              </Text>
            </div>
          ) : (
            <Text size={200} className={styles.generated}>
              No source information was reported for these figures.
            </Text>
          )}
        </div>
        <Button
          appearance="subtle"
          size="small"
          icon={open ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
          aria-expanded={open}
          aria-controls={panelId}
          onClick={() => setOpen((v) => !v)}
        >
          {open ? 'Hide data sources' : 'Show data sources'}
        </Button>
      </div>

      {open && (
        <div id={panelId} className={styles.panelWrap}>
          <CoveragePanel generatedUtc={generatedUtc} expiresUtc={expiresUtc} coverage={coverage} now={now} />
        </div>
      )}
    </Card>
  );
}
