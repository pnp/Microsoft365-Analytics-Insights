import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import {
  Button,
  Dropdown,
  Field,
  Option,
  OptionGroup,
  Spinner,
  Tag,
  TagPicker,
  TagPickerControl,
  TagPickerGroup,
  TagPickerInput,
  TagPickerList,
  TagPickerOption,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Organization16Regular, Person16Regular } from '@fluentui/react-icons';
import { fetchUserFilterValues } from '../../api/userFilterApi';
import { formatNumber, plural, useT } from '../../i18n';
import type {
  UserFilterClause,
  UserFilterDimension,
  UserFilterOperator,
  UserFilterValue,
} from '../../types/userFilter';
import { dimensionLabel, operatorLabel, valueLabel } from './describeUserFilter';
import { MANAGEMENT_CHAIN_DIMENSION, clauseIsComplete, isCustomDimension, isTextOperator } from './userFilterModel';

/** How many values the picker asks for at a time. The server returns the largest first. */
const VALUE_PAGE = 200;

/** How long to wait after a keystroke before searching the server, when the list is too long to hold. */
const SEARCH_DEBOUNCE_MS = 250;

/**
 * The "(not set)" option's value inside the picker. Never leaves this component - it becomes the
 * clause's `includeNotSet` flag - so it cannot collide with a real value, which could be anything.
 */
const NOT_SET_OPTION = '\u0000not-set';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow8,
    minWidth: 'min(640px, 100%)',
    maxWidth: '100%',
  },
  fields: {
    display: 'grid',
    gridTemplateColumns: 'minmax(180px, 1.1fr) minmax(140px, 0.8fr) minmax(220px, 1.6fr)',
    gap: '8px',
    alignItems: 'start',
    '@media (max-width: 720px)': {
      gridTemplateColumns: '1fr',
    },
  },
  optionContent: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
  },
  cue: {
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
  },
  customCue: {
    color: tokens.colorPaletteBerryForeground2,
    flexShrink: 0,
  },
  hint: {
    color: tokens.colorNeutralForeground3,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
  },
  actions: {
    display: 'flex',
    gap: '8px',
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  spacer: {
    flexGrow: 1,
  },
  listNote: {
    display: 'block',
    padding: '6px 10px',
    color: tokens.colorNeutralForeground3,
  },
  valueRow: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '16px',
    width: '100%',
  },
  valueCount: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
  },
});

/** The cue that tells a custom organisation type from a standard Entra ID attribute. */
export function DimensionCue({ kind, className }: { kind: 'entra' | 'custom'; className?: string }) {
  const styles = useStyles();
  const t = useT();
  const label = kind === 'custom' ? t('userFilter.editor.kind.custom') : t('userFilter.editor.kind.entra');

  return kind === 'custom' ? (
    <Organization16Regular className={className ?? styles.customCue} aria-label={label} role="img" />
  ) : (
    <Person16Regular className={className ?? styles.cue} aria-label={label} role="img" />
  );
}

export interface UserFilterClauseEditorProps {
  /** The condition being edited. A new one arrives with an empty dimension. */
  clause: UserFilterClause;
  dimensions: UserFilterDimension[];
  /** True when the condition is being added rather than changed - there is nothing to remove yet. */
  isNew: boolean;
  onApply: (clause: UserFilterClause) => void;
  onCancel: () => void;
  onRemove?: () => void;
  /** A reason the condition cannot be applied as it stands (the filter would be too long), or null. */
  validate?: (clause: UserFilterClause) => string | null;
  /** Names echoed by the server, so a condition on a type that has since gone still has a label. */
  echoNames?: Record<string, string> | null;
}

/**
 * Edits one condition: property, operator and values - the three boxes Azure Monitor's metric
 * filter uses, because admins already know how to read them.
 *
 * Shown inline in the bar - under the pills for an existing condition, after them for a new one -
 * rather than in a pop-over: it carries two drop-downs of its own, and a pop-over that closes when
 * one of them is clicked is the most common way this kind of editor becomes unusable.
 */
export default function UserFilterClauseEditor({
  clause,
  dimensions,
  isNew,
  onApply,
  onCancel,
  onRemove,
  validate,
  echoNames,
}: UserFilterClauseEditorProps) {
  const styles = useStyles();
  const t = useT();

  const [dimension, setDimension] = useState(clause.dimension);
  const [operator, setOperator] = useState<UserFilterOperator>(clause.operator);
  const [values, setValues] = useState<string[]>(clause.values);
  const [includeNotSet, setIncludeNotSet] = useState(clause.includeNotSet);
  const [query, setQuery] = useState('');
  const [options, setOptions] = useState<UserFilterValue[]>([]);
  const [truncated, setTruncated] = useState(false);
  const [totalMatching, setTotalMatching] = useState(0);
  const [peopleWithoutValue, setPeopleWithoutValue] = useState(0);
  const [loadingValues, setLoadingValues] = useState(false);
  const [valuesError, setValuesError] = useState<string | null>(null);
  const [attempted, setAttempted] = useState(false);
  const [serverSearch, setServerSearch] = useState('');
  const valuesInput = useRef<HTMLInputElement>(null);
  const propertyButton = useRef<HTMLButtonElement>(null);

  // An existing condition opens under the pills, not where its pill sits, so take the reader to it.
  // A new one needs no help: its property list opens by itself.
  useEffect(() => {
    if (!isNew) propertyButton.current?.focus();
  }, [isNew]);

  const selected = dimensions.find((d) => d.key === dimension) ?? null;
  const unknownDimension = dimension !== '' && selected === null;
  const textMatch = isTextOperator(operator);
  const isChain = dimension === MANAGEMENT_CHAIN_DIMENSION;
  const entra = dimensions.filter((d) => d.kind === 'entra');
  const custom = dimensions.filter((d) => d.kind === 'custom');
  const source = { dimensions, names: echoNames };

  const draft: UserFilterClause = {
    join: clause.join,
    dimension,
    operator,
    values,
    // The management chain has no "(not set)" option: "has no manager" is the Manager attribute's.
    includeNotSet: isChain ? false : includeNotSet,
  };
  const complete = dimension !== '' && clauseIsComplete(draft);
  const problem = complete && validate ? validate(draft) : null;

  // The values of the chosen attribute, largest first. Refetched when the attribute changes, and when
  // a search is typed into a list too long to hold in the page.
  useEffect(() => {
    if (!dimension || unknownDimension || textMatch) {
      // Also stops the spinner: switching to "contains" mid-fetch aborts the request, and an aborted
      // request deliberately leaves the loading flag alone for the fetch that replaces it.
      setOptions([]);
      setTruncated(false);
      setLoadingValues(false);
      return;
    }

    const controller = new AbortController();
    setLoadingValues(true);
    setValuesError(null);

    fetchUserFilterValues(dimension, serverSearch, VALUE_PAGE, controller.signal)
      .then((page) => {
        setOptions(page.values);
        setTruncated(page.truncated);
        setTotalMatching(page.totalMatching);
        setPeopleWithoutValue(page.peopleWithoutValue);
      })
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setValuesError(e instanceof Error ? e.message : t('userFilter.editor.valuesError'));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoadingValues(false);
      });

    return () => controller.abort();
  }, [dimension, unknownDimension, textMatch, serverSearch, t]);

  // Only a list the server cut short needs the server to search it; anything else filters in place.
  useEffect(() => {
    if (!truncated && serverSearch === '') return;
    const timer = setTimeout(() => setServerSearch(query.trim()), SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [query, truncated, serverSearch]);

  const visibleOptions = useMemo(() => {
    const term = query.trim().toLowerCase();
    const chosen = new Set(values.map((v) => v.toLowerCase()));
    return options
      .filter((o) => !chosen.has(o.value.toLowerCase()))
      .filter((o) => term === '' || valueLabel(t, dimension, o.value).toLowerCase().includes(term));
  }, [options, values, query, dimension, t]);

  const selectDimension = (key: string) => {
    if (key === dimension) return;
    const next = dimensions.find((d) => d.key === key);
    setDimension(key);
    setValues([]);
    setIncludeNotSet(false);
    setQuery('');
    setServerSearch('');
    setAttempted(false);
    if (next && !next.supportsTextMatch && isTextOperator(operator)) setOperator('is');
    // Straight on to the values: picking a property is only ever the first half of the job.
    setTimeout(() => valuesInput.current?.focus(), 0);
  };

  const selectOperator = (next: UserFilterOperator) => {
    if (next === operator) return;
    // Values picked from a list and terms typed for "contains" are different things; carrying one
    // over as the other would silently change what the condition means.
    if (isTextOperator(next) !== isTextOperator(operator)) {
      setValues([]);
      setIncludeNotSet(false);
    }
    setOperator(next);
  };

  const addTerm = (raw: string) => {
    const term = raw.trim();
    if (!term) return;
    if (!values.some((v) => v.toLowerCase() === term.toLowerCase())) setValues([...values, term]);
    setQuery('');
  };

  const onPickerSelect = (_e: unknown, data: { value: string; selectedOptions: string[] }) => {
    const next = data.selectedOptions;
    setIncludeNotSet(next.includes(NOT_SET_OPTION));
    setValues(next.filter((v) => v !== NOT_SET_OPTION));
    setQuery('');
  };

  const onInputKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter' && textMatch && query.trim()) {
      e.preventDefault();
      addTerm(query);
    }
  };

  const apply = () => {
    setAttempted(true);
    if (!complete || problem) return;
    onApply(draft);
  };

  const pickerSelection = includeNotSet ? [NOT_SET_OPTION, ...values] : values;
  const countLabel = (people: number) =>
    isChain
      ? t(plural(people, 'userFilter.editor.below.one', 'userFilter.editor.below.other'), { count: formatNumber(people) })
      : t(plural(people, 'userFilter.editor.people.one', 'userFilter.editor.people.other'), { count: formatNumber(people) });

  const operators: UserFilterOperator[] =
    selected?.supportsTextMatch ? ['is', 'isNot', 'contains', 'notContains'] : ['is', 'isNot'];

  return (
    <div className={styles.card} role="group" aria-label={t('userFilter.editor.title')}>
      <div className={styles.fields}>
        <Field label={t('userFilter.editor.property')}>
          <Dropdown
            ref={propertyButton}
            aria-label={t('userFilter.editor.property')}
            placeholder={t('userFilter.editor.selectProperty')}
            value={dimension ? dimensionLabel(t, dimension, source) : ''}
            selectedOptions={dimension ? [dimension] : []}
            onOptionSelect={(_e: unknown, data: { optionValue?: string }) => data.optionValue && selectDimension(data.optionValue)}
            defaultOpen={isNew && !clause.dimension}
          >
            <OptionGroup label={t('userFilter.editor.group.entra')}>
              {entra.map((d) => (
                <Option key={d.key} value={d.key} text={dimensionLabel(t, d.key, source)}>
                  <span className={styles.optionContent}>
                    <DimensionCue kind="entra" />
                    {dimensionLabel(t, d.key, source)}
                  </span>
                </Option>
              ))}
            </OptionGroup>
            <OptionGroup label={t('userFilter.editor.group.custom')}>
              {custom.length === 0 ? (
                <Option key="none" value="" disabled text={t('userFilter.editor.noCustomTypes')}>
                  {t('userFilter.editor.noCustomTypes')}
                </Option>
              ) : (
                custom.map((d) => (
                  <Option key={d.key} value={d.key} text={dimensionLabel(t, d.key, source)}>
                    <span className={styles.optionContent}>
                      <DimensionCue kind="custom" />
                      {dimensionLabel(t, d.key, source)}
                    </span>
                  </Option>
                ))
              )}
            </OptionGroup>
          </Dropdown>
        </Field>

        <Field label={t('userFilter.editor.operator')}>
          <Dropdown
            aria-label={t('userFilter.editor.operator')}
            value={operatorLabel(t, dimension, operator)}
            selectedOptions={[operator]}
            onOptionSelect={(_e: unknown, data: { optionValue?: string }) =>
              data.optionValue && selectOperator(data.optionValue as UserFilterOperator)
            }
            disabled={!dimension}
          >
            {operators.map((op) => (
              <Option key={op} value={op} text={operatorLabel(t, dimension, op)}>
                {operatorLabel(t, dimension, op)}
              </Option>
            ))}
          </Dropdown>
        </Field>

        <Field
          label={textMatch ? t('userFilter.editor.terms') : t('userFilter.editor.values')}
          validationState={attempted && !complete ? 'error' : undefined}
          validationMessage={attempted && !complete ? t('userFilter.editor.chooseValue') : undefined}
        >
          <TagPicker
            selectedOptions={pickerSelection}
            onOptionSelect={onPickerSelect}
            disabled={!dimension || unknownDimension}
          >
            <TagPickerControl>
              <TagPickerGroup aria-label={textMatch ? t('userFilter.editor.terms') : t('userFilter.editor.values')}>
                {pickerSelection.map((value) => (
                  <Tag key={value} shape="rounded" value={value}>
                    {value === NOT_SET_OPTION
                      ? t('userFilter.editor.notSet')
                      : textMatch
                        ? t('userFilter.describe.quoted', { term: value })
                        : valueLabel(t, dimension, value)}
                  </Tag>
                ))}
              </TagPickerGroup>
              <TagPickerInput
                ref={valuesInput}
                aria-label={textMatch ? t('userFilter.editor.terms') : t('userFilter.editor.values')}
                placeholder={
                  pickerSelection.length > 0
                    ? undefined
                    : textMatch
                      ? t('userFilter.editor.typeTerm')
                      : t('userFilter.editor.selectValues')
                }
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={onInputKeyDown}
              />
            </TagPickerControl>
            <TagPickerList>
              {textMatch ? (
                query.trim() ? (
                  <TagPickerOption value={query.trim()} text={query.trim()}>
                    {t('userFilter.editor.addTerm', { term: query.trim() })}
                  </TagPickerOption>
                ) : (
                  <span className={styles.listNote}>{t('userFilter.editor.typeTerm')}</span>
                )
              ) : (
                [
                  !includeNotSet && !isChain && query.trim() === '' && (
                    <TagPickerOption
                      key={NOT_SET_OPTION}
                      value={NOT_SET_OPTION}
                      text={t('userFilter.editor.notSet')}
                      title={t('userFilter.editor.notSetHint')}
                    >
                      <span className={styles.valueRow}>
                        <span>{t('userFilter.editor.notSet')}</span>
                        <span className={styles.valueCount}>{countLabel(peopleWithoutValue)}</span>
                      </span>
                    </TagPickerOption>
                  ),
                  ...visibleOptions.map((o) => (
                    <TagPickerOption key={o.value} value={o.value} text={valueLabel(t, dimension, o.value)}>
                      <span className={styles.valueRow}>
                        <span>{valueLabel(t, dimension, o.value)}</span>
                        <span className={styles.valueCount}>{countLabel(o.people)}</span>
                      </span>
                    </TagPickerOption>
                  )),
                  visibleOptions.length === 0 && !loadingValues && (
                    <span key="none" className={styles.listNote}>
                      {valuesError ?? t('userFilter.editor.noValues')}
                    </span>
                  ),
                ]
              )}
            </TagPickerList>
          </TagPicker>
        </Field>
      </div>

      {loadingValues && <Spinner size="extra-tiny" label={t('userFilter.editor.loadingValues')} labelPosition="after" />}
      {valuesError && <Text size={200} className={styles.error}>{valuesError}</Text>}
      {truncated && !loadingValues && (
        <Text size={200} className={styles.hint}>
          {t('userFilter.editor.truncated', { shown: formatNumber(options.length), total: formatNumber(totalMatching) })}
        </Text>
      )}
      {isChain && <Text size={200} className={styles.hint}>{t('userFilter.editor.managementChainHint')}</Text>}
      {unknownDimension && <Text size={200} className={styles.error}>{t('userFilter.editor.unknownDimension')}</Text>}
      {problem && <Text size={200} className={styles.error}>{problem}</Text>}

      <div className={styles.actions}>
        <Button appearance="primary" size="small" onClick={apply} disabled={!dimension || (attempted && (!complete || !!problem))}>
          {t('userFilter.editor.apply')}
        </Button>
        <Button size="small" onClick={onCancel}>
          {t('userFilter.editor.cancel')}
        </Button>
        <div className={styles.spacer} />
        {!isNew && onRemove && (
          <Button size="small" appearance="subtle" onClick={onRemove}>
            {t('userFilter.editor.remove')}
          </Button>
        )}
        {isCustomDimension(dimension) && selected && (
          <Text size={200} className={styles.hint}>
            <DimensionCue kind="custom" /> {t('userFilter.editor.kind.custom')}
          </Text>
        )}
      </div>
    </div>
  );
}
