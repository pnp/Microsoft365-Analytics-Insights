import { useMemo, useState } from 'react';
import { makeStyles, tokens, Text, Card, Badge, Select, Checkbox, Tooltip } from '@fluentui/react-components';
import type { AgentEstateSummary, AgentUsageRow, CopilotAdoptionOptions } from '../../types/copilotAdoption';
import { AgentHealth } from '../../types/copilotAdoption';
import CategoryBarChart from '../charts/CategoryBarChart';
import TreemapChart from '../charts/TreemapChart';
import DonutChart from '../charts/DonutChart';
import SqlPopover from '../SqlPopover';
import InfoTip from './InfoTip';
import { KpiGrid, formatCount, formatDate } from './KpiGrid';
import type { KpiDefinition } from './KpiGrid';
import { useAdoptionTableStyles } from './adoptionShared';

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

  const [health, setHealth] = useState<'' | string>('');
  const [customOnly, setCustomOnly] = useState(false);

  const visible = useMemo(() => {
    return agents.filter(
      (a) => (health === '' || a.health === Number(health)) && (!customOnly || a.isCustomAgent),
    );
  }, [agents, health, customOnly]);

  const legendStates = useMemo(() => {
    const present = new Set(visible.map((a) => a.health));
    return HEALTH_ORDER.filter((h) => present.has(h));
  }, [visible]);

  if (estate.knownAgents === 0) {
    return (
      <Card>
        <Text weight="semibold" size={400}>
          No Copilot agents found
        </Text>
        <Text size={200} block className={styles.muted} style={{ marginTop: '6px' }}>
          No Copilot interaction in the history window was attributed to an agent. Either no agents are in
          use in this tenant, or the Copilot audit import has not run for long enough to have seen one.
        </Text>
      </Card>
    );
  }

  const kpis = buildAgentKpis(estate, options, windowDays);

  return (
    <div className={styles.stack}>
      <KpiGrid items={kpis} />

      <div className={styles.twoUp}>
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Inventory health
              </Text>
              <Text size={200} block className={styles.muted}>
                Every known agent gets exactly one verdict. The Retire and Review counts are the size of the
                clean-up.
              </Text>
            </div>
            <InfoTip
              title="Inventory health"
              content={{
                what: 'What to do about each agent that has been seen in the history window: keep it, review it, retire it, or leave it alone because it is too new to judge.',
                how: `Retire = not used for ${options.agentRetireInactiveDays} days or more. Review = last used between ${options.agentReviewInactiveDays} and ${options.agentRetireInactiveDays} days ago, or still current but used by fewer than ${options.agentMinUsers} people. New = first seen within the last ${options.agentNewDays} days, which exempts it from review entirely. Keep = used within ${options.agentReviewInactiveDays} days by at least ${options.agentMinUsers} people.`,
                source: `The "New" exemption is deliberate: a brand-new agent with two users has not failed, it has not started. Agents are counted over ${estate.historyDays} days rather than the reporting period, because an agent nobody has touched for months is exactly what this is looking for. That window is deliberately shorter than the analysis history: it only needs to reach past the ${options.agentRetireInactiveDays}-day retirement line, and reading a full year of audit history to learn nothing extra is expensive on a large tenant.`,
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <DonutChart
              categories={estate.healthBreakdown}
              colours={HEALTH_ORDER.map((h) => AGENT_HEALTH_COLOUR[h])}
              centreValue={formatCount(estate.knownAgents)}
              centreLabel="known agents"
            />
          </div>
        </Card>

        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Where agent effort goes
              </Text>
              <Text size={200} block className={styles.muted}>
                Interactions per agent. Usually a handful carry almost everything.
              </Text>
            </div>
            <InfoTip
              title="Where agent effort goes"
              content={{
                what: 'Total interactions attributed to each agent across the history window, sized by area.',
                how: `The top ${options.topSegments} agents by interaction count. Counted across everyone - an agent's worth to the organisation does not depend on whether the people using it hold a Copilot seat.`,
                source:
                  'Read against the inventory table below: a large tile with very few users is one person\u2019s tool, not an adopted agent.',
              }}
            />
          </div>
          <div className={styles.cardBody}>
            {estate.usageByAgent.length > 0 ? (
              <TreemapChart categories={estate.usageByAgent} valueLabel="interactions" />
            ) : (
              <div className={styles.empty}>No agent interactions recorded.</div>
            )}
          </div>
        </Card>
      </div>

      {estate.usageByDepartment.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Agent usage by department
              </Text>
              <Text size={200} block className={styles.muted}>
                Agent interactions in the selected period, by the department of the person who ran them.
              </Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title="Agent usage by department"
                content={{
                  what: 'Which parts of the organisation are actually using Copilot agents.',
                  how: `Agent interactions in the last ${windowDays} days grouped by the user\u2019s department from the imported metadata, top ${options.topSegments}. Counts interactions, not people, so one heavy user can dominate a department.`,
                  source:
                    'Unlike the inventory above, this uses the selected reporting period rather than the full history - it is a "what is happening now" view.',
                }}
              />
              {sql?.agentsByDepartment && (
                <SqlPopover sql={sql.agentsByDepartment} title="SQL behind this chart" />
              )}
            </div>
          </div>
          <div className={styles.cardBody}>
            <CategoryBarChart categories={estate.usageByDepartment} valueLabel="Interactions" />
          </div>
        </Card>
      )}

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>
              Agent inventory
            </Text>
            <Text size={200} block className={styles.muted}>
              Every agent seen in the last {estate.historyDays} days, busiest first.
            </Text>
          </div>
          <div className={styles.cardTools}>
            <InfoTip
              title="Agent inventory"
              content={{
                what: 'Every Copilot agent that has been used at least once in the history window, with how many people use it, how much, how broadly, and the verdict on it.',
                how: `"Users" is distinct people across the whole tenant; "licensed" is how many of them hold a Copilot seat. "Surfaces" is the number of distinct Copilot hosts the agent was invoked from - an agent used in only one host is doing a narrower job than its interaction count suggests, which is what "most versatile" above measures.`,
                source:
                  'Agent identity comes from the Copilot audit log. Agents that have never been invoked do not appear at all - the audit log only records agents that were used.',
              }}
            />
            {sql?.agents && <SqlPopover sql={sql.agents} title="SQL behind this table" />}
          </div>
        </div>

        <div className={styles.cardBody}>
          <div className={styles.filters}>
            <Select
              value={health}
              aria-label="Filter agents by health"
              onChange={(_e, d) => setHealth(d.value)}
            >
              <option value="">All verdicts</option>
              {HEALTH_ORDER.map((h) => (
                <option key={h} value={h}>
                  {healthLabel(h)}
                </option>
              ))}
            </Select>
            <Tooltip
              content="Agents your organisation built, rather than the ones Microsoft ships."
              relationship="description"
            >
              <Checkbox
                label="Custom agents only"
                checked={customOnly}
                onChange={(_e, d) => setCustomOnly(!!d.checked)}
              />
            </Tooltip>
            <div className={styles.spacer} />
            <Text size={200} className={styles.muted}>
              {formatCount(visible.length)} of {formatCount(agents.length)} agents
            </Text>
          </div>

          {visible.length === 0 ? (
            <div className={styles.empty}>No agents match these filters.</div>
          ) : (
            <>
              <div className={styles.tableWrap}>
                <table className={table.table}>
                  <thead>
                    <tr>
                      <th className={table.th}>Agent</th>
                      <th className={table.th}>Type</th>
                      <th className={`${table.th} ${table.thNumeric}`}>Users</th>
                      <th className={`${table.th} ${table.thNumeric}`}>Interactions</th>
                      <th className={`${table.th} ${table.thNumeric}`}>Per user</th>
                      <th className={`${table.th} ${table.thNumeric}`}>Surfaces</th>
                      <th className={table.th}>Last used</th>
                      <th className={table.th}>Verdict</th>
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
                              <Text size={100} className={styles.muted}>
                                {agent.agentKey}
                              </Text>
                            )}
                          </span>
                        </td>
                        <td className={table.td}>{agent.isCustomAgent ? 'Custom' : 'Microsoft'}</td>
                        <td className={`${table.td} ${table.tdNumeric}`}>
                          {formatCount(agent.users)}
                          <Text size={100} block className={styles.muted}>
                            {formatCount(agent.licensedUsers)} licensed
                          </Text>
                        </td>
                        <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(agent.interactions)}</td>
                        <td className={`${table.td} ${table.tdNumeric}`}>{agent.interactionsPerUser}</td>
                        <td className={`${table.td} ${table.tdNumeric}`}>{agent.appsUsed}</td>
                        <td className={table.td}>
                          {formatDate(agent.lastUsedUtc)}
                          {agent.daysSinceLastUse !== null && agent.daysSinceLastUse > 0 && (
                            <Text size={100} block className={styles.muted}>
                              {agent.daysSinceLastUse} days ago
                            </Text>
                          )}
                        </td>
                        <td className={table.td}>
                          <Tooltip relationship="description" content={agent.healthReason}>
                            <div>
                              <AgentHealthBadge health={agent.health} name={agent.healthName} />
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
                  What each verdict means
                </Text>
                {legendStates.map((h) => (
                  <div key={h} className={styles.legendRow}>
                    <AgentHealthBadge health={h} name={healthLabel(h)} />
                    <Text size={200} className={styles.reason}>
                      {healthMeaning(h, options)}
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

function healthLabel(health: AgentHealth): string {
  switch (health) {
    case AgentHealth.Keep:
      return 'Keep';
    case AgentHealth.New:
      return 'New';
    case AgentHealth.Review:
      return 'Review';
    default:
      return 'Retire';
  }
}

/** The rule, stated once per verdict rather than repeated per row. */
function healthMeaning(health: AgentHealth, o: CopilotAdoptionOptions): string {
  switch (health) {
    case AgentHealth.Keep:
      return `Used within the last ${o.agentReviewInactiveDays} days by at least ${o.agentMinUsers} people. Genuinely adopted - keep supporting it.`;
    case AgentHealth.New:
      return `First seen within the last ${o.agentNewDays} days. Too new to judge, and deliberately exempt from review - a brand-new agent with two users has not failed, it has not started.`;
    case AgentHealth.Review:
      return `Either going quiet (last used ${o.agentReviewInactiveDays}-${o.agentRetireInactiveDays} days ago) or still current but used by fewer than ${o.agentMinUsers} people - often its author testing it, or an agent nobody was told about.`;
    default:
      return `Not used for ${o.agentRetireInactiveDays} days or more. Confirm with its owner, then remove it.`;
  }
}

function buildAgentKpis(
  estate: AgentEstateSummary,
  o: CopilotAdoptionOptions,
  windowDays: number,
): KpiDefinition[] {
  const retire = estate.healthBreakdown.find((h) => h.label === 'Retire')?.value ?? 0;

  return [
    {
      key: 'active',
      label: 'Active agents',
      value: formatCount(estate.activeAgents),
      hint: `${formatCount(estate.knownAgents)} known, ${formatCount(estate.customAgents)} custom-built`,
      info: {
        what: `Agents used at least once in the last ${windowDays} days. "Known" counts every agent seen anywhere in the ${estate.historyDays}-day inventory window, used recently or not.`,
        how: 'An agent only appears once it has been invoked - the Copilot audit log records agents that were used, not agents that exist. An agent built but never run is invisible here, and to everyone else too.',
        source: 'Custom means an agent your organisation built, rather than one Microsoft ships.',
      },
    },
    {
      key: 'users',
      label: 'Agent users',
      value: formatCount(estate.agentUsers),
      hint: `${formatCount(estate.licensedAgentUsers)} of them hold a Copilot seat`,
      info: {
        what: `Distinct people who used at least one agent in the last ${windowDays} days, licensed or not.`,
        how: 'Counted from the per-user rows rather than by summing across agents, which would double-count anyone who uses more than one.',
        source:
          'Agents are available to unlicensed Copilot Chat users too, which is why this can exceed the licensed figure.',
      },
    },
    {
      key: 'intensity',
      label: 'Interactions per agent user',
      value: estate.interactionsPerAgentUser,
      hint: `${formatCount(estate.agentInteractions)} agent interactions in total`,
      info: {
        what: 'How much the people who use agents actually use them.',
        how: 'Total agent interactions in the period divided by the number of distinct people who ran at least one. Only people who used an agent are in the denominator - including everyone else would just restate the adoption rate.',
        source: 'A high figure across very few users is one or two enthusiasts, not an adopted capability.',
      },
    },
    {
      key: 'popular',
      label: 'Most used agent',
      value: <span style={{ fontSize: '20px' }}>{estate.mostPopularAgent ?? '\u2014'}</span>,
      hint: 'The agent whose retirement would be felt most',
      info: {
        what: 'The agent used by the most distinct people.',
        how: 'Ranked by user count, not interaction count - an agent one person runs a thousand times is not the most widely useful one, and ranking by volume would say it was.',
        source: 'Ties are broken by interaction count.',
      },
    },
    {
      key: 'versatile',
      label: 'Most versatile agent',
      value: <span style={{ fontSize: '20px' }}>{estate.mostVersatileAgent ?? '\u2014'}</span>,
      hint: 'Used across the most Copilot surfaces',
      info: {
        what: 'The agent invoked from the greatest number of distinct Copilot surfaces (Teams, Word, Outlook, Copilot Chat and so on).',
        how: 'Breadth of surface, not volume. An agent used everywhere by a few people is doing a broader job than one used constantly in a single host, and the two need different support.',
        source: 'Ties are broken by user count.',
      },
    },
    {
      key: 'retire',
      label: 'Agents to retire',
      value: formatCount(retire),
      tone: retire > 0 ? 'critical' : 'good',
      hint: `Unused for ${o.agentRetireInactiveDays}+ days`,
      info: {
        what: 'Agents that have not been used for long enough that they are almost certainly abandoned.',
        how: `No recorded interaction for ${o.agentRetireInactiveDays} days or more. Agents first seen within the last ${o.agentNewDays} days are exempt regardless, so this never catches something that simply has not launched yet.`,
        source:
          'Retiring an agent is not free - confirm with its owner first. The point of the figure is that an unreviewed agent estate grows indefinitely and nobody notices.',
      },
    },
  ];
}
