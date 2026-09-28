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
import { formatNumber, useT, type TFunction } from '../../i18n';
import type { UserFilter, UserFilterClause, UserFilterDimension, UserFilterJoin } from '../../types/userFilter';
import UserFilterClauseEditor, { DimensionCue } from './UserFilterClauseEditor';
import { describeClause, describeUserFilter, dimensionLabel, operatorShortLabel, valueLabel } from './describeUserFilter';
import {
  MAX_CLAUSES,
  MAX_TEXT_TERMS,
  MAX_VALUES_PER_CLAUSE,
  addClause,
  fitsLimits,
  groupClauseIndexes,
  hasTooManyTextTerms,
  hasTooManyValues,
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

  // The key of the last filter this bar produced itself, so the effect below can tell its own changes
  // from one made elsewhere.
  const emittedKey = useRef<string | null>(null);
  const emit = (next: UserFilter) => {
    emittedKey.current = userFilterKey(next);
    onChange(next);
  };

  // A change made elsewhere - the domain table's Filter button, a different ?filter= link - while a
  // condition is open would leave the editor holding a draft of a clause that has moved or gone, and
  // Apply would write that stale draft over whatever now sits at its index. Close it instead. The
  // bar's own changes are exempt: switching a connector moves no condition, so the draft is still a
  // draft of the condition at its index, and discarding it would throw away work for nothing.
  const filterKey = userFilterKey(filter);
  const lastFilterKey = useRef(filterKey);
  useEffect(() => {
    if (lastFilterKey.current === filterKey) return;
    lastFilterKey.current = filterKey;
    const own = emittedKey.current === filterKey;
    emittedKey.current = null;
    if (!own) setEditing((current) => (typeof current === 'number' ? null : current));
  }, [filterKey]);

  const dimensions: UserFilterDimension[] = list?.dimensions ?? [];
  const source = { dimensions, names: echoNames };
  const clauses = filter.clauses;
  const groups = groupClauseIndexes(filter);
  const grouped = groups.length > 1;

  const condition = (clause: UserFilterClause) =>
    describeClause(t, clause, dimensionLabel(t, clause.dimension, source));

  const tooLong = (candidate: UserFilter) => {
    if (fitsLimits(candidate)) return null;
    if (hasTooManyValues(candidate)) {
      return t('userFilter.editor.tooManyValues', { max: formatNumber(MAX_VALUES_PER_CLAUSE) });
    }
    return hasTooManyTextTerms(candidate)
      ? t('userFilter.editor.tooManyTerms', { max: formatNumber(MAX_TEXT_TERMS) })
      : t('userFilter.editor.tooLong');
  };

  // The filter an editor's draft would produce. The connector belongs to the bar, not the editor: it
  // can be switched while the condition is open, and the editor's copy is the one it opened with.
  const withDraft = (index: number | 'new', draft: UserFilterClause) =>
    index === 'new'
      ? addClause(filter, { ...draft, join: newJoin })
      : replaceClause(filter, index, { ...draft, join: clauses[index]?.join ?? draft.join });

  const startAdding = () => {
    setNewJoin('and');
    setEditing('new');
  };

  const apply = (index: number | 'new', clause: UserFilterClause) => {
    emit(withDraft(index, clause));
    setEditing(null);
  };

  const remove = (index: number) => {
    emit(removeClause(filter, index));
    // Removing a condition renumbers the ones after it, so an open condition's draft would no longer
    // match its index. A condition still being added has no index yet, and is kept.
    setEditing((current) => (current === 'new' ? current : null));
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
    const open = editing === index;
    return (
      <Tooltip key={`pill-${index}`} content={described} relationship="description">
        <InteractionTag shape="rounded" size="small" appearance={open ? 'brand' : 'outline'}>
          <InteractionTagPrimary
            icon={<DimensionCue kind={isCustomDimension(clause.dimension) ? 'custom' : 'entra'} />}
            hasSecondaryAction
            // The condition in words: the pill's own text uses "=" and "≠", which a screen reader
            // reads as symbols. Fluent labels the remove button with this plus its own label.
            aria-label={described}
            aria-description={t('userFilter.bar.editHint')}
            aria-expanded={open}
            // Editing needs the attribute list; without it the editor could only claim, wrongly, that
            // the attribute no longer exists. The condition can still be removed.
            onClick={() => {
              if (list) setEditing(open ? null : index);
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

  const editor = (index: number | 'new') => (
    <UserFilterClauseEditor
      key={`editor-${index}`}
      clause={index === 'new' ? newClause('') : clauses[index]}
      dimensions={dimensions}
      isNew={index === 'new'}
      echoNames={echoNames}
      onApply={(next) => apply(index, next)}
      onCancel={() => setEditing(null)}
      onRemove={index === 'new' ? undefined : () => remove(index)}
      validate={(draft) => tooLong(withDraft(index, draft))}
    />
  );

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
              ? [joinMenu(clauses[index].join, (join) => emit(setJoin(filter, index, join)), `join-${index}`)]
              : []),
            pill(clauses[index], index),
          ]);

          const orJoin = g > 0 ? [joinMenu('or', (join) => emit(setJoin(filter, group[0], join)), `or-${group[0]}`)] : [];

          // Drawn as a box only when there is more than one group - the one case where the reader has
          // to know that AND binds tighter than OR to read the filter correctly.
          return grouped
            ? [
                ...orJoin,
                <span key={`group-${group[0]}`} className={styles.group}>
                  {content}
                </span>,
              ]
            : [...orJoin, ...content];
        })}

        {editing === 'new' && clauses.length > 0 && joinMenu(newJoin, setNewJoin, 'join-new')}
        {editing === 'new' && editor('new')}

        {editing === null && (
          <Button size="small" appearance="subtle" icon={<Add16Regular />} onClick={startAdding} disabled={!canAdd}>
            {t('userFilter.bar.addFilter')}
          </Button>
        )}

        {clauses.length > 0 && editing === null && (
          <Button size="small" appearance="subtle" onClick={() => emit({ clauses: [] })}>
            {t('userFilter.bar.clearAll')}
          </Button>
        )}

        {loading && !list && <Text size={200} className={styles.muted}>{t('userFilter.bar.loadingDimensions')}</Text>}
      </div>

      {/* An existing condition is edited here, under the pills, rather than in place of its pill. The
          connectors stay usable while it is open, and switching one regroups the pills; an editor
          inside a group would be remounted by that regrouping and lose the draft it holds. Its pill
          stays highlighted, so the reader can see which condition this is. */}
      {typeof editing === 'number' && clauses[editing] && editor(editing)}

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
