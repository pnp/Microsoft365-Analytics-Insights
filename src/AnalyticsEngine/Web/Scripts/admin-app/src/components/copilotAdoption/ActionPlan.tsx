import { makeStyles, tokens, Text, Badge } from '@fluentui/react-components';
import type { AdoptionActionSummary } from '../../types/copilotAdoption';
import { formatCount, formatPct } from './KpiGrid';

/**
 * Action colours run from "this seat is costing money" through to "this seat is paying for itself",
 * matching the engagement-band palette so the two views never appear to disagree.
 */
export const ACTION_COLOUR: Record<string, string> = {
  reclaim: '#d13438',
  reengage: '#ca5010',
  coach: '#c19c00',
  broaden: '#0f6cbd',
  grow: '#2b7cc4',
  sustain: '#008272',
  advocate: '#107c10',
};

const useStyles = makeStyles({
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    paddingTop: '4px',
  },
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(140px, 170px) minmax(60px, auto) 1fr',
    gap: '12px',
    alignItems: 'start',
  },
  rowNoCount: {
    display: 'grid',
    gridTemplateColumns: 'minmax(140px, 170px) 1fr',
    gap: '12px',
    alignItems: 'start',
  },
  badge: {
    color: '#ffffff',
    whiteSpace: 'nowrap',
  },
  count: {
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
  },
  share: {
    color: tokens.colorNeutralForeground3,
  },
  description: {
    color: tokens.colorNeutralForeground2,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '20px 0',
    textAlign: 'center',
  },
});

/** The action badge used in the user list, so the tag and the plan always use one palette. */
export function ActionBadge({ code, label }: { code: string; label: string }) {
  const styles = useStyles();
  return (
    <Badge className={styles.badge} style={{ backgroundColor: ACTION_COLOUR[code] ?? '#605e5c' }} size="small">
      {label}
    </Badge>
  );
}

/**
 * The enablement plan: each recommended action stated once, with how many people need it.
 *
 * Replaces what used to be a per-row "Recommended action" column repeating the identical paragraph
 * for every user in a band. That column was a hundred copies of one sentence dressed up as a
 * hundred findings; the useful version of the same information is the size of each job, which is
 * what an admin takes to a department lead. The per-user list keeps a two-word tag, and the CSV
 * keeps the full sentence on every row where the repetition genuinely helps.
 */
export default function ActionPlan({
  actions,
  showCounts = true,
}: {
  actions: AdoptionActionSummary[];
  showCounts?: boolean;
}) {
  const styles = useStyles();

  if (actions.length === 0) {
    return <div className={styles.empty}>No licensed users to plan for.</div>;
  }

  return (
    <div className={styles.list}>
      {actions.map((a) => (
        <div key={a.code} className={showCounts ? styles.row : styles.rowNoCount}>
          <ActionBadge code={a.code} label={a.label} />
          {showCounts && (
            <Text size={300} weight="semibold" className={styles.count}>
              {formatCount(a.users)}{' '}
              <Text size={200} className={styles.share}>
                ({formatPct(a.sharePct)})
              </Text>
            </Text>
          )}
          <Text size={200} className={styles.description}>
            {a.description}
          </Text>
        </div>
      ))}
    </div>
  );
}
