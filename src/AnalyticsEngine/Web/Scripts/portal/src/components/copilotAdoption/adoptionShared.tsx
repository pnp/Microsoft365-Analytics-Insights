import { makeStyles, tokens, Text, Badge, Button } from '@fluentui/react-components';
import { ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons';
import { useCallback, useState } from 'react';
import type { ReactNode } from 'react';
import { AdoptionBand } from '../../types/copilotAdoption';
import type { AdoptionSegmentRow } from '../../types/copilotAdoption';
import { ADOPTION_BANDS } from '../charts/GaugeRing';
import { formatCount, formatPct } from '../shared/KpiGrid';
import InfoTip from '../shared/InfoTip';
import type { InfoTipContent } from '../shared/InfoTip';

/**
 * Band colours run cold-to-warm with maturity, and the two zero-usage bands are deliberately the
 * only red ones - they are the seats that are costing money for nothing.
 */
const BAND_COLOUR: Record<AdoptionBand, string> = {
  [AdoptionBand.NeverUsed]: '#d13438',
  [AdoptionBand.Dormant]: '#ca5010',
  [AdoptionBand.Trialling]: '#c19c00',
  [AdoptionBand.Developing]: '#0f6cbd',
  [AdoptionBand.Established]: '#008272',
  [AdoptionBand.Champion]: '#107c10',
};

/** Band colours in the enum's own order, for charts that colour by band. */
export const BAND_COLOUR_LIST = [
  BAND_COLOUR[AdoptionBand.NeverUsed],
  BAND_COLOUR[AdoptionBand.Dormant],
  BAND_COLOUR[AdoptionBand.Trialling],
  BAND_COLOUR[AdoptionBand.Developing],
  BAND_COLOUR[AdoptionBand.Established],
  BAND_COLOUR[AdoptionBand.Champion],
];

const useStyles = makeStyles({
  badge: {
    color: tokens.colorNeutralForegroundOnBrand,
    whiteSpace: 'nowrap',
  },
  scoreCell: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    minWidth: '108px',
  },
  scoreTrack: {
    position: 'relative',
    flexGrow: 1,
    height: '8px',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
    minWidth: '46px',
  },
  scoreBar: {
    height: '100%',
    borderRadius: tokens.borderRadiusSmall,
  },
  scoreValue: {
    fontVariantNumeric: 'tabular-nums',
    minWidth: '34px',
    textAlign: 'right',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
  },
  th: {
    textAlign: 'left',
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke2,
    color: tokens.colorNeutralForeground3,
    // Pinned to the same size as the <Text size={200}> used inside the cells. Without this the
    // table inherits the provider's base300 size, so a bare value like a row count rendered a
    // size larger than the names and badges sitting next to it in the same row.
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  thNumeric: {
    textAlign: 'right',
  },
  /**
   * A clickable column header. Rendered as a button inside the th so it is keyboard reachable and
   * announced as a control, while the th keeps the `aria-sort` state that screen readers read out.
   */
  sortButton: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    backgroundColor: 'transparent',
    // Longhands throughout - Griffel rejects the shorthands it cannot split reliably.
    borderTopStyle: 'none',
    borderRightStyle: 'none',
    borderBottomStyle: 'none',
    borderLeftStyle: 'none',
    padding: '0',
    margin: '0',
    fontFamily: 'inherit',
    fontSize: 'inherit',
    fontWeight: 'inherit',
    lineHeight: 'inherit',
    color: 'inherit',
    cursor: 'pointer',
    ':hover': {
      color: tokens.colorNeutralForeground1,
      textDecorationLine: 'underline',
    },
    ':focus-visible': {
      outlineWidth: '2px',
      outlineStyle: 'solid',
      outlineColor: tokens.colorStrokeFocus2,
      outlineOffset: '2px',
      borderRadius: tokens.borderRadiusSmall,
    },
  },
  /** The direction arrow. Reserved width so headers do not shift as the sort moves between columns. */
  sortArrow: {
    width: '10px',
    fontSize: '9px',
    lineHeight: '1',
  },
  /**
   * Wraps the sort button and the column's "i" as SIBLINGS.
   *
   * They used to be nested - the `InfoTip` was passed in as `children` of the sort button - which is
   * invalid HTML (a button inside a button) and meant the info icon could not be clicked at all: the
   * click bubbled to the sort handler and re-sorted the table instead of opening the definition. On
   * a page whose whole premise is "every figure carries its definition", an unreachable definition is
   * a real defect, not a nicety.
   */
  thContent: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '2px',
  },
  sortArrowActive: {
    color: tokens.colorBrandForeground1,
  },
  sortArrowIdle: {
    // Faint rather than hidden: the arrow is the affordance that says the header is clickable, but
    // eleven of them at full strength would compete with the data.
    opacity: 0.3,
  },
  /**
   * Pins a column to the right-hand edge of a horizontally scrolling table.
   *
   * The licensed-user table has twelve columns and overflows on a normal laptop, which put the
   * recommended Action - the column the whole page exists to produce - off the right edge, reachable
   * only by scrolling. It is worth more than the columns it now floats above.
   *
   * Needs an opaque background: the cells it overlaps scroll underneath it.
   */
  stickyRight: {
    position: 'sticky',
    right: '0',
    zIndex: 1,
    backgroundColor: tokens.colorNeutralBackground1,
    // A hairline so the pinned column reads as pinned rather than as an overlap artefact once the
    // table is actually scrolled.
    borderLeftWidth: '1px',
    borderLeftStyle: 'solid',
    borderLeftColor: tokens.colorNeutralStroke2,
    // The shadow matters as much as the hairline: without it the column it floats over looks
    // truncated rather than scrolled-under, which reads as a rendering bug.
    boxShadow: `-6px 0 6px -6px ${tokens.colorNeutralShadowAmbient}`,
  },
  /**
   * Pins a column to the LEFT-hand edge of a horizontally scrolling table.
   *
   * These tables are wider than a laptop screen, so reading a column on the right means scrolling the
   * user's own name off the left - at which point the row being read is anonymous. Pinning the
   * identity column keeps "who is this about?" answerable at every scroll position, which is the one
   * thing a reader needs at all times.
   *
   * Needs an opaque background for the same reason as `stickyRight`: the cells it overlaps scroll
   * underneath it.
   */
  stickyLeft: {
    position: 'sticky',
    left: '0',
    zIndex: 1,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRightWidth: '1px',
    borderRightStyle: 'solid',
    borderRightColor: tokens.colorNeutralStroke2,
    boxShadow: `6px 0 6px -6px ${tokens.colorNeutralShadowAmbient}`,
  },
  /**
   * Keeps a value cell on one line.
   *
   * Applied per column rather than to every `td`, because the same table also carries prose columns
   * that genuinely have to wrap. A date, a badge or a count that wraps sets the height of the whole
   * row for no benefit.
   */
  tdNoWrap: {
    whiteSpace: 'nowrap',
  },
  td: {
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
    verticalAlign: 'middle',
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  tdNumeric: {
    textAlign: 'right',
    fontVariantNumeric: 'tabular-nums',
  },
  /**
   * The smaller second line inside a cell - "0 mtgs" under the Teams count, "12 licensed" under the
   * user count. Fluent's Text hard-codes `text-align: start`, so without the explicit `inherit` the
   * sub-line left-aligns inside a right-aligned numeric cell and the two halves of a single value
   * end up at opposite ends of the column.
   */
  tdSub: {
    color: tokens.colorNeutralForeground3,
    textAlign: 'inherit',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '20px 0',
    textAlign: 'center',
  },
  /** The identity cell, with its expand chevron sitting to the left of the name stack. */
  expandableUser: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '2px',
  },
  /** Name over job title, so the identity cell stays one column wide however long the UPN is. */
  upnStack: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: '0',
  },
  userSecondary: {
    color: tokens.colorNeutralForeground3,
  },
  expandToggle: {
    minWidth: '20px',
    width: '20px',
    height: '20px',
    padding: 0,
    marginTop: '1px',
    color: tokens.colorNeutralForeground3,
  },
  detailRow: {
    backgroundColor: tokens.colorNeutralBackground2,
  },
  detailCell: {
    padding: '0',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke2,
  },
  /**
   * Pins the detail panel to the left edge of the scroll container.
   *
   * The cell it sits in spans every column, so it is as wide as the whole table - which on these
   * tables is wider than the screen. Left as a normal block it would scroll away with the columns,
   * which is the exact problem the expander exists to solve, so it is given a bounded width and made
   * sticky: wherever the table is scrolled to, the detail is on screen.
   */
  detailInner: {
    position: 'sticky',
    left: '0',
    width: 'min(1040px, calc(100vw - 96px))',
    boxSizing: 'border-box',
    padding: '12px 14px 14px 34px',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  detailSections: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))',
    gap: '12px 20px',
  },
  detailSectionTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: '2px',
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    marginBottom: '4px',
  },
  detailStats: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(104px, 1fr))',
    gap: '8px 12px',
  },
  detailStat: {
    display: 'flex',
    flexDirection: 'column',
  },
  detailStatLabel: {
    color: tokens.colorNeutralForeground3,
  },
  detailStatValue: {
    fontVariantNumeric: 'tabular-nums',
  },
  detailStatSub: {
    color: tokens.colorNeutralForeground3,
  },
  /** The justification, in full. Nothing is clamped here - that is the point of the expander. */
  detailProse: {
    color: tokens.colorNeutralForeground2,
    maxWidth: '900px',
  },
});

/** The engagement band as a coloured pill. */
export function BandBadge({ band, name }: { band: AdoptionBand; name: string }) {
  const styles = useStyles();
  return (
    <Badge className={styles.badge} style={{ backgroundColor: BAND_COLOUR[band] ?? '#605e5c' }} size="small">
      {name}
    </Badge>
  );
}

/**
 * A 0-100 score as a small inline bar plus the number.
 *
 * The bar matters: a column of bare numbers makes an executive read every row, whereas a column of
 * bars makes the shape of the problem visible in one glance - which is the whole reason this list
 * exists rather than a raw data dump.
 */
export function ScoreBar({ score, colour }: { score: number; colour?: string }) {
  const styles = useStyles();
  const clamped = Math.max(0, Math.min(100, score));

  return (
    <div className={styles.scoreCell}>
      <div className={styles.scoreTrack}>
        <div
          className={styles.scoreBar}
          style={{ width: `${clamped}%`, backgroundColor: colour ?? scoreColour(clamped) }}
        />
      </div>
      <Text size={200} weight="semibold" className={styles.scoreValue}>
        {Math.round(clamped)}
      </Text>
    </div>
  );
}

/**
 * Colour for an adoption *rate* (a percentage of people), as opposed to an engagement *score*.
 *
 * These are different measures and must not share a threshold set: 50 on the engagement scale means
 * "habit formed", whereas 50% adoption means half the seats are idle. Rates use the same three-band
 * judgement scale as the headline gauges, so a department bar and the gauge above it agree.
 */
export function rateColour(ratePct: number): string {
  const band = ADOPTION_BANDS.find((b) => ratePct <= b.upTo) ?? ADOPTION_BANDS[ADOPTION_BANDS.length - 1];
  return band.colour;
}

/**
 * Score-to-colour, using the same thresholds as the engagement bands so the two never disagree.
 *
 * The thresholds are passed in wherever the caller has the options the analysis actually ran with -
 * a deployment that tunes the bands must not be shown a chart coloured by the shipped defaults.
 */
export function scoreColour(
  score: number,
  bands?: { champion: number; established: number; developing: number },
): string {
  const champion = bands?.champion ?? 75;
  const established = bands?.established ?? 50;
  const developing = bands?.developing ?? 25;

  if (score >= champion) return BAND_COLOUR[AdoptionBand.Champion];
  if (score >= established) return BAND_COLOUR[AdoptionBand.Established];
  if (score >= developing) return BAND_COLOUR[AdoptionBand.Developing];
  if (score > 0) return BAND_COLOUR[AdoptionBand.Trialling];
  return BAND_COLOUR[AdoptionBand.NeverUsed];
}

/**
 * Adoption per department or country, worst first.
 *
 * Shows the seat count next to the percentage on purpose: "0% adopted" across six seats and across
 * six hundred are the same percentage and completely different decisions, and a chart that shows
 * only the rate invites the wrong one.
 */
export function SegmentTable({
  rows,
  segmentLabel,
  bands,
}: {
  rows: AdoptionSegmentRow[];
  segmentLabel: string;
  /** The tuned band thresholds, so this table colours by the same rules as the rest of the page. */
  bands?: { champion: number; established: number; developing: number };
}) {
  const styles = useStyles();

  if (rows.length === 0) {
    return (
      <div className={styles.empty}>
        Not enough licensed users in any {segmentLabel.toLowerCase()} to break down reliably.
      </div>
    );
  }

  return (
    <table className={styles.table}>
      <thead>
        <tr>
          <th className={styles.th}>{segmentLabel}</th>
          <th className={`${styles.th} ${styles.thNumeric}`}>Licences</th>
          <th className={`${styles.th} ${styles.thNumeric}`}>Active</th>
          <th className={`${styles.th} ${styles.thNumeric}`}>Habitual</th>
          <th className={`${styles.th} ${styles.thNumeric}`}>Never used</th>
          <th className={styles.th}>Adoption rate</th>
          <th className={styles.th}>Avg. score</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row.segment}>
            <td className={styles.td}>{row.segment}</td>
            <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(row.licensedUsers)}</td>
            <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(row.activeUsers)}</td>
            <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(row.habitualUsers)}</td>
            <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(row.neverUsedUsers)}</td>
            <td className={styles.td}>
              <div className={styles.scoreCell}>
                <div className={styles.scoreTrack}>
                  <div
                    className={styles.scoreBar}
                    style={{
                      width: `${Math.max(0, Math.min(100, row.adoptionRatePct))}%`,
                      // An adoption RATE, not an engagement score - so it is coloured on the
                      // adoption band scale used by the gauges, not on the engagement bands. The two
                      // are different measures and sharing one threshold set would be a category error.
                      backgroundColor: rateColour(row.adoptionRatePct),
                    }}
                  />
                </div>
                <Text size={200} weight="semibold" className={styles.scoreValue}>
                  {formatPct(row.adoptionRatePct)}
                </Text>
              </div>
            </td>
            <td className={styles.td}>
              <ScoreBar score={row.averageAdoptionScore} colour={scoreColour(row.averageAdoptionScore, bands)} />
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** Shared table styling for the two user lists, so they look and behave identically. */
export function useAdoptionTableStyles() {
  return useStyles();
}

/**
 * Per-row expansion state for the seat-holder tables.
 *
 * Keyed by user id rather than by row index, so a re-sort does not leave a different person's
 * detail open. `collapseAll` exists because paging or re-filtering replaces the rows underneath an
 * open detail, and an expander left open over an unrelated person is worse than one that closed.
 */
export function useRowExpansion() {
  const [expanded, setExpanded] = useState<ReadonlySet<number>>(() => new Set<number>());

  const toggle = useCallback((id: number) => {
    setExpanded((previous) => {
      const next = new Set(previous);
      if (!next.delete(id)) {
        next.add(id);
      }
      return next;
    });
  }, []);

  const collapseAll = useCallback(() => setExpanded(new Set<number>()), []);

  return { isExpanded: (id: number) => expanded.has(id), toggle, collapseAll };
}

/**
 * The identity cell, carrying the row's expand control.
 *
 * The chevron lives in this cell rather than in a column of its own because this is the column that
 * is pinned to the left edge. A separate leading column would either have to be pinned too - which
 * means hard-coding its width into the pinned offset of this one - or would scroll out of reach on
 * exactly the wide tables the expander is there to rescue.
 */
export function ExpandableUserCell({
  open,
  onToggle,
  userPrincipalName,
  secondary,
  className,
}: {
  open: boolean;
  onToggle: () => void;
  userPrincipalName: string;
  secondary?: string | null;
  className?: string;
}) {
  const styles = useStyles();
  return (
    <td className={`${styles.td} ${className ?? ''}`}>
      <div className={styles.expandableUser}>
        <Button
          appearance="subtle"
          size="small"
          className={styles.expandToggle}
          icon={open ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
          aria-expanded={open}
          aria-label={`${open ? 'Hide' : 'Show'} the full assessment for ${userPrincipalName}`}
          onClick={onToggle}
        />
        <span className={styles.upnStack}>
          <Text size={200} weight="semibold">
            {userPrincipalName}
          </Text>
          <Text size={100} className={styles.userSecondary}>
            {secondary || ''}
          </Text>
        </span>
      </div>
    </td>
  );
}

/**
 * The expanded detail, as a full-width row beneath its summary row.
 *
 * A second `tr` rather than an overlay or a dialog: the detail stays attached to the row it explains
 * and to the table's own scrolling, and several can be open at once for comparison - which is what
 * this list is for.
 */
export function DetailRow({ colSpan, children }: { colSpan: number; children: ReactNode }) {
  const styles = useStyles();
  return (
    <tr className={styles.detailRow}>
      <td className={styles.detailCell} colSpan={colSpan}>
        <div className={styles.detailInner}>{children}</div>
      </td>
    </tr>
  );
}

/** The grid an expanded row's sections sit in. */
export function DetailSections({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.detailSections}>{children}</div>;
}

/** A labelled group of figures inside an expanded row. */
export function DetailSection({
  title,
  info,
  infoTitle,
  children,
}: {
  title: string;
  info?: InfoTipContent;
  infoTitle?: string;
  children: ReactNode;
}) {
  const styles = useStyles();
  return (
    <div>
      <div className={styles.detailSectionTitle}>
        <Text size={100} weight="semibold">
          {title}
        </Text>
        {info && <InfoTip title={infoTitle ?? title} content={info} />}
      </div>
      {children}
    </div>
  );
}

/** The grid a `DetailSection`'s figures sit in. */
export function DetailStats({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.detailStats}>{children}</div>;
}

/**
 * One figure in an expanded row.
 *
 * `sub` carries the qualifier that makes the number honest - the component score behind a raw
 * count, or the target it was measured against - because these numbers exist to show the working,
 * and a bare "57" shows none of it.
 */
export function DetailStat({ label, value, sub }: { label: string; value: ReactNode; sub?: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.detailStat}>
      <Text size={100} className={styles.detailStatLabel}>
        {label}
      </Text>
      <Text size={300} weight="semibold" className={styles.detailStatValue}>
        {value}
      </Text>
      {sub && (
        <Text size={100} className={styles.detailStatSub}>
          {sub}
        </Text>
      )}
    </div>
  );
}

/**
 * The per-user justification, in full.
 *
 * This used to be a column, clamped to two lines with the rest on hover. In a table already wider
 * than the screen it was both unreadable and, on the widest tables, scrolled off the right edge
 * entirely - so the sentence written specifically to be read was the one thing you could not read.
 * Here it has the width to be a sentence.
 */
export function DetailRationale({ text }: { text: string }) {
  const styles = useStyles();
  return (
    <Text size={200} className={styles.detailProse}>
      {text || '\u2014'}
    </Text>
  );
}

/**
 * A sortable column header, optionally carrying the column's definition.
 *
 * Clicking selects the column; clicking the column that is already selected reverses it. The first
 * click uses `defaultDescending`, because the useful first answer differs per column: "most
 * interactions" but "A-Z" for a name, and starting a numeric column ascending shows the reader a
 * screen of zeroes.
 *
 * `aria-sort` lives on the `th` (that is where assistive technology looks for it) while the control
 * itself is a real `button`, so the header is reachable and operable from the keyboard.
 *
 * The definition is passed as `info` rather than rendered into `children` ON PURPOSE. An `InfoTip`
 * is itself a button, so putting one inside the sort button produced nested buttons: the browser
 * hoisted the inner one out of the header, and every attempt to open the definition landed on the
 * sort handler and re-sorted the table instead. Kept as siblings, both are clickable and both are
 * separate tab stops.
 */
export function SortableTh({
  label,
  sortKey,
  activeKey,
  descending,
  onSort,
  numeric = false,
  defaultDescending = false,
  className,
  info,
  infoTitle,
  children,
}: {
  /** Accessible name for the sort control. Use `children` to render something richer. */
  label: string;
  sortKey: string;
  activeKey: string;
  descending: boolean;
  onSort: (key: string, descending: boolean) => void;
  numeric?: boolean;
  /** Direction applied when this column is selected from cold. */
  defaultDescending?: boolean;
  className?: string;
  /** The column's definition, shown by an "i" that sits OUTSIDE the sort button. */
  info?: InfoTipContent;
  /** Heading for the definition popover. Defaults to the column's own label. */
  infoTitle?: string;
  children?: ReactNode;
}) {
  const styles = useStyles();
  const active = activeKey === sortKey;
  const ariaSort: 'ascending' | 'descending' | 'none' = active
    ? descending
      ? 'descending'
      : 'ascending'
    : 'none';

  return (
    <th
      className={`${styles.th}${numeric ? ` ${styles.thNumeric}` : ''}${className ? ` ${className}` : ''}`}
      aria-sort={ariaSort}
    >
      <span className={styles.thContent}>
        <button
          type="button"
          className={styles.sortButton}
          onClick={() => onSort(sortKey, active ? !descending : defaultDescending)}
          title={`Sort by ${label}`}
        >
          {children ?? label}
          <span
            className={`${styles.sortArrow} ${active ? styles.sortArrowActive : styles.sortArrowIdle}`}
            aria-hidden="true"
          >
            {active ? (descending ? '\u25BC' : '\u25B2') : '\u25B2'}
          </span>
        </button>
        {info && <InfoTip title={infoTitle ?? label} content={info} />}
      </span>
    </th>
  );
}
