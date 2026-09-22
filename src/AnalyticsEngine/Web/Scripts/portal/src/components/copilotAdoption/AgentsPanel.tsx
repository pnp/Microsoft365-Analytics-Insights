import { useMemo, useState } from 'react';
import {
  makeStyles,
  tokens,
  Text,
  Card,
  Badge,
  Button,
  Input,
  Select,
  Checkbox,
  Tooltip,
} from '@fluentui/react-components';
import { Dismiss16Regular, Search16Regular } from '@fluentui/react-icons';
import type { AgentEstateSummary, AgentUsageRow, CopilotAdoptionOptions } from '../../types/copilotAdoption';
import { AgentHealth } from '../../types/copilotAdoption';
import CategoryBarChart from '../charts/CategoryBarChart';
import TreemapChart from '../charts/TreemapChart';
import DonutChart from '../charts/DonutChart';
import SqlPopover from '../SqlPopover';
import InfoTip from '../shared/InfoTip';
import { KpiGrid, formatCount, formatDate } from '../shared/KpiGrid';
import type { KpiDefinition } from '../shared/KpiGrid';
import { useAdoptionTableStyles } from './adoptionShared';
import { useT, type TFunction } from '../../i18n';
import { agentHealthReason } from './serverText';

/**
 * Health colours run from "delete this" to "this is working", matching the engagement-band palette
 * so a reader never has to learn a second colour language.
 */
export const AGENT_HEALTH_COLOUR: Record<AgentHealth, string> = {
  [AgentHealth.Retire]: '#d13438',
  [AgentHealth.Review]: '#c19c00',
  [AgentHealth.New]: '#8764b8',
  [AgentHealth.Keep]: '#107c10',
};

const HEALTH_ORDER = [AgentHealth.Retire, AgentHealth.Review, AgentHealth.New, AgentHealth.Keep];

const useStyles = makeStyles({
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  twoUp: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))',
    gap: '16px',
  },
  cardHead: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
  },
  cardTools: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    flexShrink: 0,
  },
  cardBody: {
    marginTop: '10px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  filters: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: '8px',
    marginBottom: '12px',
  },
  spacer: {
    flexGrow: 1,
  },
  tableWrap: {
    overflowX: 'auto',
  },
  badge: {
    color: '#ffffff',
    whiteSpace: 'nowrap',
  },
  agentName: {
    display: 'flex',
    flexDirection: 'column',
    // Agent identifiers are machine-generated and can run to several hundred characters with no
    // break opportunity in them. Left unbounded, one such row stretches this column until every
    // other column is pushed off the right-hand side of the screen.
    maxWidth: '360px',
    // The name is tenant-supplied, so it can be a single unbreakable token too. It wraps rather
    // than truncates - a name is what a reader identifies the agent by, so losing the end of it is
    // worse than a taller row. (No effect on the key below, which is nowrap.)
    overflowWrap: 'anywhere',
  },
  agentKey: {
    display: 'block',
    maxWidth: '360px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    color: tokens.colorNeutralForeground3,
  },
  search: {
    minWidth: '240px',
  },
  reason: {
    maxWidth: '320px',
    color: tokens.colorNeutralForeground2,
  },
  legend: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    marginTop: '12px',
  },
  legendRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(80px, 90px) 1fr',
    gap: '10px',
    alignItems: 'start',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

/** The health verdict as a coloured pill. */
export function AgentHealthBadge({ health, name }: { health: AgentHealth; name: string }) {
  const styles = useStyles();
  return (
    <Badge className={styles.badge} style={{ backgroundColor: AGENT_HEALTH_COLOUR[health] ?? '#605e5c' }} size="small">
      {name}
    </Badge>
  );
}

/**
 * The Copilot agent estate: what exists, who actually uses it, and what should be retired.
 *
 * Belongs in a licence-and-spend tool rather than in general usage reporting because an agent estate
 * has the same problem a seat estate does - things get created, stop being used, and quietly go on
 * costing attention and support. The verdict on each agent is stated, with its reason, rather than
 * leaving the reader to infer it from a date column.
 */
export default function AgentsPanel({
  estate,
  agents,
  options,
  windowDays,
  sql,
}: {
  estate: AgentEstateSummary;
  agents: AgentUsageRow[];
  options: CopilotAdoptionOptions;
  windowDays: number;
  sql: Record<string, string> | null;
}) {
  const styles = useStyles();
  const table = useAdoptionTableStyles();
  const t = useT();

  const [health, setHealth] = useState<'' | string>('');
  const [customOnly, setCustomOnly] = useState(false);
  const [search, setSearch] = useState('');

  // Filtered in the browser rather than on the server: the whole inventory is already in memory,
  // so a name filter is instant and a round trip would only make it feel slower.
  const needle = search.trim().toLowerCase();

  const visible = useMemo(() => {
    return agents.filter(
      (a) =>
        (health === '' || a.health === Number(health)) &&
        (!customOnly || a.isCustomAgent) &&
        (needle === '' ||
          a.name.toLowerCase().includes(needle) ||
          (a.agentKey ?? '').toLowerCase().includes(needle)),
    );
  }, [agents, health, customOnly, needle]);

  const legendStates = useMemo(() => {
    const present = new Set(visible.map((a) => a.health));
    return HEALTH_ORDER.filter((h) => present.has(h));
  }, [visible]);

  const healthBreakdown = useMemo(
    () => estate.healthBreakdown.map((h) => ({ ...h, label: healthBreakdownLabel(h.label, t) })),
    [estate.healthBreakdown, t],
  );

  if (estate.knownAgents === 0) {
    return (
      <Card>
        <Text weight="semibold" size={400}>
          {t('copilotAdoptionAgents.agents.empty.title')}
        </Text>
        <Text size={200} block className={styles.muted} style={{ marginTop: '6px' }}>
          {t('copilotAdoptionAgents.agents.empty.description')}
        </Text>
      </Card>
    );
  }

  const kpis = buildAgentKpis(estate, options, windowDays, t);

  return (
    <div className={styles.stack}>
      <KpiGrid items={kpis} />

      <div className={styles.twoUp}>
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                {t('copilotAdoptionAgents.agents.inventoryHealth.title')}
              </Text>
              <Text size={200} block className={styles.muted}>
                {t('copilotAdoptionAgents.agents.inventoryHealth.description')}
              </Text>
            </div>
            <InfoTip
              title={t('copilotAdoptionAgents.agents.inventoryHealth.title')}
              content={{
                what: t('copilotAdoptionAgents.agents.inventoryHealth.what'),
                how: t('copilotAdoptionAgents.agents.inventoryHealth.how', {
                  retireDays: options.agentRetireInactiveDays,
                  reviewDays: options.agentReviewInactiveDays,
                  minUsers: options.agentMinUsers,
                  newDays: options.agentNewDays,
                }),
                source: t('copilotAdoptionAgents.agents.inventoryHealth.source', {
                  historyDays: estate.historyDays,
                  retireDays: options.agentRetireInactiveDays,
                }),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <DonutChart
              categories={healthBreakdown}
              colours={HEALTH_ORDER.map((h) => AGENT_HEALTH_COLOUR[h])}
              centreValue={formatCount(estate.knownAgents)}
              centreLabel={t('copilotAdoptionAgents.agents.inventoryHealth.knownAgents')}
            />
          </div>
        </Card>

        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                {t('copilotAdoptionAgents.agents.effort.title')}
              </Text>
              <Text size={200} block className={styles.muted}>
                {t('copilotAdoptionAgents.agents.effort.description')}
              </Text>
            </div>
            <InfoTip
              title={t('copilotAdoptionAgents.agents.effort.title')}
              content={{
                what: t('copilotAdoptionAgents.agents.effort.what'),
                how: t('copilotAdoptionAgents.agents.effort.how', { top: options.topSegments }),
                source:
                  t('copilotAdoptionAgents.agents.effort.source'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            {estate.usageByAgent.length > 0 ? (
              <TreemapChart categories={estate.usageByAgent} valueLabel={t('copilotAdoptionAgents.agents.unit.interactions')} />
            ) : (
              <div className={styles.empty}>{t('copilotAdoptionAgents.agents.effort.empty')}</div>
            )}
          </div>
        </Card>
      </div>

      {estate.usageByDepartment.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                {t('copilotAdoptionAgents.agents.department.title')}
              </Text>
              <Text size={200} block className={styles.muted}>
                {t('copilotAdoptionAgents.agents.department.description')}
              </Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title={t('copilotAdoptionAgents.agents.department.title')}
                content={{
                  what: t('copilotAdoptionAgents.agents.department.what'),
                  how: t('copilotAdoptionAgents.agents.department.how', {
                    windowDays,
                    top: options.topSegments,
                  }),
                  source:
                    t('copilotAdoptionAgents.agents.department.source'),
                }}
              />
              {sql?.agentsByDepartment && (
                <SqlPopover sql={sql.agentsByDepartment} title={t('copilotAdoptionAgents.agents.department.sqlTitle')} />
              )}
            </div>
          </div>
          <div className={styles.cardBody}>
            <CategoryBarChart categories={estate.usageByDepartment} valueLabel={t('copilotAdoptionAgents.agents.unit.interactionsTitle')} />
          </div>
        </Card>
      )}

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>
              {t('copilotAdoptionAgents.agents.inventory.title')}
            </Text>
            <Text size={200} block className={styles.muted}>
              {t('copilotAdoptionAgents.agents.inventory.description', { days: estate.historyDays })}
            </Text>
          </div>
          <div className={styles.cardTools}>
            <InfoTip
              title={t('copilotAdoptionAgents.agents.inventory.title')}
              content={{
                what: t('copilotAdoptionAgents.agents.inventory.what'),
                how: t('copilotAdoptionAgents.agents.inventory.how'),
                source:
                  t('copilotAdoptionAgents.agents.inventory.source'),
              }}
            />
            {sql?.agents && <SqlPopover sql={sql.agents} title={t('copilotAdoptionAgents.agents.inventory.sqlTitle')} />}
          </div>
        </div>

        <div className={styles.cardBody}>
          <div className={styles.filters}>
            <Input
              className={styles.search}
              value={search}
              placeholder={t('copilotAdoptionAgents.agents.inventory.search.placeholder')}
              aria-label={t('copilotAdoptionAgents.agents.inventory.search.ariaLabel')}
              contentBefore={<Search16Regular />}
              contentAfter={
                search ? (
                  <Button
                    appearance="transparent"
                    size="small"
                    icon={<Dismiss16Regular />}
                    aria-label={t('copilotAdoptionAgents.agents.inventory.search.clearAriaLabel')}
                    onClick={() => setSearch('')}
                  />
                ) : undefined
              }
              onChange={(_e: any, d: any) => setSearch(d.value)}
            />
            <Select
              value={health}
              aria-label={t('copilotAdoptionAgents.agents.inventory.filterHealth.ariaLabel')}
              onChange={(_e: any, d: any) => setHealth(d.value)}
            >
              <option value="">{t('copilotAdoptionAgents.agents.inventory.filterHealth.all')}</option>
              {HEALTH_ORDER.map((h) => (
                <option key={h} value={h}>
                  {healthLabel(h, t)}
                </option>
              ))}
            </Select>
            <Tooltip
              content={t('copilotAdoptionAgents.agents.inventory.customOnly.tooltip')}
              relationship="description"
            >
              <Checkbox
                label={t('copilotAdoptionAgents.agents.inventory.customOnly.label')}
                checked={customOnly}
                onChange={(_e: any, d: any) => setCustomOnly(!!d.checked)}
              />
            </Tooltip>
            <div className={styles.spacer} />
            <Text size={200} className={styles.muted}>
              {t('copilotAdoptionAgents.agents.inventory.visibleCount', {
                visible: formatCount(visible.length),
                total: formatCount(agents.length),
              })}
            </Text>
          </div>

          {visible.length === 0 ? (
            <div className={styles.empty}>{t('copilotAdoptionAgents.agents.inventory.noMatches')}</div>
          ) : (
            <>
              <div className={styles.tableWrap}>
                <table className={table.table}>
                  <thead>
                    <tr>
                      <th className={table.th}>{t('copilotAdoptionAgents.agents.table.agent')}</th>
                      <th className={table.th}>{t('copilotAdoptionAgents.agents.table.type')}</th>
                      <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionAgents.agents.table.users')}</th>
                      <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionAgents.agents.table.interactions')}</th>
                      <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionAgents.agents.table.perUser')}</th>
                      <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionAgents.agents.table.surfaces')}</th>
                      <th className={table.th}>{t('copilotAdoptionAgents.agents.table.lastUsed')}</th>
                      <th className={table.th}>{t('copilotAdoptionAgents.agents.table.verdict')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {visible.map((agent) => (
                      <tr key={agent.agentId}>
                        <td className={table.td}>
                          <span className={styles.agentName}>
                            <Text size={200} weight="semibold">
                              {agent.name}
                            </Text>
                            {agent.agentKey && (
                              <Text size={100} className={styles.agentKey} title={agent.agentKey}>
                                {agent.agentKey}
                              </Text>
                            )}
                          </span>
                        </td>
                        <td className={table.td}>{agent.isCustomAgent ? t('copilotAdoptionAgents.agents.table.type.custom') : t('copilotAdoptionAgents.agents.table.type.microsoft')}</td>
                        <td className={`${table.td} ${table.tdNumeric}`}>
                          {formatCount(agent.users)}
                          <Text size={100} block className={table.tdSub}>
                            {t('copilotAdoptionAgents.agents.table.licensedUsers', { count: formatCount(agent.licensedUsers) })}
                          </Text>
                        </td>
                        <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(agent.interactions)}</td>
                        <td className={`${table.td} ${table.tdNumeric}`}>{agent.interactionsPerUser}</td>
                        <td className={`${table.td} ${table.tdNumeric}`}>{agent.appsUsed}</td>
                        <td className={table.td}>
                          {formatDate(agent.lastUsedUtc)}
                          {agent.daysSinceLastUse !== null && agent.daysSinceLastUse > 0 && (
                            <Text size={100} block className={table.tdSub}>
                              {t('common.time.daysAgo', { days: agent.daysSinceLastUse })}
                            </Text>
                          )}
                        </td>
                        <td className={table.td}>
                          <Tooltip relationship="description" content={agentHealthReason(t, agent, options)}>
                            <div>
                              <AgentHealthBadge health={agent.health} name={healthLabel(agent.health, t)} />
                            </div>
                          </Tooltip>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              <div className={styles.legend}>
                <Text size={200} weight="semibold">
                  {t('copilotAdoptionAgents.agents.legend.title')}
                </Text>
                {legendStates.map((h) => (
                  <div key={h} className={styles.legendRow}>
                    <AgentHealthBadge health={h} name={healthLabel(h, t)} />
                    <Text size={200} className={styles.reason}>
                      {healthMeaning(h, options, t)}
                    </Text>
                  </div>
                ))}
              </div>
            </>
          )}
        </div>
      </Card>
    </div>
  );
}

function healthLabel(health: AgentHealth, t: TFunction): string {
  switch (health) {
    case AgentHealth.Keep:
      return t('copilotAdoptionAgents.agents.health.keep');
    case AgentHealth.New:
      return t('copilotAdoptionAgents.agents.health.new');
    case AgentHealth.Review:
      return t('copilotAdoptionAgents.agents.health.review');
    default:
      return t('copilotAdoptionAgents.agents.health.retire');
  }
}

function healthBreakdownLabel(label: string, t: TFunction): string {
  switch (label) {
    case 'Keep':
      return t('copilotAdoptionAgents.agents.health.keep');
    case 'New':
      return t('copilotAdoptionAgents.agents.health.new');
    case 'Review':
      return t('copilotAdoptionAgents.agents.health.review');
    case 'Retire':
      return t('copilotAdoptionAgents.agents.health.retire');
    default:
      return label;
  }
}

/** The rule, stated once per verdict rather than repeated per row. */
function healthMeaning(health: AgentHealth, o: CopilotAdoptionOptions, t: TFunction): string {
  switch (health) {
    case AgentHealth.Keep:
      return t('copilotAdoptionAgents.agents.health.keep.meaning', {
        reviewDays: o.agentReviewInactiveDays,
        minUsers: o.agentMinUsers,
      });
    case AgentHealth.New:
      return t('copilotAdoptionAgents.agents.health.new.meaning', { newDays: o.agentNewDays });
    case AgentHealth.Review:
      return t('copilotAdoptionAgents.agents.health.review.meaning', {
        reviewDays: o.agentReviewInactiveDays,
        retireDays: o.agentRetireInactiveDays,
        minUsers: o.agentMinUsers,
      });
    default:
      return t('copilotAdoptionAgents.agents.health.retire.meaning', { retireDays: o.agentRetireInactiveDays });
  }
}

function buildAgentKpis(
  estate: AgentEstateSummary,
  o: CopilotAdoptionOptions,
  windowDays: number,
  t: TFunction,
): KpiDefinition[] {
  const retire = estate.healthBreakdown.find((h) => h.label === 'Retire')?.value ?? 0;

  return [
    {
      key: 'active',
      label: t('copilotAdoptionAgents.agents.kpi.active.label'),
      value: formatCount(estate.activeAgents),
      hint: t('copilotAdoptionAgents.agents.kpi.active.hint', {
        known: formatCount(estate.knownAgents),
        custom: formatCount(estate.customAgents),
      }),
      info: {
        what: t('copilotAdoptionAgents.agents.kpi.active.what', {
          windowDays,
          historyDays: estate.historyDays,
        }),
        how: t('copilotAdoptionAgents.agents.kpi.active.how'),
        source: t('copilotAdoptionAgents.agents.kpi.active.source'),
      },
    },
    {
      key: 'users',
      label: t('copilotAdoptionAgents.agents.kpi.users.label'),
      value: formatCount(estate.agentUsers),
      hint: t('copilotAdoptionAgents.agents.kpi.users.hint', { licensed: formatCount(estate.licensedAgentUsers) }),
      info: {
        what: t('copilotAdoptionAgents.agents.kpi.users.what', { windowDays }),
        how: t('copilotAdoptionAgents.agents.kpi.users.how'),
        source:
          t('copilotAdoptionAgents.agents.kpi.users.source'),
      },
    },
    {
      key: 'intensity',
      label: t('copilotAdoptionAgents.agents.kpi.intensity.label'),
      value: estate.interactionsPerAgentUser,
      hint: t('copilotAdoptionAgents.agents.kpi.intensity.hint', { interactions: formatCount(estate.agentInteractions) }),
      info: {
        what: t('copilotAdoptionAgents.agents.kpi.intensity.what'),
        how: t('copilotAdoptionAgents.agents.kpi.intensity.how'),
        source: t('copilotAdoptionAgents.agents.kpi.intensity.source'),
      },
    },
    {
      key: 'popular',
      label: t('copilotAdoptionAgents.agents.kpi.popular.label'),
      value: <span style={{ fontSize: '20px' }}>{estate.mostPopularAgent ?? '\u2014'}</span>,
      hint: t('copilotAdoptionAgents.agents.kpi.popular.hint'),
      info: {
        what: t('copilotAdoptionAgents.agents.kpi.popular.what'),
        how: t('copilotAdoptionAgents.agents.kpi.popular.how'),
        source: t('copilotAdoptionAgents.agents.kpi.popular.source'),
      },
    },
    {
      key: 'versatile',
      label: t('copilotAdoptionAgents.agents.kpi.versatile.label'),
      value: <span style={{ fontSize: '20px' }}>{estate.mostVersatileAgent ?? '\u2014'}</span>,
      hint: t('copilotAdoptionAgents.agents.kpi.versatile.hint'),
      info: {
        what: t('copilotAdoptionAgents.agents.kpi.versatile.what'),
        how: t('copilotAdoptionAgents.agents.kpi.versatile.how'),
        source: t('copilotAdoptionAgents.agents.kpi.versatile.source'),
      },
    },
    {
      key: 'retire',
      label: t('copilotAdoptionAgents.agents.kpi.retire.label'),
      value: formatCount(retire),
      tone: retire > 0 ? 'critical' : 'good',
      hint: t('copilotAdoptionAgents.agents.kpi.retire.hint', { retireDays: o.agentRetireInactiveDays }),
      info: {
        what: t('copilotAdoptionAgents.agents.kpi.retire.what'),
        how: t('copilotAdoptionAgents.agents.kpi.retire.how', {
          retireDays: o.agentRetireInactiveDays,
          newDays: o.agentNewDays,
        }),
        source:
          t('copilotAdoptionAgents.agents.kpi.retire.source'),
      },
    },
  ];
}
