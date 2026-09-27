import { useEffect, useRef, useState } from 'react';
import {
  Button,
  InteractionTag,
  InteractionTagPrimary,
  InteractionTagSecondary,
  Menu,
  MenuButton,
  MenuItemRadio,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Text,
  Tooltip,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { Add16Regular, Filter16Regular } from '@fluentui/react-icons';
import { useT, type TFunction } from '../../i18n';
import type { UserFilter, UserFilterClause, UserFilterDimension, UserFilterJoin } from '../../types/userFilter';
import UserFilterClauseEditor, { DimensionCue } from './UserFilterClauseEditor';
import { describeClause, describeUserFilter, dimensionLabel, operatorShortLabel, valueLabel } from './describeUserFilter';
import {
  MAX_CLAUSES,
  addClause,
  fitsLimits,
  groupClauseIndexes,
  isCustomDimension,
  isTextOperator,
  newClause,
  removeClause,
  replaceClause,
  setJoin,
  userFilterKey,
} from './userFilterModel';
import { useUserFilterDimensions } from './useUserFilterDimensions';

/** How many values a pill spells out before summarising the rest as "+N more". */
const PILL_VALUES = 2;

const useStyles = makeStyles({
  bar: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    marginTop: '12px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
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
    color: tokens.colorNeutralForeground2,
  },
  group: {
    display: 'inline-flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '6px',
    padding: '3px 6px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px dashed ${tokens.colorNeutralStroke1}`,
  },
  editorGroup: {
    flexBasis: '100%',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  pillLabel: {
    fontWeight: tokens.fontWeightSemibold,
  },
  pillValues: {
    maxWidth: '320px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  join: {
    minWidth: 'auto',
    fontWeight: tokens.fontWeightSemibold,
  },
  orJoin: {
    color: tokens.colorBrandForeground1,
  },
});

/** The values as a pill shows them: the first few, then "+N more". */
function pillValues(t: TFunction, clause: UserFilterClause): string {
  const labels = [
    ...(clause.includeNotSet ? [t('userFilter.editor.notSet')] : []),
    ...clause.values.map((v) =>
      isTextOperator(clause.operator) ? t('userFilter.describe.quoted', { term: v }) : valueLabel(t, clause.dimension, v),
    ),
  ];

  const shown = labels.slice(0, PILL_VALUES).join(', ');
  const rest = labels.length - PILL_VALUES;
  return rest > 0 ? `${shown} ${t('userFilter.bar.moreValues', { count: rest })}` : shown;
}

export interface UserFilterBarProps {
  filter: UserFilter;
  onChange: (filter: UserFilter) => void;
  /**
   * The custom organisation type names the server echoed with the filter it applied, so a condition
   * still has a label if the dimension list has not loaded - or no longer lists that type.
   */
  echoNames?: Record<string, string> | null;
}

/**
 * The page-wide filter: conditions on people's Entra ID attributes and custom organisations, shown
 * as pills in the way Azure Monitor shows metric filters.
 *
 * Built for two readers. Most people add one condition - "Department is Sales" - so the first one
 * needs nothing but a property and a value. Anyone who adds a second is building something, so from
 * there on the AND/OR connectors appear between the pills, and the groups they form are drawn,
 * because AND binds tighter than OR and a filter whose meaning depends on an unseen rule is a filter
 * nobody trusts. The plain-English readback below the pills settles any doubt.
 *
 * Reusable: nothing here knows which report it narrows. The report takes the serialised filter
 * (`serializeUserFilter`) and hands it to its own API.
 */
export default function UserFilterBar({ filter, onChange, echoNames }: UserFilterBarProps) {
  const styles = useStyles();
  const t = useT();
  const { list, loading, error, reload } = useUserFilterDimensions();
  const [editing, setEditing] = useState<number | 'new' | null>(null);
  const [newJoin, setNewJoin] = useState<UserFilterJoin>('and');

  // A change made elsewhere - the domain table's Filter button, a different ?filter= link - while a
  // condition is open would leave the editor holding a draft of a clause that has moved or gone, and
  // Apply would write that stale draft over whatever now sits at its index. Close it instead.
  const filterKey = userFilterKey(filter);
  const lastFilterKey = useRef(filterKey);
  useEffect(() => {
    if (lastFilterKey.current === filterKey) return;
    lastFilterKey.current = filterKey;
    setEditing((current) => (typeof current === 'number' ? null : current));
  }, [filterKey]);

  const dimensions: UserFilterDimension[] = list?.dimensions ?? [];
  const source = { dimensions, names: echoNames };
  const clauses = filter.clauses;
  const groups = groupClauseIndexes(filter);
  const grouped = groups.length > 1;

  const condition = (clause: UserFilterClause) =>
    describeClause(t, clause, dimensionLabel(t, clause.dimension, source));

  const tooLong = (candidate: UserFilter) => (fitsLimits(candidate) ? null : t('userFilter.editor.tooLong'));

  const startAdding = () => {
    setNewJoin('and');
    setEditing('new');
  };

  const apply = (index: number | 'new', clause: UserFilterClause) => {
    onChange(index === 'new' ? addClause(filter, { ...clause, join: newJoin }) : replaceClause(filter, index, clause));
    setEditing(null);
  };

  const remove = (index: number) => {
    onChange(removeClause(filter, index));
    setEditing(null);
  };

  const joinMenu = (join: UserFilterJoin, onSelect: (join: UserFilterJoin) => void, key: string) => {
    const label = join === 'or' ? t('userFilter.join.or') : t('userFilter.join.and');
    return (
      <Menu key={key} checkedValues={{ join: [join] }} onCheckedValueChange={(_e, data) => onSelect(data.checkedItems[0] as UserFilterJoin)}>
        <MenuTrigger disableButtonEnhancement>
          <Tooltip content={t('userFilter.join.precedence')} relationship="description">
            <MenuButton
              size="small"
              appearance="transparent"
              className={mergeClasses(styles.join, join === 'or' && styles.orJoin)}
              aria-label={t('userFilter.join.menuAria', { join: label })}
            >
              {label}
            </MenuButton>
          </Tooltip>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItemRadio name="join" value="and">
              {t('userFilter.join.andHint')}
            </MenuItemRadio>
            <MenuItemRadio name="join" value="or">
              {t('userFilter.join.orHint')}
            </MenuItemRadio>
          </MenuList>
        </MenuPopover>
      </Menu>
    );
  };

  const pill = (clause: UserFilterClause, index: number) => {
    const described = condition(clause);
    return (
      <Tooltip key={`pill-${index}`} content={described} relationship="description">
        <InteractionTag shape="rounded" size="small" appearance="outline">
          <InteractionTagPrimary
            icon={<DimensionCue kind={isCustomDimension(clause.dimension) ? 'custom' : 'entra'} />}
            hasSecondaryAction
            // The condition in words: the pill's own text uses "=" and "≠", which a screen reader
            // reads as symbols. Fluent labels the remove button with this plus its own label.
            aria-label={described}
            aria-description={t('userFilter.bar.editHint')}
            // Editing needs the attribute list; without it the editor could only claim, wrongly, that
            // the attribute no longer exists. The condition can still be removed.
            onClick={() => {
              if (list) setEditing(index);
            }}
          >
            <span className={styles.pillLabel}>{dimensionLabel(t, clause.dimension, source)}</span>{' '}
            {operatorShortLabel(t, clause.dimension, clause.operator)}{' '}
            <span className={styles.pillValues}>{pillValues(t, clause)}</span>
          </InteractionTagPrimary>
          <InteractionTagSecondary aria-label={t('userFilter.bar.remove')} onClick={() => remove(index)} />
        </InteractionTag>
      </Tooltip>
    );
  };

  const editor = (clause: UserFilterClause, index: number | 'new') => (
    <UserFilterClauseEditor
      key={`editor-${index}`}
      clause={clause}
      dimensions={dimensions}
      isNew={index === 'new'}
      echoNames={echoNames}
      onApply={(next) => apply(index, next)}
      onCancel={() => setEditing(null)}
      onRemove={index === 'new' ? undefined : () => remove(index)}
      validate={(draft) =>
        tooLong(index === 'new' ? addClause(filter, { ...draft, join: newJoin }) : replaceClause(filter, index, draft))
      }
    />
  );

  const item = (index: number) =>
    editing === index ? editor(clauses[index], index) : pill(clauses[index], index);

  const canAdd = clauses.length < MAX_CLAUSES && !!list;

  return (
    <div className={styles.bar} data-print="hide">
      <div className={styles.row} role="group" aria-label={t('userFilter.bar.groupAria')}>
        <span className={styles.lead}>
          <Filter16Regular aria-hidden />
          <Text weight="semibold" size={300}>{t('userFilter.bar.label')}</Text>
        </span>

        {clauses.length === 0 && editing !== 'new' && (
          <Text size={300} className={styles.muted}>{t('userFilter.bar.everyone')}</Text>
        )}

        {groups.flatMap((group, g) => {
          const content = group.flatMap((index, position) => [
            ...(position > 0
              ? [joinMenu(clauses[index].join, (join) => onChange(setJoin(filter, index, join)), `join-${index}`)]
              : []),
            item(index),
          ]);

          const orJoin = g > 0 ? [joinMenu('or', (join) => onChange(setJoin(filter, group[0], join)), `or-${group[0]}`)] : [];

          // Drawn as a box only when there is more than one group - the one case where the reader has
          // to know that AND binds tighter than OR to read the filter correctly.
          return grouped
            ? [
                ...orJoin,
                <span
                  key={`group-${group[0]}`}
                  className={mergeClasses(styles.group, typeof editing === 'number' && group.includes(editing) && styles.editorGroup)}
                >
                  {content}
                </span>,
              ]
            : [...orJoin, ...content];
        })}

        {editing === 'new' && clauses.length > 0 && joinMenu(newJoin, setNewJoin, 'join-new')}
        {editing === 'new' && editor(newClause(''), 'new')}

        {editing !== 'new' && (
          <Button size="small" appearance="subtle" icon={<Add16Regular />} onClick={startAdding} disabled={!canAdd}>
            {t('userFilter.bar.addFilter')}
          </Button>
        )}

        {clauses.length > 0 && editing === null && (
          <Button size="small" appearance="subtle" onClick={() => onChange({ clauses: [] })}>
            {t('userFilter.bar.clearAll')}
          </Button>
        )}

        {loading && !list && <Text size={200} className={styles.muted}>{t('userFilter.bar.loadingDimensions')}</Text>}
      </div>

      {/* Read back only when there are OR groups - the one case where the reader has to know that
          AND binds tighter than OR to read the pills correctly. A plain AND chain reads left to
          right, and the banner above the figures says it in words anyway. */}
      {grouped && (
        <Text size={200} className={styles.muted}>
          {t('userFilter.bar.readback', { description: describeUserFilter(t, filter, source) })}
        </Text>
      )}

      {error && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('userFilter.bar.dimensionsError')}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={reload}>
              {t('userFilter.bar.retry')}
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}
    </div>
  );
}
