import { useMemo, useState } from 'react';
import {
  Button,
  Card,
  Checkbox,
  Input,
  Switch,
  Text,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { ArrowReset20Regular, Search16Regular } from '@fluentui/react-icons';
import { formatNumber, plural, useT, type TranslationKey } from '../../i18n';
import { usePortalAccess } from '../../access';
import type {
  ActivityAnalysisLicence,
  ActivityAnalysisMetric,
  ActivityAnalysisRangeMaximum,
} from '../../types/activityAnalysis';
import UserFilterBar from '../userFilter/UserFilterBar';
import { formatCompact } from '../charts/chartCommon';
import {
  NO_FILTERS,
  applyDraft,
  draftFrom,
  filtersKey,
  type AppliedFilters,
  type FilterDraft,
  type RangeError,
  type RangeText,
} from './filters';
import { categoryLabel, isDuration, metricLabelWithUnit, secondsToHours, type MetricGroup } from './metrics';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    marginTop: '12px',
    padding: '16px',
  },
  head: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    paddingTop: '12px',
    borderTopWidth: '1px',
    borderTopStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke3,
  },
  sectionHead: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  search: {
    maxWidth: '320px',
  },
  licenceList: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))',
    columnGap: '12px',
  },
  // Only a long list scrolls: a handful of licences should simply be on the page.
  licenceListLong: {
    maxHeight: '224px',
    overflowY: 'auto',
    paddingRight: '4px',
  },
  licenceCount: {
    marginLeft: '6px',
    color: tokens.colorNeutralForeground3,
    fontVariantNumeric: 'tabular-nums',
  },
  rangeGroups: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(340px, 1fr))',
    gap: '12px 24px',
  },
  rangeGroup: {
    margin: 0,
    padding: 0,
    border: 'none',
    minWidth: 0,
  },
  legend: {
    padding: 0,
    marginBottom: '4px',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  rangeHeader: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 104px 104px',
    gap: '6px',
    color: tokens.colorNeutralForeground3,
  },
  rangeRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 104px 104px',
    alignItems: 'center',
    gap: '6px',
    paddingBlock: '2px',
  },
  rangeLabel: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  rangeInput: {
    width: '104px',
    minWidth: 0,
  },
  rangeError: {
    gridColumn: '1 / -1',
    color: tokens.colorPaletteRedForeground1,
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
    paddingTop: '12px',
    borderTopWidth: '1px',
    borderTopStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke3,
  },
  footerText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  warning: {
    color: tokens.colorPaletteDarkOrangeForeground1,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
  },
  buttons: {
    display: 'flex',
    gap: '8px',
  },
});

const RANGE_ERROR_KEYS: Record<RangeError, TranslationKey> = {
  number: 'activityAnalysis.filters.ranges.error.number',
  whole: 'activityAnalysis.filters.ranges.error.whole',
  order: 'activityAnalysis.filters.ranges.error.order',
};

/** Above this many licences the list gets a search box. */
const LICENCE_SEARCH_THRESHOLD = 8;

interface FiltersPanelProps {
  id: string;
  draft: FilterDraft;
  onDraftChange: (draft: FilterDraft) => void;
  applied: AppliedFilters;
  onApply: (applied: AppliedFilters) => void;
  groups: MetricGroup[];
  order: string[];
  metrics: ReadonlyMap<string, ActivityAnalysisMetric>;
  /** Licences held by the population, from the latest report. */
  licences: ActivityAnalysisLicence[];
  rangeMaxima: ActivityAnalysisRangeMaximum[];
  /** The administrator's names for custom organisation types, echoed with the last report. */
  echoNames?: Record<string, string> | null;
  /** People matching the applied filters, from the report on screen. */
  matchingPeople: number | null;
}

/**
 * The Power BI "Filter Settings" view: who the analysis covers. People's directory attributes (the
 * shared user filter), the licences they hold, and ranges on their total activity over the period.
 *
 * Edits stay a draft until "Apply filters", so a reader can set several conditions without the page
 * re-running the analysis after every keystroke.
 *
 * The people filter, the licences and the activity ranges all need See PII: any condition the reader
 * chooses can be differenced against the report without it to single one person out, so the API refuses
 * them all for anyone else. Without See PII the panel only says so - nothing in it could be applied.
 */
export default function FiltersPanel({
  id,
  draft,
  onDraftChange,
  applied,
  onApply,
  groups,
  order,
  metrics,
  licences,
  rangeMaxima,
  echoNames,
  matchingPeople,
}: FiltersPanelProps) {
  const styles = useStyles();
  const t = useT();
  const access = usePortalAccess();
  const [licenceSearch, setLicenceSearch] = useState('');
  const [showAllMetrics, setShowAllMetrics] = useState(false);

  const { applied: next, errors } = useMemo(() => applyDraft(draft, order, metrics), [draft, order, metrics]);
  const invalid = Object.keys(errors).length > 0;
  const dirty = filtersKey(next) !== filtersKey(applied);

  const maxima = useMemo(() => new Map(rangeMaxima.map((m) => [m.metric, m.max])), [rangeMaxima]);

  const setRange = (key: string, text: RangeText) => onDraftChange({ ...draft, ranges: { ...draft.ranges, [key]: text } });

  const toggleLicence = (licenceId: number) =>
    onDraftChange({
      ...draft,
      licences: draft.licences.includes(licenceId)
        ? draft.licences.filter((l) => l !== licenceId)
        : [...draft.licences, licenceId],
    });

  // A licence chosen earlier stays listed even if this period's population no longer holds it, so it
  // can still be seen and unticked.
  const licenceOptions = useMemo(() => {
    const known = new Set(licences.map((l) => l.id));
    const missing = draft.licences
      .filter((l) => !known.has(l))
      .map((l): ActivityAnalysisLicence => ({ id: l, name: t('activityAnalysis.filters.licences.unknown', { id: String(l) }), skuId: null, people: 0 }));
    return [...licences, ...missing];
  }, [licences, draft.licences, t]);

  const search = licenceSearch.trim().toLowerCase();
  const shownLicences = search ? licenceOptions.filter((l) => l.name.toLowerCase().includes(search)) : licenceOptions;

  // The core metrics, plus any other metric that already has a range - so hiding the full list never
  // hides a filter that is in force.
  const rangeGroups = groups
    .map((group) => ({
      category: group.category,
      metrics: group.metrics.filter((m) => {
        const text = draft.ranges[m.key];
        return showAllMetrics || m.core || (text && (text.min.trim() !== '' || text.max.trim() !== ''));
      }),
    }))
    .filter((group) => group.metrics.length > 0);

  const maxPlaceholder = (metric: ActivityAnalysisMetric): string | undefined => {
    const max = maxima.get(metric.key);
    if (max == null) return undefined;
    const shown = isDuration(metric) ? secondsToHours(max) : max;
    // Exact while it fits the box; compact ("27.7k") beyond, where the digits would be cut off anyway.
    const value = shown >= 10000 ? formatCompact(shown) : formatNumber(shown, { maximumFractionDigits: 1 });
    return t('activityAnalysis.filters.ranges.maxPlaceholder', { value });
  };

  return (
    <Card id={id} className={styles.card} data-print="hide" role="region" aria-labelledby={`${id}-title`}>
      <div className={styles.head}>
        <Text id={`${id}-title`} as="h2" weight="semibold" size={500} style={{ margin: 0 }}>
          {t('activityAnalysis.filters.title')}
        </Text>
        <Text size={200} className={styles.muted}>
          {access.seePii ? t('activityAnalysis.filters.intro') : t('activityAnalysis.filters.piiHidden', { role: access.roles.seePii })}
        </Text>
      </div>

      {access.seePii && (
        <>
          <section className={styles.section} aria-labelledby={`${id}-people`}>
            <Text id={`${id}-people`} as="h3" weight="semibold" size={400} style={{ margin: 0 }}>
              {t('activityAnalysis.filters.people.heading')}
            </Text>
            <UserFilterBar
              filter={draft.userFilter}
              onChange={(userFilter) => onDraftChange({ ...draft, userFilter })}
              echoNames={echoNames}
            />
          </section>

          <section className={styles.section} aria-labelledby={`${id}-licences`}>
            <div className={styles.sectionHead}>
              <div>
                <Text id={`${id}-licences`} as="h3" weight="semibold" size={400} block style={{ margin: 0 }}>
                  {t('activityAnalysis.filters.licences.heading')}
                </Text>
                <Text size={200} className={styles.muted}>
                  {t('activityAnalysis.filters.licences.hint')}
                </Text>
              </div>
              {licenceOptions.length > LICENCE_SEARCH_THRESHOLD && (
                <Input
                  className={styles.search}
                  size="small"
                  contentBefore={<Search16Regular />}
                  value={licenceSearch}
                  placeholder={t('activityAnalysis.filters.licences.search')}
                  aria-label={t('activityAnalysis.filters.licences.search')}
                  onChange={(_e, data) => setLicenceSearch(data.value)}
                />
              )}
            </div>

            {licenceOptions.length === 0 ? (
              <Text size={200} className={styles.muted}>
                {t('activityAnalysis.filters.licences.none')}
              </Text>
            ) : shownLicences.length === 0 ? (
              <Text size={200} className={styles.muted}>
                {t('activityAnalysis.filters.licences.noMatch', { search: licenceSearch.trim() })}
              </Text>
            ) : (
              <div
                className={mergeClasses(styles.licenceList, shownLicences.length > LICENCE_SEARCH_THRESHOLD * 2 ? styles.licenceListLong : undefined)}
                role="group"
                aria-labelledby={`${id}-licences`}
              >
                {shownLicences.map((licence) => (
                  <Checkbox
                    key={licence.id}
                    checked={draft.licences.includes(licence.id)}
                    onChange={() => toggleLicence(licence.id)}
                    label={
                      <>
                        {licence.name}
                        <span className={styles.licenceCount} title={t('activityAnalysis.filters.licences.peopleTitle')}>
                          {formatNumber(licence.people)}
                        </span>
                      </>
                    }
                  />
                ))}
              </div>
            )}
          </section>

          <section className={styles.section} aria-labelledby={`${id}-ranges`}>
            <div className={styles.sectionHead}>
              <div>
                <Text id={`${id}-ranges`} as="h3" weight="semibold" size={400} block style={{ margin: 0 }}>
                  {t('activityAnalysis.filters.ranges.heading')}
                </Text>
                <Text size={200} className={styles.muted}>
                  {t('activityAnalysis.filters.ranges.hint')}
                </Text>
              </div>
              <Switch
                checked={showAllMetrics}
                onChange={(_e, data) => setShowAllMetrics(data.checked)}
                label={t('activityAnalysis.filters.ranges.showAll')}
              />
            </div>

            <div className={styles.rangeGroups}>
              {rangeGroups.map((group) => (
                <fieldset key={group.category} className={styles.rangeGroup}>
                  <legend className={styles.legend}>{categoryLabel(t, group.category)}</legend>
                  <div className={styles.rangeHeader} aria-hidden="true">
                    <span />
                    <Text size={200}>{t('activityAnalysis.filters.ranges.min')}</Text>
                    <Text size={200}>{t('activityAnalysis.filters.ranges.max')}</Text>
                  </div>
                  {group.metrics.map((metric) => {
                    const text = draft.ranges[metric.key] ?? { min: '', max: '' };
                    const error = errors[metric.key];
                    const label = metricLabelWithUnit(t, metric.key, metric);
                    const step = isDuration(metric) ? 0.5 : 1;
                    return (
                      <div key={metric.key} className={styles.rangeRow}>
                        <Text size={300} className={styles.rangeLabel} title={label}>
                          {label}
                        </Text>
                        <Input
                          className={styles.rangeInput}
                          type="number"
                          size="small"
                          min={0}
                          step={step}
                          inputMode="decimal"
                          value={text.min}
                          aria-label={t('activityAnalysis.filters.ranges.minAria', { metric: label })}
                          aria-invalid={error ? true : undefined}
                          onChange={(_e, data) => setRange(metric.key, { ...text, min: data.value })}
                        />
                        <Input
                          className={styles.rangeInput}
                          type="number"
                          size="small"
                          min={0}
                          step={step}
                          inputMode="decimal"
                          value={text.max}
                          placeholder={maxPlaceholder(metric)}
                          aria-label={t('activityAnalysis.filters.ranges.maxAria', { metric: label })}
                          aria-invalid={error ? true : undefined}
                          onChange={(_e, data) => setRange(metric.key, { ...text, max: data.value })}
                        />
                        {error && (
                          <Text size={200} role="alert" className={styles.rangeError}>
                            {t(RANGE_ERROR_KEYS[error])}
                          </Text>
                        )}
                      </div>
                    );
                  })}
                </fieldset>
              ))}
            </div>
          </section>

          <div className={styles.footer}>
            <div className={styles.footerText}>
              {matchingPeople != null && (
                <Text size={200}>
                  {t(plural(matchingPeople, 'activityAnalysis.filters.matching.one', 'activityAnalysis.filters.matching.other'), {
                    count: formatNumber(matchingPeople),
                  })}
                </Text>
              )}
              {invalid ? (
                <Text size={200} className={styles.error}>
                  {t('activityAnalysis.filters.invalid')}
                </Text>
              ) : dirty ? (
                <Text size={200} className={styles.warning} aria-live="polite">
                  {t('activityAnalysis.filters.pending')}
                </Text>
              ) : null}
            </div>
            <div className={styles.buttons}>
              <Button
                icon={<ArrowReset20Regular />}
                title={t('activityAnalysis.filters.resetHint')}
                onClick={() => onDraftChange(draftFrom(NO_FILTERS, metrics))}
              >
                {t('activityAnalysis.filters.reset')}
              </Button>
              <Button appearance="primary" disabled={!dirty || invalid} onClick={() => onApply(next)}>
                {t('activityAnalysis.filters.apply')}
              </Button>
            </div>
          </div>
        </>
      )}
    </Card>
  );
}
