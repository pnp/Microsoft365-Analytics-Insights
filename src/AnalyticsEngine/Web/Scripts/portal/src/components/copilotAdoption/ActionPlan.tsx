import { makeStyles, tokens, Text, Badge, Button } from '@fluentui/react-components';
import type { MouseEvent } from 'react';
import type { AdoptionActionSummary } from '../../types/copilotAdoption';
import { formatCount, formatPct } from '../shared/KpiGrid';

/**
 * Action colours run from "this licence is costing money" through to "this licence is paying for itself",
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
  review: '#8a8886',
  excluded: '#605e5c',
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
    gridTemplateColumns: 'minmax(140px, 170px) minmax(60px, auto) 1fr auto',
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
  guidance: {
    marginTop: '4px',
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'baseline',
    gap: '6px',
    color: tokens.colorNeutralForeground3,
  },
  // A bare <a> does not inherit Fluent's Text sizing, so without an explicit size it rendered at the
  // browser default and towered over the label next to it.
  guidanceLink: {
    color: tokens.colorBrandForegroundLink,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '20px 0',
    textAlign: 'center',
  },
  clickable: {
    cursor: 'pointer',
    borderRadius: tokens.borderRadiusMedium,
    paddingTop: '6px',
    paddingBottom: '6px',
    paddingLeft: '6px',
    paddingRight: '6px',
    marginLeft: '-6px',
    marginRight: '-6px',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
    ':focus-visible': {
      outlineWidth: '2px',
      outlineStyle: 'solid',
      outlineColor: tokens.colorStrokeFocus2,
    },
  },
  actionButton: {
    whiteSpace: 'nowrap',
  },
  drill: {
    color: tokens.colorBrandForegroundLink,
    whiteSpace: 'nowrap',
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
  onSelect,
  onCreateIntervention,
}: {
  actions: AdoptionActionSummary[];
  showCounts?: boolean;
  onCreateIntervention?: (code: string) => void;
  /**
   * Drill-through. Without it the plan states "76 people need coaching" and then leaves the reader
   * to rebuild that exact group by hand from the filters on another tab - which is both tedious and
   * a chance to get a different 76.
   */
  onSelect?: (code: string) => void;
}) {
  const styles = useStyles();

  if (actions.length === 0) {
    return <div className={styles.empty}>No licensed users to plan for.</div>;
  }

  return (
    <div className={styles.list}>
      {actions.map((a) => {
        const rowClass = `${showCounts ? styles.row : styles.rowNoCount}${
          onSelect ? ` ${styles.clickable}` : ''
        }`;

        return (
          <div
            key={a.code}
            className={rowClass}
            role={onSelect ? 'button' : undefined}
            tabIndex={onSelect ? 0 : undefined}
            title={onSelect ? `Show the ${formatCount(a.users)} people who need this` : undefined}
            onClick={onSelect ? () => onSelect(a.code) : undefined}
            onKeyDown={
              onSelect
                ? (e: any) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      e.preventDefault();
                      onSelect(a.code);
                    }
                  }
                : undefined
            }
          >
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
              {(a.guidanceLinks?.length ?? 0) > 0 && (
                <span className={styles.guidance}>
                  <Text size={200}>Microsoft&apos;s guidance for this kind of user:</Text>
                  {a.guidanceLinks?.map((link) => (
                    <a
                      key={`${a.code}-${link.url}`}
                      className={styles.guidanceLink}
                      href={link.url}
                      target="_blank"
                      rel="noreferrer"
                      onClick={(e) => e.stopPropagation()}
                      onKeyDown={(e) => e.stopPropagation()}
                    >
                      {link.title}
                    </a>
                  ))}
                </span>
              )}
              {onSelect && (
                <>
                  {' '}
                  <Text size={200} className={styles.drill}>
                    Show these people &rsaquo;
                  </Text>
                </>
              )}
            </Text>
            {onCreateIntervention && (
              <Button
                size="small"
                appearance="secondary"
                className={styles.actionButton}
                onClick={(e: MouseEvent<HTMLButtonElement>) => {
                  e.stopPropagation();
                  onCreateIntervention(a.code);
                }}
              >
                Start intervention
              </Button>
            )}
          </div>
        );
      })}
    </div>
  );
}
