import type { ReactNode } from 'react';
import {
  Button,
  Link,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Tag,
  Text,
  Tooltip,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular, Info16Regular, LockClosed16Regular, LockOpen16Regular, Warning16Regular } from '@fluentui/react-icons';
import { formatNumber, plural, useT } from '../../i18n';
import { usePortalAccess } from '../../access';
import type { GlobalFilterClauseEcho, GlobalFilterEcho } from '../../types/globalFilter';
import { dimensionLabel, operatorShortLabel, type DimensionNameSource } from '../userFilter/describeUserFilter';
import { groupClauseIndexes } from '../userFilter/userFilterModel';
import { describeGlobalClause, describeGlobalFilter, globalPillValues, isUnresolved } from './describeGlobalFilter';
import { usesViewer } from './globalFilterModel';
import { useGlobalFilter } from './GlobalFilterProvider';

/** Where the administrator edits the filter. */
export const GLOBAL_FILTER_ADMIN_PATH = '/admin/global-filter';

const useStyles = makeStyles({
  bar: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    marginTop: '12px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorBrandBackground2,
    border: `1px solid ${tokens.colorBrandStroke2}`,
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '6px',
  },
  lead: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    marginRight: '4px',
    color: tokens.colorBrandForeground2,
  },
  infoIcon: {
    color: tokens.colorNeutralForeground3,
    cursor: 'help',
  },
  group: {
    display: 'inline-flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '6px',
    padding: '3px 6px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px dashed ${tokens.colorBrandStroke2}`,
  },
  join: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
  },
  orJoin: {
    color: tokens.colorBrandForeground1,
  },
  pillLabel: {
    fontWeight: tokens.fontWeightSemibold,
  },
  pillValues: {
    maxWidth: '320px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    display: 'inline-block',
    verticalAlign: 'bottom',
  },
  unresolved: {
    color: tokens.colorPaletteDarkOrangeForeground1,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  spacer: {
    flexGrow: 1,
  },
  print: {
    display: 'none',
    marginTop: '8px',
    padding: '8px 10px',
    borderLeft: `3px solid ${tokens.colorBrandStroke1}`,
  },
  message: {
    marginTop: '12px',
  },
});

export interface GlobalFilterBarProps {
  /**
   * How the filter applies on this page, where part of it cannot be narrowed - team-level figures, an
   * agent's own costs. Said beside the filter, so nobody reads a tenant-wide figure as a filtered one.
   */
  note?: string;
}

/**
 * The administrator's global report filter, as every Insights page shows it: the conditions as locked
 * pills - the same shape as the reader's own filter, so the two read as one filter in two parts - with
 * the reader's own values filled in ("Sales (from your profile)").
 *
 * Shown only; the reports apply it on the server whatever this component does. A portal administrator
 * also gets a link to edit it and a switch to see reports without it, which changes only their own view.
 */
export default function GlobalFilterBar({ note }: GlobalFilterBarProps) {
  const styles = useStyles();
  const t = useT();
  const access = usePortalAccess();
  const { status, effective, switching, refresh, setBypassed } = useGlobalFilter();

  if (!effective) {
    // Nothing is shown while the first answer is awaited - a bar that appears and vanishes on every page
    // load for the many tenants with no filter would be noise. A failure is said, because the reports
    // still apply any filter that is set and the reader deserves to know why the figures may be narrowed.
    if (status !== 'error') return null;
    return (
      <MessageBar intent="warning" className={styles.message} data-print="hide">
        <MessageBarBody>{t('globalFilter.bar.loadFailed')}</MessageBarBody>
        <MessageBarActions>
          <Button size="small" icon={<ArrowClockwise16Regular />} onClick={() => void refresh()}>
            {t('globalFilter.bar.retry')}
          </Button>
        </MessageBarActions>
      </MessageBar>
    );
  }

  if (!effective.active) return null;

  // Switching the filter off for one's own view needs Administration; editing it needs See PII as well,
  // as the editor page does - so an administrator without it is not offered a link to a refusal.
  const editLink = effective.canBypass && access.seePii ? (
    <Link href={`#${GLOBAL_FILTER_ADMIN_PATH}`}>{t('globalFilter.bar.edit')}</Link>
  ) : null;

  if (effective.bypassed) {
    return (
      <>
        <MessageBar intent="warning" className={styles.message} data-print="hide">
          <MessageBarBody>
            {t('globalFilter.bar.bypassed')} {editLink}
          </MessageBarBody>
          <MessageBarActions>
            <Button size="small" icon={<LockClosed16Regular />} disabled={switching} onClick={() => void setBypassed(false)}>
              {switching ? t('globalFilter.bar.switching') : t('globalFilter.bar.switchOn')}
            </Button>
          </MessageBarActions>
        </MessageBar>
        <div className={styles.print} data-print="only">
          <Text size={300} block>{t('globalFilter.print.bypassed')}</Text>
        </div>
      </>
    );
  }

  if (effective.invalid || !effective.filter) {
    return (
      <MessageBar intent="error" className={styles.message} data-print="hide">
        <MessageBarBody>
          {effective.canBypass ? (
            <>
              {t('globalFilter.bar.invalidAdmin')} {editLink}
            </>
          ) : (
            t('globalFilter.bar.invalid')
          )}
        </MessageBarBody>
        {effective.canBypass && (
          <MessageBarActions>
            <Button size="small" icon={<LockOpen16Regular />} disabled={switching} onClick={() => void setBypassed(true)}>
              {switching ? t('globalFilter.bar.switching') : t('globalFilter.bar.switchOff')}
            </Button>
          </MessageBarActions>
        )}
      </MessageBar>
    );
  }

  return (
    <LockedFilter
      echo={effective.filter}
      note={note}
      canBypass={effective.canBypass}
      switching={switching}
      onSwitchOff={() => void setBypassed(true)}
      editLink={editLink}
      tooFewPeople={effective.tooFewPeople === true}
      minimumPeople={effective.minimumPeople ?? 0}
    />
  );
}

function LockedFilter({
  echo,
  note,
  canBypass,
  switching,
  onSwitchOff,
  editLink,
  tooFewPeople,
  minimumPeople,
}: {
  echo: GlobalFilterEcho;
  note?: string;
  canBypass: boolean;
  switching: boolean;
  onSwitchOff: () => void;
  editLink: ReactNode;
  tooFewPeople: boolean;
  minimumPeople: number;
}) {
  const styles = useStyles();
  const t = useT();
  const source: DimensionNameSource = { names: echo.dimensionNames };
  const clauses = echo.clauses;
  const groups = groupClauseIndexes({ clauses });
  const grouped = groups.length > 1;
  const description = describeGlobalFilter(t, clauses, 'reader', source);

  const pill = (clause: GlobalFilterClauseEcho, index: number) => {
    const described = describeGlobalClause(t, clause, 'reader', source);
    const unresolved = isUnresolved(clause);
    return (
      <Tooltip key={`pill-${index}`} content={described} relationship="description">
        <Tag
          size="small"
          shape="rounded"
          appearance="outline"
          icon={unresolved ? <Warning16Regular className={styles.unresolved} /> : <LockClosed16Regular />}
          aria-label={t('globalFilter.pill.lockedAria', { condition: described })}
        >
          <span className={styles.pillLabel}>{dimensionLabel(t, clause.dimension, source)}</span>{' '}
          {operatorShortLabel(t, clause.dimension, clause.operator)}{' '}
          <span className={mergeClasses(styles.pillValues, unresolved && styles.unresolved)}>
            {globalPillValues(t, clause, 'reader')}
          </span>
        </Tag>
      </Tooltip>
    );
  };

  const join = (clause: GlobalFilterClauseEcho, key: string) => (
    <span key={key} className={mergeClasses(styles.join, clause.join === 'or' && styles.orJoin)}>
      {clause.join === 'or' ? t('userFilter.join.or') : t('userFilter.join.and')}
    </span>
  );

  const viewerMissing = !echo.viewerFound && usesViewer(clauses);
  const nobody = !viewerMissing && echo.matchedPeople === 0;

  return (
    <>
      <div className={styles.bar} data-print="hide">
        <div className={styles.row} role="group" aria-label={t('globalFilter.bar.groupAria')}>
          <span className={styles.lead}>
            <LockClosed16Regular aria-hidden />
            <Text weight="semibold" size={300}>{t('globalFilter.bar.label')}</Text>
            <Tooltip content={t('globalFilter.bar.info')} relationship="description">
              <Info16Regular className={styles.infoIcon} tabIndex={0} aria-label={t('globalFilter.bar.infoAria')} role="img" />
            </Tooltip>
          </span>

          {groups.flatMap((group, g) => {
            const content = group.flatMap((index, position) => [
              ...(position > 0 ? [join(clauses[index], `join-${index}`)] : []),
              pill(clauses[index], index),
            ]);
            const orJoin = g > 0 ? [join(clauses[group[0]], `or-${group[0]}`)] : [];
            return grouped
              ? [
                  ...orJoin,
                  <span key={`group-${group[0]}`} className={styles.group}>
                    {content}
                  </span>,
                ]
              : [...orJoin, ...content];
          })}

          <Text size={200} className={styles.muted}>
            {t('globalFilter.bar.coverage', {
              matched: formatNumber(echo.matchedPeople),
              total: formatNumber(echo.directoryPeople),
            })}
          </Text>

          <span className={styles.spacer} />

          {editLink}
          {canBypass && (
            <Tooltip content={t('globalFilter.bar.switchOffHint')} relationship="description">
              <Button size="small" appearance="subtle" icon={<LockOpen16Regular />} disabled={switching} onClick={onSwitchOff}>
                {switching ? t('globalFilter.bar.switching') : t('globalFilter.bar.switchOff')}
              </Button>
            </Tooltip>
          )}
        </div>

        {grouped && (
          <Text size={200} className={styles.muted}>
            {t('globalFilter.bar.readback', { description })}
          </Text>
        )}

        {note && <Text size={200} className={styles.muted}>{note}</Text>}

        {viewerMissing && (
          <MessageBar intent="warning">
            <MessageBarBody>{t('globalFilter.bar.viewerNotFound')}</MessageBarBody>
          </MessageBar>
        )}
        {nobody && (
          <MessageBar intent="warning">
            <MessageBarBody>{t('globalFilter.bar.matchesNobody')}</MessageBarBody>
          </MessageBar>
        )}
        {tooFewPeople && (
          <MessageBar intent="warning">
            <MessageBarBody>
              {t(plural(echo.matchedPeople, 'globalFilter.bar.tooFewPeople.one', 'globalFilter.bar.tooFewPeople.other'), {
                matched: formatNumber(echo.matchedPeople),
                minimum: formatNumber(minimumPeople),
              })}
            </MessageBarBody>
          </MessageBar>
        )}
        {echo.unknownDimensions.length > 0 && (
          <MessageBar intent="info">
            <MessageBarBody>{t('globalFilter.bar.unknownDimension')}</MessageBarBody>
          </MessageBar>
        )}
      </div>

      {/* On paper the bar is hidden with every other control, and a printout that did not say who it
          leaves out would be read as the whole tenant. */}
      <div className={styles.print} data-print="only">
        <Text weight="semibold" size={300} block>
          {t('globalFilter.print.heading')}
        </Text>
        <Text size={300} block>{t('globalFilter.print.description', { description })}</Text>
        <Text size={200} block>
          {t('globalFilter.print.coverage', {
            matched: formatNumber(echo.matchedPeople),
            total: formatNumber(echo.directoryPeople),
          })}
        </Text>
        {note && <Text size={200} block>{note}</Text>}
      </div>
    </>
  );
}
