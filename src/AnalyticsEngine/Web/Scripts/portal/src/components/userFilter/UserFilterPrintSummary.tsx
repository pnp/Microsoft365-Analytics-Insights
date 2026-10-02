import { Text, makeStyles, tokens } from '@fluentui/react-components';
import { formatNumber, useT } from '../../i18n';
import type { UserFilter, UserFilterDimension, UserFilterEcho } from '../../types/userFilter';
import { describeGroups, joinConditions, type DimensionNameSource } from './describeUserFilter';
import { isEmptyFilter } from './userFilterModel';

const useStyles = makeStyles({
  block: {
    display: 'none',
    marginTop: '8px',
    padding: '8px 10px',
    borderLeft: `3px solid ${tokens.colorBrandStroke1}`,
  },
  list: {
    margin: '4px 0 0 0',
    paddingInlineStart: '20px',
  },
});

/**
 * Who a printed report covers, in plain words, for the executive holding the paper.
 *
 * Print only (`data-print="only"`): on screen the filter bar says the same thing and can change it.
 * On paper the bar is hidden with the rest of the controls, and a printout that did not say which
 * people it describes would be read - and quoted - as the whole tenant. So the filter is spelled out
 * the way a person would say it: one sentence for one condition, a list for several, and numbered
 * groups when any one of several groups qualifies.
 *
 * Reads the filter the server says it applied (`echo`) when there is one, rather than the one the page
 * asked for, so the paper can never describe a different population from the figures printed on it.
 */
export default function UserFilterPrintSummary({
  filter,
  echo,
  dimensions,
  withinGlobalFilter = false,
}: {
  filter: UserFilter;
  echo?: UserFilterEcho | null;
  dimensions?: UserFilterDimension[] | null;
  /**
   * True when an administrator's global filter also narrows the report. With no filter of the reader's
   * own, "everyone" then means everyone that filter allows - which the global filter's own printed block
   * spells out above this one.
   */
  withinGlobalFilter?: boolean;
}) {
  const styles = useStyles();
  const t = useT();

  const applied: UserFilter = echo ? { clauses: echo.clauses } : filter;
  const source: DimensionNameSource = { dimensions, names: echo?.dimensionNames };
  const groups = isEmptyFilter(applied) ? [] : describeGroups(t, applied, source);

  return (
    <div className={styles.block} data-print="only">
      <Text weight="semibold" size={300} block>
        {t('userFilter.print.heading')}
      </Text>

      {groups.length === 0 && (
        <Text size={300} block>
          {withinGlobalFilter ? t('userFilter.print.everyoneWithinGlobal') : t('userFilter.print.everyone')}
        </Text>
      )}

      {groups.length === 1 && groups[0].length === 1 && (
        <Text size={300} block>{t('userFilter.print.single', { condition: groups[0][0] })}</Text>
      )}

      {groups.length === 1 && groups[0].length > 1 && (
        <>
          <Text size={300} block>{t('userFilter.print.all')}</Text>
          <ul className={styles.list}>
            {groups[0].map((condition, i) => (
              <li key={i}>
                <Text size={300}>{condition}</Text>
              </li>
            ))}
          </ul>
        </>
      )}

      {groups.length > 1 && (
        <>
          <Text size={300} block>{t('userFilter.print.any')}</Text>
          <ul className={styles.list}>
            {groups.map((conditions, i) => (
              <li key={i}>
                <Text size={300}>
                  {t('userFilter.print.groupItem', {
                    number: formatNumber(i + 1),
                    conditions: joinConditions(t, conditions, 'and'),
                  })}
                </Text>
              </li>
            ))}
          </ul>
        </>
      )}

      {echo && groups.length > 0 && (
        <Text size={200} block>
          {t('userFilter.print.matched', {
            matched: formatNumber(echo.matchedPeople),
            total: formatNumber(echo.directoryPeople),
          })}
        </Text>
      )}

      {echo && echo.unknownDimensions.length > 0 && <Text size={200} block>{t('userFilter.print.unknown')}</Text>}
    </div>
  );
}
