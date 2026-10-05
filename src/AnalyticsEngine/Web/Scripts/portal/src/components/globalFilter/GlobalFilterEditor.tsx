import { useState } from 'react';
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
  Text,
  Tooltip,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { Add16Regular } from '@fluentui/react-icons';
import { formatNumber, useT, type TFunction, type TranslationKey } from '../../i18n';
import type { UserFilterDimension, UserFilterJoin } from '../../types/userFilter';
import type { GlobalFilterClause, GlobalFilterDefinition } from '../../types/globalFilter';
import UserFilterClauseEditor, { DimensionCue, type UserFilterClauseViewerOptions } from '../userFilter/UserFilterClauseEditor';
import { dimensionLabel, operatorShortLabel, type DimensionNameSource } from '../userFilter/describeUserFilter';
import {
  MANAGEMENT_CHAIN_DIMENSION,
  MAX_CLAUSES,
  MAX_TEXT_TERMS,
  MAX_VALUES_PER_CLAUSE,
  USER_NAME_DIMENSION,
  groupClauseIndexes,
  isCustomDimension,
} from '../userFilter/userFilterModel';
import { VIEWER_OPTION_KEYS, describeGlobalClause, describeGlobalFilter, globalPillValues } from './describeGlobalFilter';
import {
  MANAGER_DIMENSION,
  addGlobalClause,
  globalFilterProblem,
  newGlobalClause,
  removeGlobalClause,
  replaceGlobalClause,
  setGlobalJoin,
  viewerAttributesFor,
  viewerKind,
  type GlobalFilterProblem,
} from './globalFilterModel';

const useStyles = makeStyles({
  editor: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '6px',
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
    maxWidth: '360px',
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

const PROBLEM_KEYS: Record<GlobalFilterProblem, TranslationKey> = {
  tooManyClauses: 'globalFilter.editor.problem.tooManyClauses',
  tooManyValues: 'userFilter.editor.tooManyValues',
  valueTooLong: 'globalFilter.editor.problem.valueTooLong',
  tooManyTerms: 'userFilter.editor.tooManyTerms',
  tooLong: 'globalFilter.editor.problem.tooLong',
  viewerText: 'globalFilter.editor.problem.viewerText',
};

/** Why a definition cannot be saved, in words - or null when it can. */
export function globalFilterProblemText(t: TFunction, filter: GlobalFilterDefinition): string | null {
  const problem = globalFilterProblem(filter);
  if (!problem) return null;
  return t(PROBLEM_KEYS[problem], {
    max: formatNumber(problem === 'tooManyTerms' ? MAX_TEXT_TERMS : problem === 'tooManyClauses' ? MAX_CLAUSES : MAX_VALUES_PER_CLAUSE),
  });
}

/** The line under the editor explaining what each "viewer" option does for the property chosen. */
function viewerHint(t: TFunction, dimension: string): string | null {
  if (dimension === USER_NAME_DIMENSION) return t('globalFilter.viewer.hint.userName');
  if (dimension === MANAGER_DIMENSION) return t('globalFilter.viewer.hint.manager');
  if (dimension === MANAGEMENT_CHAIN_DIMENSION) return t('globalFilter.viewer.hint.managementChain');
  return t('globalFilter.viewer.hint.own');
}

export interface GlobalFilterEditorProps {
  filter: GlobalFilterDefinition;
  onChange: (filter: GlobalFilterDefinition) => void;
  dimensions: UserFilterDimension[];
  /** Names the server knows for custom organisation types, for a condition whose type the list no longer has. */
  names?: Record<string, string> | null;
  disabled?: boolean;
}

/**
 * Builds the global filter: the user filter's pills, joins and condition editor, with one more kind of
 * value - the viewer's own. "Department is the viewer's own value" shows every reader their own department,
 * so one filter can serve a whole organisation of managers.
 */
export default function GlobalFilterEditor({ filter, onChange, dimensions, names, disabled }: GlobalFilterEditorProps) {
  const styles = useStyles();
  const t = useT();
  const [editing, setEditing] = useState<number | 'new' | null>(null);
  const [newJoin, setNewJoin] = useState<UserFilterJoin>('and');

  const source: DimensionNameSource = { dimensions, names };
  const clauses = filter.clauses;
  const groups = groupClauseIndexes(filter);
  const grouped = groups.length > 1;

  const viewer = (clause: GlobalFilterClause | null): UserFilterClauseViewerOptions => ({
    attributesFor: viewerAttributesFor,
    label: (_dimension, attribute) => t(VIEWER_OPTION_KEYS[viewerKind(attribute)]),
    hint: (dimension) => viewerHint(t, dimension),
    initial: clause?.viewerAttribute ?? null,
  });

  const withDraft = (index: number | 'new', draft: GlobalFilterClause): GlobalFilterDefinition =>
    index === 'new'
      ? addGlobalClause(filter, { ...draft, join: newJoin })
      : replaceGlobalClause(filter, index, { ...draft, join: clauses[index]?.join ?? draft.join });

  const apply = (index: number | 'new', draft: GlobalFilterClause) => {
    onChange(withDraft(index, draft));
    setEditing(null);
  };

  const remove = (index: number) => {
    onChange(removeGlobalClause(filter, index));
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
              disabled={disabled}
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

  const pill = (clause: GlobalFilterClause, index: number) => {
    const described = describeGlobalClause(t, clause, 'definition', source);
    const open = editing === index;
    return (
      <Tooltip key={`pill-${index}`} content={described} relationship="description">
        <InteractionTag shape="rounded" size="small" appearance={open ? 'brand' : 'outline'} disabled={disabled}>
          <InteractionTagPrimary
            icon={<DimensionCue kind={isCustomDimension(clause.dimension) ? 'custom' : 'entra'} />}
            hasSecondaryAction
            aria-label={described}
            aria-description={t('userFilter.bar.editHint')}
            aria-expanded={open}
            onClick={() => setEditing(open ? null : index)}
          >
            <span className={styles.pillLabel}>{dimensionLabel(t, clause.dimension, source)}</span>{' '}
            {operatorShortLabel(t, clause.dimension, clause.operator)}{' '}
            <span className={styles.pillValues}>{globalPillValues(t, clause, 'definition')}</span>
          </InteractionTagPrimary>
          <InteractionTagSecondary aria-label={t('userFilter.bar.remove')} onClick={() => remove(index)} />
        </InteractionTag>
      </Tooltip>
    );
  };

  const editor = (index: number | 'new') => {
    const current = index === 'new' ? null : clauses[index];
    return (
      <UserFilterClauseEditor
        key={`editor-${index}`}
        clause={current ?? newGlobalClause()}
        dimensions={dimensions}
        isNew={index === 'new'}
        echoNames={names}
        viewer={viewer(current)}
        onApply={(next, viewerAttribute) => apply(index, { ...next, viewerAttribute })}
        onCancel={() => setEditing(null)}
        onRemove={index === 'new' ? undefined : () => remove(index)}
        validate={(draft, viewerAttribute) => globalFilterProblemText(t, withDraft(index, { ...draft, viewerAttribute }))}
      />
    );
  };

  const canAdd = !disabled && clauses.length < MAX_CLAUSES && dimensions.length > 0;

  return (
    <div className={styles.editor}>
      <div className={styles.row} role="group" aria-label={t('globalFilter.editor.groupAria')}>
        {clauses.length === 0 && editing !== 'new' && (
          <Text size={300} className={styles.muted}>{t('globalFilter.editor.none')}</Text>
        )}

        {groups.flatMap((group, g) => {
          const content = group.flatMap((index, position) => [
            ...(position > 0
              ? [joinMenu(clauses[index].join, (join) => onChange(setGlobalJoin(filter, index, join)), `join-${index}`)]
              : []),
            pill(clauses[index], index),
          ]);
          const orJoin = g > 0 ? [joinMenu('or', (join) => onChange(setGlobalJoin(filter, group[0], join)), `or-${group[0]}`)] : [];
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
          <Button
            size="small"
            appearance="subtle"
            icon={<Add16Regular />}
            onClick={() => {
              setNewJoin('and');
              setEditing('new');
            }}
            disabled={!canAdd}
          >
            {t('globalFilter.editor.add')}
          </Button>
        )}

        {clauses.length > 0 && editing === null && (
          <Button size="small" appearance="subtle" disabled={disabled} onClick={() => onChange({ clauses: [] })}>
            {t('userFilter.bar.clearAll')}
          </Button>
        )}
      </div>

      {typeof editing === 'number' && clauses[editing] && editor(editing)}

      {clauses.length > 0 && (
        <Text size={200} className={styles.muted}>
          {t('globalFilter.editor.readback', { description: describeGlobalFilter(t, clauses, 'definition', source) })}
        </Text>
      )}
    </div>
  );
}
