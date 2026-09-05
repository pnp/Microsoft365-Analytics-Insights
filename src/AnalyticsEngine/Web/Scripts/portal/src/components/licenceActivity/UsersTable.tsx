import { Fragment, useState } from 'react';
import { makeStyles, tokens, Text, Badge, Button, Tooltip } from '@fluentui/react-components';
import { ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons';
import type { LicenceActivityEvidence, LicenceActivityUser, WorkloadKey } from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';
import { BAND_METHOD, bandColour, bandForeground, bandLabel, frequencyPct } from './bands';
import { DASH, UNKNOWN_TEXT, formatAge, formatDate } from './format';
import { statusMeta } from './statuses';
import { useLaTableStyles } from './tableStyles';

const useStyles = makeStyles({
  user: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: '180px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  disabled: {
    color: tokens.colorPaletteRedForeground1,
  },
  reason: {
    color: tokens.colorNeutralForeground3,
  },
  rank: {
    color: tokens.colorNeutralForeground3,
    fontVariantNumeric: 'tabular-nums',
    width: '36px',
  },
  bandBadge: {
    whiteSpace: 'nowrap',
  },
  toggle: {
    minWidth: '24px',
    width: '24px',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '18px 0',
    textAlign: 'center',
  },
  detail: {
    backgroundColor: tokens.colorNeutralBackground2,
  },
  detailInner: {
    padding: '8px 12px',
  },
  detailTitle: {
    color: tokens.colorNeutralForeground2,
    marginBottom: '6px',
  },
  detailTable: {
    width: '100%',
    borderCollapse: 'collapse',
  },
  detailCell: {
    padding: '4px 8px',
    verticalAlign: 'top',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
  },
  detailHead: {
    textAlign: 'left',
    padding: '4px 8px',
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
});

function evidenceFor(user: LicenceActivityUser, workload: WorkloadKey | string): LicenceActivityEvidence | null {
  return user.workloads.find((w) => w.workload === workload) ?? null;
}

function isUnknown(ev: LicenceActivityEvidence | null): boolean {
  // The band is authoritative (LicenceActivityRules.Band returns "unknown" for incomplete coverage).
  return !ev || ev.band === 'unknown';
}

/** An activity average to one decimal, or the unknown marker when not measured (never 0). */
function formatAverage(value: number | null | undefined): string {
  if (value == null) return UNKNOWN_TEXT;
  return (Math.round(value * 10) / 10).toLocaleString();
}

/** Active/observed samples with expected context, distinguishing "nothing observed" from a real 0. */
function formatSamples(ev: LicenceActivityEvidence | null): string {
  if (!ev) return UNKNOWN_TEXT;
  if (ev.observedSamples <= 0) return `${DASH}; ${ev.observedSamples} observed of ${ev.expectedSamples} expected`;
  const base = `${ev.activeSamples} / ${ev.observedSamples}`;
  return ev.observedSamples !== ev.expectedSamples ? `${base} of ${ev.expectedSamples}` : base;
}

/** A band badge - grey "Unknown" when incomplete, coloured otherwise. `showReason` adds the coverage
 *  reason under an unknown badge (used in the main row; off in the detail, which has a status column). */
function BandCell({ ev, showReason = true }: { ev: LicenceActivityEvidence | null; showReason?: boolean }) {
  const styles = useStyles();
  const unknown = isUnknown(ev);
  const shown = unknown ? 'unknown' : ev?.band ?? 'unknown';
  // Only surface a reason that ADDS information: a literal "unknown" status would just repeat the badge.
  const reason = showReason && unknown && ev && ev.status && ev.status !== 'unknown' ? statusMeta(ev.status).label : null;
  return (
    <span style={{ display: 'inline-flex', flexDirection: 'column', gap: '2px' }}>
      <Badge
        className={styles.bandBadge}
        size="small"
        style={{ backgroundColor: bandColour(shown), color: bandForeground(shown) }}
      >
        {unknown ? UNKNOWN_TEXT : bandLabel(shown)}
      </Badge>
      {reason && (
        <Text size={100} className={styles.reason}>
          {reason}
        </Text>
      )}
    </span>
  );
}

/** The expandable per-user panel: every workload's evidence, so an admin can inspect the same person
 *  without re-ranking the list by a different workload. */
function AllWorkloadsDetail({ user }: { user: LicenceActivityUser }) {
  const styles = useStyles();
  return (
    <div className={styles.detailInner}>
      <Text size={200} weight="semibold" block className={styles.detailTitle}>
        All workloads for {user.userPrincipalName}
      </Text>
      <table className={styles.detailTable}>
        <thead>
          <tr>
            <th className={styles.detailHead}>Workload</th>
            <th className={styles.detailHead}>Coverage</th>
            <th className={styles.detailHead}>Band</th>
            <th className={styles.detailHead}>Source &middot; Measure</th>
            <th className={styles.detailHead}>Active / observed (expected)</th>
            <th className={styles.detailHead}>Avg</th>
            <th className={styles.detailHead}>Last activity</th>
          </tr>
        </thead>
        <tbody>
          {WORKLOADS.map((w) => {
            const ev = evidenceFor(user, w.key);
            const meta = statusMeta(ev?.status);
            return (
              <tr key={w.key}>
                <td className={styles.detailCell}>
                  <Text size={200} weight="semibold">
                    {w.label}
                  </Text>
                </td>
                <td className={styles.detailCell}>
                  <Text size={200}>{meta.label}</Text>
                  {meta.explanation && (
                    <Text size={100} block className={styles.reason}>
                      {meta.explanation}
                    </Text>
                  )}
                </td>
                <td className={styles.detailCell}>
                  <BandCell ev={ev} showReason={false} />
                </td>
                <td className={styles.detailCell}>
                  <Text size={200}>
                    {ev?.source || DASH}
                    {ev?.measure ? ` \u00b7 ${ev.measure}` : ''}
                  </Text>
                </td>
                <td className={styles.detailCell}>
                  <Text size={200}>{formatSamples(ev)}</Text>
                </td>
                <td className={styles.detailCell}>
                  <Text size={200}>{formatAverage(ev?.averageActions)}</Text>
                </td>
                <td className={styles.detailCell}>
                  <Text size={200}>{formatDate(ev?.lastActivityUtc)}</Text>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

interface UsersTableProps {
  rows: LicenceActivityUser[];
  /** Which workload's evidence to render in the main row for each user. */
  workload: WorkloadKey;
  workloadLabel: string;
  /** Show a leading rank column (used by the most/least highlight lists). */
  showRank?: boolean;
  /** Rank of the first row (1-based). Lets a paged browse continue the numbering across pages. */
  startRank?: number;
  emptyText?: string;
}

/**
 * The drill-down user list. Identifier-first (UPN only - no display names are imported). The main row
 * summarises the SELECTED workload; each row expands to show ALL FIVE workloads' evidence (status,
 * source, measure, active/observed/expected samples, average and last activity), so the same person
 * can be inspected across workloads without re-ranking the list.
 *
 * Every figure is unknown-aware: an incomplete-coverage band shows "Unknown" with the reason, and
 * "nothing observed" is a dash rather than a zero that would read as measured inactivity.
 */
export default function UsersTable({
  rows,
  workload,
  workloadLabel,
  showRank,
  startRank = 1,
  emptyText = 'No users match this selection.',
}: UsersTableProps) {
  const styles = useStyles();
  const table = useLaTableStyles();
  const [expanded, setExpanded] = useState<Set<number>>(new Set());

  const toggle = (userId: number): void =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(userId)) next.delete(userId);
      else next.add(userId);
      return next;
    });

  if (rows.length === 0) {
    return <div className={styles.empty}>{emptyText}</div>;
  }

  const colSpan = 7 + (showRank ? 1 : 0);

  return (
    <div className={table.wrap}>
      <table className={table.table}>
        <thead>
          <tr>
            <th className={table.th} aria-label="Expand" />
            {showRank && <th className={`${table.th} ${table.thNumeric}`}>#</th>}
            <th className={table.th}>User</th>
            <th className={table.th}>Department</th>
            <th className={table.th}>
              <Tooltip relationship="description" content={BAND_METHOD}>
                <span style={{ borderBottom: `1px dotted ${tokens.colorNeutralForeground4}`, cursor: 'help' }}>
                  {workloadLabel} band
                </span>
              </Tooltip>
            </th>
            <th className={`${table.th} ${table.thNumeric}`}>Avg actions</th>
            <th className={`${table.th} ${table.thNumeric}`}>Active samples</th>
            <th className={table.th}>Last activity</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row, index) => {
            const ev = evidenceFor(row, workload);
            const open = expanded.has(row.userId);
            return (
              <Fragment key={row.userId}>
                <tr>
                  <td className={table.td}>
                    <Button
                      className={styles.toggle}
                      appearance="subtle"
                      size="small"
                      icon={open ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
                      aria-expanded={open}
                      aria-label={`Show all workloads for ${row.userPrincipalName}`}
                      onClick={() => toggle(row.userId)}
                    />
                  </td>
                  {showRank && (
                    <td className={`${table.td} ${table.tdNumeric} ${styles.rank}`}>{startRank + index}</td>
                  )}
                  <td className={table.td}>
                    <span className={styles.user}>
                      <Text size={200} weight="semibold">
                        {row.userPrincipalName}
                      </Text>
                      {row.accountEnabled === false && (
                        <Text size={100} className={styles.disabled}>
                          Account disabled
                        </Text>
                      )}
                    </span>
                  </td>
                  <td className={table.td}>{row.department || DASH}</td>
                  <td className={table.td}>
                    <BandCell ev={ev} />
                  </td>
                  <td className={`${table.td} ${table.tdNumeric}`}>{formatAverage(ev?.averageActions)}</td>
                  <td className={`${table.td} ${table.tdNumeric}`}>
                    {(() => {
                      const freq = ev && ev.observedSamples > 0 ? frequencyPct(ev.activeSamples, ev.observedSamples) : null;
                      return (
                        <>
                          {formatSamples(ev)}
                          {freq != null && <span className={styles.muted}> ({Math.round(freq)}%)</span>}
                        </>
                      );
                    })()}
                  </td>
                  <td className={table.td}>
                    {formatDate(ev?.lastActivityUtc)}
                    {ev?.lastActivityUtc && (
                      <Text size={100} block className={styles.muted}>
                        {formatAge(ev.lastActivityUtc)}
                      </Text>
                    )}
                  </td>
                </tr>
                {open && (
                  <tr className={styles.detail}>
                    <td className={table.td} colSpan={colSpan}>
                      <AllWorkloadsDetail user={row} />
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
