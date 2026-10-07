import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type {
  AdoptionCombinedSegmentRow,
  AdoptionConcentrationBand,
  CopilotAdoptionSummary,
} from '../../types/copilotAdoption';
import { formatNumber, plural, useT, type TFunction, type TranslationKey } from '../../i18n';
import { concentrationLabel } from './serverText';
import { formatCount, formatPct } from '../shared/KpiGrid';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import { useAdoptionTableStyles } from './adoptionShared';

/**
 * An agent counts as having spread beyond its home team once people in this many departments use it (#647).
 * Mirrors `CopilotAdoptionService.AgentReachDepartmentThreshold`; a test reads the C# so the two cannot drift.
 */
export const AGENT_REACH_DEPARTMENT_THRESHOLD = 3;

/**
 * Catalog entries for the agent scopes the server can name in `agentFiguresScope` - the C#
 * `CopilotAgentFigureScope` constants. A test reads the C# so a new scope cannot ship unworded.
 */
export const AGENT_FIGURES_SCOPE_KEYS: Record<string, TranslationKey> = {
  allAgents: 'copilotAdoption.combinedViews.agents.scope.allAgents',
  customerBuiltAgents: 'copilotAdoption.combinedViews.agents.scope.customerBuiltAgents',
};

/** Which agents the breadth, depth and reach figures count, in the reader's language. */
export function agentFiguresScopeText(t: TFunction, scope: string | null | undefined): string {
  const key = AGENT_FIGURES_SCOPE_KEYS[scope ?? 'allAgents'];
  return key ? t(key) : String(scope);
}

/** The tenant-level agent breadth, depth, builder and reach figures (#646, #647) the department card states. */
export type AgentAdoptionTotals = Pick<
  CopilotAdoptionSummary,
  | 'agentFiguresScope'
  | 'agentBreadthDepartments'
  | 'agentBreadthDepartmentsWithAgentUsers'
  | 'agentBreadthDepartmentPct'
  | 'agentActiveUsers'
  | 'agentBreadthAgentUsers'
  | 'agentBreadthUserPct'
  | 'agentDepthAgentsPer100ActiveUsers'
  | 'agentDepthInteractionsPerActiveAgent'
  | 'agentUnknownOriginAgents'
  | 'agentBuilders'
  | 'agentsInThreeOrMoreDepartments'
  | 'agentsInThreeOrMoreDepartmentsUnknownOrigin'
>;

const NOT_MEASURED = '\u2014';

/** A one-decimal figure in the reader's number format, or a dash when it was not measured. */
function formatTenth(value: number | null | undefined): string {
  return value === null || value === undefined
    ? NOT_MEASURED
    : formatNumber(value, { minimumFractionDigits: 0, maximumFractionDigits: 1 });
}

function formatOptionalCount(value: number | null | undefined): string {
  return value === null || value === undefined ? NOT_MEASURED : formatCount(value);
}

/** Heaviest cohort darkest, so the shape of the power law reads left to right. */
const COHORT_COLOUR = ['#0b3d6b', '#1f6cb0', '#5b9bd5', '#c7dbef'];

/** Ends of the heat-table colour ramp, as the fraction of the hue mixed onto the page background. */
const RAMP_MIN_ALPHA = 0.12;
const RAMP_MAX_ALPHA = 0.65;

/** `#rrggbb` to an sRGB triple. */
function hexToRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.replace('#', ''), 16);
  return [(n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff];
}

/**
 * Mixes `hex` into the (white) page background at `alpha`, returning a solid sRGB triple.
 *
 * Heat ramps are built this way instead of with CSS `opacity` because opacity is applied to the
 * whole element - including the number printed on it - so the low end of the ramp fades the value
 * out of legibility.
 */
function blendOnWhite(hex: string, alpha: number): [number, number, number] {
  return hexToRgb(hex).map((channel) => Math.round(255 + alpha * (channel - 255))) as [number, number, number];
}

/**
 * White or near-black text, whichever has more contrast on the given fill.
 *
 * The crossover is where WCAG contrast against white equals contrast against Fluent's light-theme
 * body colour (#242424), which lands at a relative luminance of ~0.218.
 */
function readableForeground(r: number, g: number, b: number): string {
  const toLinear = (channel: number) => {
    const s = channel / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  const luminance = 0.2126 * toLinear(r) + 0.7152 * toLinear(g) + 0.0722 * toLinear(b);
  return luminance < 0.218 ? '#ffffff' : tokens.colorNeutralForeground1;
}

const useStyles = makeStyles({
  bar: {
    display: 'flex',
    width: '100%',
    height: '30px',
    borderRadius: tokens.borderRadiusSmall,
    overflow: 'hidden',
    marginTop: '8px',
  },
  slice: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '12px',
    fontVariantNumeric: 'tabular-nums',
    minWidth: '2px',
    // A hairline between slices so two adjacent steps of the ramp stay separable in greyscale, on a
    // projector, or to a reader who cannot distinguish the hues at all.
    borderRightWidth: '1px',
    borderRightStyle: 'solid',
    borderRightColor: '#ffffff',
    ':last-child': {
      borderRightWidth: '0',
    },
  },
  legend: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '14px',
    marginTop: '10px',
  },
  legendItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
  },
  swatch: {
    width: '16px',
    height: '16px',
    borderRadius: '3px',
    display: 'inline-block',
    flexShrink: 0,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
  heat: {
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
    padding: '6px 10px',
    // Matches the shared table cell (see adoptionShared's `td`). This class deliberately does not
    // reuse `table.td` because it carries its own shading, so the size has to be kept in step by
    // hand - without it these two cells render a size larger than the six around them.
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
  },
  agentTotals: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
    gap: '8px 16px',
    marginBottom: '12px',
  },
  agentTotal: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  tableWrap: {
    overflowX: 'auto',
  },
});

/**
 * How concentrated Copilot usage is across the people who use it.
 *
 * The chart that distinguishes a working programme from one propped up by enthusiasts. "40% adoption
 * spread evenly" and "40% adoption where a tenth of them do most of it" produce the same adoption
 * percentage and are completely different situations - the second collapses the moment those people
 * change team, and no headline rate will warn you.
 */
export function ConcentrationBar({ bands }: { bands: AdoptionConcentrationBand[] }) {
  const styles = useStyles();
  const t = useT();

  if (bands.length === 0) {
    return <div className={styles.empty}>{t('copilotAdoption.combinedViews.concentration.empty')}</div>;
  }

  return (
    <div>
      <div className={styles.bar}>
        {bands.map((b, i) => {
          const colour = COHORT_COLOUR[i % COHORT_COLOUR.length];
          const label = concentrationLabel(t, b.label);
          return (
            <div
              key={b.label}
              className={styles.slice}
              style={{
                width: `${Math.max(0.5, b.sharePct)}%`,
                backgroundColor: colour,
                // The ramp runs from near-black to very pale, so a fixed white label is unreadable on
                // the last step or two. Pick the label colour from the fill's luminance instead.
                color: readableForeground(...hexToRgb(colour)),
              }}
              title={t('copilotAdoption.combinedViews.concentration.sliceTitle', {
                label,
                users: formatCount(b.users),
                interactions: formatCount(b.interactions),
                pct: formatPct(b.sharePct),
                perUser: b.interactionsPerUser,
              })}
            >
              {b.sharePct >= 8 ? formatPct(b.sharePct) : ''}
            </div>
          );
        })}
      </div>

      <div className={styles.legend}>
        {bands.map((b, i) => (
          <div key={b.label} className={styles.legendItem}>
            <span
              className={styles.swatch}
              style={{ backgroundColor: COHORT_COLOUR[i % COHORT_COLOUR.length] }}
              aria-hidden="true"
            />
            <Text size={200}>
              <strong>{concentrationLabel(t, b.label)}</strong>{' '}
              <span className={styles.muted}>
                {t('copilotAdoption.combinedViews.concentration.legendDetail', { users: formatCount(b.users), perUser: b.interactionsPerUser })}
              </span>
            </Text>
          </div>
        ))}
      </div>
    </div>
  );
}

/**
 * Licensed and unlicensed Copilot use per department, side by side.
 *
 * The comparison is the whole point. A department with idle seats and heavy unlicensed Chat use is
 * not an adoption problem, it is a seat-allocation problem - and that is invisible in any view that
 * reports one population at a time.
 *
 * The right-hand columns add agent breadth and depth (#646) and Copilot Studio builders (#647) per
 * department, with the tenant-wide figures stated above the table. No new chart (#552).
 */
export function CombinedSegmentTable({
  rows,
  agentTotals,
  minSeatsPerSegment,
}: {
  rows: AdoptionCombinedSegmentRow[];
  agentTotals?: AgentAdoptionTotals;
  minSeatsPerSegment?: number;
}) {
  const styles = useStyles();
  const table = useAdoptionTableStyles();
  const t = useT();

  if (rows.length === 0) {
    return (
      <div className={styles.empty}>
        {t('copilotAdoption.combinedViews.segment.empty')}
      </div>
    );
  }

  const maxLicensed = Math.max(...rows.map((r) => r.interactionsPerLicensedUser), 1);
  const maxUnlicensed = Math.max(...rows.map((r) => r.interactionsPerUnlicensedUser), 1);

  // Conditional shading rather than a bar: this table is read by scanning for the outliers, and a
  // colour ramp finds them faster than eight columns of numbers.
  //
  // The ramp is baked into a solid background colour rather than applied with `opacity`. Element
  // opacity fades the digits along with the fill, so the palest cells became unreadable while the
  // darkest ones were left with dark text on a dark fill.
  //
  // It also stops at RAMP_MAX_ALPHA rather than running to a solid fill. Past roughly two thirds
  // strength these hues stop clearing WCAG AA 4.5:1 against the body text, and flipping the label to
  // white does not help - the flip point is itself the lowest-contrast spot on the whole ramp
  // (~3.9:1). Capping keeps one text colour, stays legible end to end, and still separates the
  // outliers plainly (contrast runs 13:1 at the pale end to ~4.9:1 at the strong end).
  const shade = (value: number, max: number, hue: string) => {
    if (value <= 0) return undefined;
    const alpha = RAMP_MIN_ALPHA + (RAMP_MAX_ALPHA - RAMP_MIN_ALPHA) * (value / max);
    const [r, g, b] = blendOnWhite(hue, alpha);
    return {
      backgroundColor: `rgb(${r}, ${g}, ${b})`,
      color: tokens.colorNeutralForeground1,
    };
  };

  return (
    <>
      {agentTotals && <AgentTotals totals={agentTotals} minSeatsPerSegment={minSeatsPerSegment} />}
      <div className={styles.tableWrap}>
        <table className={table.table}>
          <thead>
            <tr>
              <th className={table.th}>{t('copilotAdoption.combinedViews.segment.department')}</th>
              <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoption.combinedViews.segment.licences')}</th>
              <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoption.combinedViews.segment.activeLicences')}</th>
              <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoption.combinedViews.segment.interactionsPerLicence')}</th>
              <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoption.combinedViews.segment.licencesUsingAgents')}</th>
              <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoption.combinedViews.segment.unlicensedUsers')}</th>
              <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoption.combinedViews.segment.interactionsPerUnlicensedUser')}</th>
              <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoption.combinedViews.segment.unlicensedUsingAgents')}</th>
              <th className={`${table.th} ${table.thNumeric}`} title={t('copilotAdoption.combinedViews.segment.usingAgents.help')}>{t('copilotAdoption.combinedViews.segment.usingAgents')}</th>
              <th className={`${table.th} ${table.thNumeric}`} title={t('copilotAdoption.combinedViews.segment.distinctAgents.help')}>{t('copilotAdoption.combinedViews.segment.distinctAgents')}</th>
              <th className={`${table.th} ${table.thNumeric}`} title={t('copilotAdoption.combinedViews.segment.agentsPer100.help')}>{t('copilotAdoption.combinedViews.segment.agentsPer100')}</th>
              <th className={`${table.th} ${table.thNumeric}`} title={t('copilotAdoption.combinedViews.segment.interactionsPerAgent.help')}>{t('copilotAdoption.combinedViews.segment.interactionsPerAgent')}</th>
              <th className={`${table.th} ${table.thNumeric}`} title={t('copilotAdoption.combinedViews.segment.agentBuilders.help')}>{t('copilotAdoption.combinedViews.segment.agentBuilders')}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.segment}>
                <td className={table.td}>{serverPlaceholderText(t, r.segment)}</td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(r.licensedUsers)}</td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(r.licensedActiveUsers)}</td>
                <td className={styles.heat}>
                  <span
                    style={{
                      ...shade(r.interactionsPerLicensedUser, maxLicensed, '#0f6cbd'),
                      padding: '2px 6px',
                      borderRadius: '3px',
                    }}
                  >
                    {r.interactionsPerLicensedUser}
                  </span>
                </td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatPct(r.licensedAgentUserPct)}</td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(r.unlicensedActiveUsers)}</td>
                <td className={styles.heat}>
                  <span
                    style={{
                      ...shade(r.interactionsPerUnlicensedUser, maxUnlicensed, '#a4373a'),
                      padding: '2px 6px',
                      borderRadius: '3px',
                    }}
                  >
                    {r.interactionsPerUnlicensedUser}
                  </span>
                </td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatPct(r.unlicensedAgentUserPct)}</td>
                <td
                  className={`${table.td} ${table.tdNumeric}`}
                  title={
                    r.agentUsers === null || r.agentUsers === undefined || r.agentActiveUsers === null || r.agentActiveUsers === undefined
                      ? t('copilotAdoption.combinedViews.segment.notMeasured')
                      : t('copilotAdoption.combinedViews.segment.usingAgents.cell', {
                          users: formatCount(r.agentUsers),
                          active: formatCount(r.agentActiveUsers),
                        })
                  }
                >
                  {r.agentUserPct === null || r.agentUserPct === undefined ? NOT_MEASURED : formatPct(r.agentUserPct)}
                </td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatOptionalCount(r.distinctAgents)}</td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatTenth(r.agentsPer100ActiveUsers)}</td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatTenth(r.interactionsPerActiveAgent)}</td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatOptionalCount(r.agentBuilders)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

/**
 * The tenant-wide agent breadth, depth, builder and reach figures (#646, #647), above the department
 * table they summarise. Figures, not a chart (#552); a dash marks one that could not be measured.
 */
function AgentTotals({ totals, minSeatsPerSegment }: { totals: AgentAdoptionTotals; minSeatsPerSegment?: number }) {
  const styles = useStyles();
  const t = useT();

  const share = (
    count: number | null | undefined,
    total: number | null | undefined,
    pct: number | null | undefined,
  ): string =>
    count === null || count === undefined || total === null || total === undefined
      ? NOT_MEASURED
      : t('copilotAdoption.combinedViews.agents.ofTotal', {
          count: formatCount(count),
          total: formatCount(total),
          pct: pct === null || pct === undefined ? NOT_MEASURED : formatPct(pct),
        });

  const unknownAgents = totals.agentUnknownOriginAgents ?? 0;
  const unknownReach = totals.agentsInThreeOrMoreDepartmentsUnknownOrigin ?? 0;

  const items: { key: string; label: string; value: string; help: string; note?: string }[] = [
    {
      key: 'departments',
      label: t('copilotAdoption.combinedViews.agents.departments'),
      value: share(totals.agentBreadthDepartmentsWithAgentUsers, totals.agentBreadthDepartments, totals.agentBreadthDepartmentPct),
      help: t('copilotAdoption.combinedViews.agents.departments.help', { min: minSeatsPerSegment ?? 5 }),
    },
    {
      key: 'users',
      label: t('copilotAdoption.combinedViews.agents.users'),
      value: share(totals.agentBreadthAgentUsers, totals.agentActiveUsers, totals.agentBreadthUserPct),
      help: t('copilotAdoption.combinedViews.agents.users.help'),
    },
    {
      key: 'per100',
      label: t('copilotAdoption.combinedViews.agents.per100'),
      value: formatTenth(totals.agentDepthAgentsPer100ActiveUsers),
      help: t('copilotAdoption.combinedViews.agents.per100.help'),
    },
    {
      key: 'perAgent',
      label: t('copilotAdoption.combinedViews.agents.perAgent'),
      value: formatTenth(totals.agentDepthInteractionsPerActiveAgent),
      help: t('copilotAdoption.combinedViews.agents.perAgent.help'),
    },
    {
      key: 'builders',
      label: t('copilotAdoption.combinedViews.agents.builders'),
      value: formatOptionalCount(totals.agentBuilders),
      help: t('copilotAdoption.combinedViews.agents.builders.help'),
    },
    {
      key: 'reach',
      label: t('copilotAdoption.combinedViews.agents.reach', { threshold: AGENT_REACH_DEPARTMENT_THRESHOLD }),
      value: formatOptionalCount(totals.agentsInThreeOrMoreDepartments),
      help: t('copilotAdoption.combinedViews.agents.reach.help', { threshold: AGENT_REACH_DEPARTMENT_THRESHOLD }),
      note: unknownReach > 0
        ? t('copilotAdoption.combinedViews.agents.reachUnknown', { count: formatCount(unknownReach) })
        : undefined,
    },
  ];

  return (
    <div>
      <Text size={300} weight="semibold" block>
        {t('copilotAdoption.combinedViews.agents.title')}
      </Text>
      <Text size={200} block className={styles.muted}>
        {t('copilotAdoption.combinedViews.agents.scope', { scope: agentFiguresScopeText(t, totals.agentFiguresScope) })}
      </Text>
      {unknownAgents > 0 && (
        <Text size={200} block className={styles.muted} data-testid="agent-total-unknown">
          {t(
            plural(unknownAgents, 'copilotAdoption.combinedViews.agents.unknownOrigin.one', 'copilotAdoption.combinedViews.agents.unknownOrigin.other'),
            { count: formatCount(unknownAgents) },
          )}
        </Text>
      )}
      <div className={styles.agentTotals}>
        {items.map((item) => (
          <div key={item.key} className={styles.agentTotal} title={item.help} data-testid={`agent-total-${item.key}`}>
            <Text size={200} className={styles.muted}>{item.label}</Text>
            <Text size={300} weight="semibold">{item.value}</Text>
            {item.note && (
              <Text size={100} className={styles.muted}>{item.note}</Text>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
