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
import {
  MANAGEMENT_CHAIN_DIMENSION,
  MAX_VALUE_LENGTH,
  USER_NAME_DIMENSION,
  clauseIsComplete,
  isCustomDimension,
  isTextOperator,
} from './userFilterModel';

/** How many values the picker asks for at a time. The server returns the largest first. */
const VALUE_PAGE = 200;

/** How long to wait after a keystroke before searching the server, when the list is too long to hold. */
const SEARCH_DEBOUNCE_MS = 250;

/**
 * The "(not set)" option's value inside the picker. Never leaves this component - it becomes the
 * clause's `includeNotSet` flag - so it cannot collide with a real value, which could be anything.
 */
const NOT_SET_OPTION = '\u0000not-set';

/**
 * The prefix of a "the viewer's own value" option, followed by the viewer attribute. Like the "(not set)"
 * option it never leaves this component - it becomes the `viewerAttribute` handed to `onApply`.
 */
const VIEWER_OPTION_PREFIX = '\u0000viewer:';

function isViewerOption(value: string): boolean {
  return value.startsWith(VIEWER_OPTION_PREFIX);
}

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

export interface UserFilterClauseViewerOptions {
  /** The viewer's attributes a condition on `dimension` may compare with - none means none is offered. */
  attributesFor: (dimension: string) => readonly string[];
  /** What the option for comparing `dimension` with the viewer's `attribute` is called in the value list. */
  label: (dimension: string, attribute: string) => string;
  /** A line under the editor explaining the options for `dimension`, or null. */
  hint?: (dimension: string) => string | null;
  /** The viewer attribute the condition compares with when the editor opens, if any. */
  initial: string | null;
}

export interface UserFilterClauseEditorProps {
  /** The condition being edited. A new one arrives with an empty dimension. */
  clause: UserFilterClause;
  dimensions: UserFilterDimension[];
  /** True when the condition is being added rather than changed - there is nothing to remove yet. */
  isNew: boolean;
  /** `viewerAttribute` is only ever set when `viewer` is given. */
  onApply: (clause: UserFilterClause, viewerAttribute: string | null) => void;
  onCancel: () => void;
  onRemove?: () => void;
  /** A reason the condition cannot be applied as it stands (the filter would be too long), or null. */
  validate?: (clause: UserFilterClause, viewerAttribute: string | null) => string | null;
  /** Names echoed by the server, so a condition on a type that has since gone still has a label. */
  echoNames?: Record<string, string> | null;
  /**
   * Offered by the administrator's global filter editor only: the value the person viewing the report
   * holds, as one of the values - "Department is the viewer's own department". A reader's own filter
   * never offers it; it is themselves.
   */
  viewer?: UserFilterClauseViewerOptions;
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
  viewer,
}: UserFilterClauseEditorProps) {
  const styles = useStyles();
  const t = useT();

  const [dimension, setDimension] = useState(clause.dimension);
  const [operator, setOperator] = useState<UserFilterOperator>(clause.operator);
  const [values, setValues] = useState<string[]>(clause.values);
  const [includeNotSet, setIncludeNotSet] = useState(clause.includeNotSet);
  const [viewerAttribute, setViewerAttribute] = useState<string | null>(viewer?.initial ?? null);
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
  // One value per person: a count beside each name would say "1 person" 200,000 times, and nobody
  // is without one, so "(not set)" would only ever offer nobody.
  const isUserName = dimension === USER_NAME_DIMENSION;
  const entra = dimensions.filter((d) => d.kind === 'entra');
  const custom = dimensions.filter((d) => d.kind === 'custom');
  const source = { dimensions, names: echoNames };

  // A term typed but not yet added with Enter still counts: "contains", type "smith", Apply is the
  // obvious way to search for smith, and refusing it with "choose a value" would be a trap.
  const pendingTerm = textMatch ? query.trim() : '';
  const draftValues =
    pendingTerm && !values.some((v) => v.toLowerCase() === pendingTerm.toLowerCase()) ? [...values, pendingTerm] : values;

  const draft: UserFilterClause = {
    join: clause.join,
    dimension,
    operator,
    values: draftValues,
    // The management chain has no "(not set)" option: "has no manager" is the Manager attribute's.
    includeNotSet: isChain ? false : includeNotSet,
  };
  // The viewer options offered for this property. Never for a text search: "contains the viewer's own
  // department" is not a comparison anyone means, and the server refuses it.
  const viewerOptions = viewer && dimension && !textMatch ? viewer.attributesFor(dimension) : [];
  const draftViewer = viewerAttribute && viewerOptions.includes(viewerAttribute) ? viewerAttribute : null;
  const complete = dimension !== '' && (clauseIsComplete(draft) || draftViewer !== null);
  const problem = complete && validate ? validate(draft, draftViewer) : null;

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

  // "User name" opens on free text - nobody finds "smith" by scrolling 200,000 names. Remembered as
  // the editor's choice rather than the reader's, so moving on to another property puts that
  // property's value list back instead of leaving it on "contains".
  const operatorChosenByEditor = useRef(false);

  const selectDimension = (key: string) => {
    if (key === dimension) return;
    const next = dimensions.find((d) => d.key === key);
    setDimension(key);
    setValues([]);
    setIncludeNotSet(false);
    setViewerAttribute(null);
    setQuery('');
    setServerSearch('');
    setAttempted(false);
    // The old attribute's values go at once, not when the new ones arrive: until then - or for good,
    // if the request fails - "Sales" would sit under Country, one click from "Country is Sales".
    setOptions([]);
    setTruncated(false);
    setTotalMatching(0);
    setPeopleWithoutValue(0);
    setValuesError(null);
    if (key === USER_NAME_DIMENSION && next?.supportsTextMatch && !isTextOperator(operator)) {
      setOperator('contains');
      operatorChosenByEditor.current = true;
    } else if (operatorChosenByEditor.current || (next && !next.supportsTextMatch && isTextOperator(operator))) {
      setOperator('is');
      operatorChosenByEditor.current = false;
    }
    // Straight on to the values: picking a property is only ever the first half of the job.
    setTimeout(() => valuesInput.current?.focus(), 0);
  };

  const selectOperator = (next: UserFilterOperator) => {
    if (next === operator) return;
    operatorChosenByEditor.current = false;
    // Values picked from a list and terms typed for "contains" are different things; carrying one
    // over as the other would silently change what the condition means.
    if (isTextOperator(next) !== isTextOperator(operator)) {
      setValues([]);
      setIncludeNotSet(false);
      setViewerAttribute(null);
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
    // One viewer value per condition: the one just picked replaces any other.
    const viewers = next.filter(isViewerOption);
    const pickedViewer = isViewerOption(data.value) && viewers.includes(data.value) ? data.value : viewers[viewers.length - 1];
    setViewerAttribute(pickedViewer ? pickedViewer.slice(VIEWER_OPTION_PREFIX.length) : null);
    setIncludeNotSet(next.includes(NOT_SET_OPTION));
    setValues(next.filter((v) => v !== NOT_SET_OPTION && !isViewerOption(v)));
    setQuery('');
  };

  const onInputKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter' || !textMatch) return;
    e.preventDefault();
    // Enter adds the term typed; Enter again, on an empty box, applies - so a name search is
    // "smith", Enter, Enter, without reaching for the mouse.
    if (query.trim()) addTerm(query);
    else apply();
  };

  const apply = () => {
    setAttempted(true);
    if (!complete || problem) return;
    onApply(draft, draftViewer);
  };

  const viewerSelection = draftViewer ? [VIEWER_OPTION_PREFIX + draftViewer] : [];
  const pickerSelection = [...viewerSelection, ...(includeNotSet ? [NOT_SET_OPTION] : []), ...values];
  const viewerLabel = (option: string) => (viewer ? viewer.label(dimension, option.slice(VIEWER_OPTION_PREFIX.length)) : option);
  const viewerHint = viewer?.hint && dimension && viewerOptions.length > 0 ? viewer.hint(dimension) : null;
  const visibleViewerOptions = viewerOptions
    .map((attribute) => VIEWER_OPTION_PREFIX + attribute)
    .filter((option) => !viewerSelection.includes(option))
    .filter((option) => query.trim() === '' || viewerLabel(option).toLowerCase().includes(query.trim().toLowerCase()));
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
                  <Tag key={value} shape="rounded" value={value} icon={isViewerOption(value) ? <Person16Regular /> : undefined}>
                    {isViewerOption(value)
                      ? viewerLabel(value)
                      : value === NOT_SET_OPTION
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
                maxLength={MAX_VALUE_LENGTH}
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
                  ...visibleViewerOptions.map((option) => (
                    <TagPickerOption key={option} value={option} text={viewerLabel(option)}>
                      <span className={styles.optionContent}>
                        <Person16Regular className={styles.cue} />
                        {viewerLabel(option)}
                      </span>
                    </TagPickerOption>
                  )),
                  !includeNotSet && !isChain && !isUserName && query.trim() === '' && (
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
                        {!isUserName && <span className={styles.valueCount}>{countLabel(o.people)}</span>}
                      </span>
                    </TagPickerOption>
                  )),
                  visibleOptions.length === 0 && visibleViewerOptions.length === 0 && !loadingValues && (
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
          {isUserName
            ? t('userFilter.editor.truncatedNames', { shown: formatNumber(options.length), total: formatNumber(totalMatching) })
            : t('userFilter.editor.truncated', { shown: formatNumber(options.length), total: formatNumber(totalMatching) })}
        </Text>
      )}
      {isChain && <Text size={200} className={styles.hint}>{t('userFilter.editor.managementChainHint')}</Text>}
      {isUserName && <Text size={200} className={styles.hint}>{t('userFilter.editor.userNameHint')}</Text>}
      {viewerHint && <Text size={200} className={styles.hint}>{viewerHint}</Text>}
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
