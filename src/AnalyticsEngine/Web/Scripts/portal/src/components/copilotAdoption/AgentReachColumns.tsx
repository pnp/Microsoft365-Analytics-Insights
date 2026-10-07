import { Text } from '@fluentui/react-components';
import type { AgentUsageRow } from '../../types/copilotAdoption';
import { useT } from '../../i18n';
import { formatCount, formatPct } from '../shared/KpiGrid';
import { useAdoptionTableStyles } from './adoptionShared';

const NOT_MEASURED = '\u2014';

/**
 * The agent inventory's reach columns (#647): how many departments an agent's users came from in the
 * reporting period, and its home department - the one most of them are in - with that department's share.
 *
 * A low home share is the signal: a local win that has spread beyond the team that made it. The home
 * department's name is tenant data, shown as stored, and the server withholds it when fewer than
 * `minSeatsPerSegment` of the agent's users are in it, so it never points at one or two people.
 *
 * Kept out of AgentsPanel so the inventory table gains two cells rather than their logic.
 */
export function AgentReachHeaderCells({ minSeatsPerSegment }: { minSeatsPerSegment: number }) {
  const t = useT();
  const table = useAdoptionTableStyles();

  return (
    <>
      <th
        className={`${table.th} ${table.thNumeric}`}
        title={t('copilotAdoptionAgents.agents.table.departments.help')}
      >
        {t('copilotAdoptionAgents.agents.table.departments')}
      </th>
      <th className={table.th} title={t('copilotAdoptionAgents.agents.table.homeDepartment.help', { min: minSeatsPerSegment })}>
        {t('copilotAdoptionAgents.agents.table.homeDepartment')}
      </th>
    </>
  );
}

export function AgentReachCells({ agent, minSeatsPerSegment }: { agent: AgentUsageRow; minSeatsPerSegment: number }) {
  const t = useT();
  const table = useAdoptionTableStyles();
  const share = agent.homeDepartmentSharePct;

  return (
    <>
      <td className={`${table.td} ${table.tdNumeric}`}>
        {agent.departments === null || agent.departments === undefined ? NOT_MEASURED : formatCount(agent.departments)}
      </td>
      <td className={table.td}>
        {share === null || share === undefined ? (
          NOT_MEASURED
        ) : (
          <>
            {agent.homeDepartment ?? (
              <Text size={200} italic>
                {t('copilotAdoptionAgents.agents.table.homeDepartment.withheld', { min: minSeatsPerSegment })}
              </Text>
            )}
            <Text size={100} block className={table.tdSub}>
              {t('copilotAdoptionAgents.agents.table.homeDepartment.share', {
                pct: formatPct(share),
                users: formatCount(agent.windowUsers ?? 0),
              })}
            </Text>
          </>
        )}
      </td>
    </>
  );
}
