import { useEffect, useMemo, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  Link,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Checkmark16Filled, Dismiss16Regular } from '@fluentui/react-icons';
import { formatNumber, useT, type TFunction, type TranslationKey } from '../../i18n';
import { fetchInvestmentPeople, fetchInvestmentPerson, type InvestmentQuery } from '../../api/copilotInvestmentApi';
import type { InvestmentPeopleFilters, InvestmentPeoplePage, InvestmentPersonDetail, InvestmentPersonRow } from '../../types/copilotInvestment';
import {
  BAND_LABEL_KEYS,
  EVIDENCE_LABEL_KEYS,
  FLAG_LABEL_KEYS,
  REASON_LABEL_KEYS,
  SIGNAL_LABEL_KEYS,
  SectionHeading,
  formatCredits,
  formatDecimal,
  formatMoney,
  formatShortDate,
} from './investmentShared';

const PAGE_SIZE = 50;
const SEARCH_DEBOUNCE_MS = 350;

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  card: { display: 'flex', flexDirection: 'column', gap: '12px', padding: '16px', overflowX: 'auto' },
  // Wide enough for a sign-in name, scrolling sideways inside the card on a narrow screen rather than overlapping.
  table: { minWidth: '1080px', tableLayout: 'auto' },
  personCol: { width: '26%', minWidth: '220px' },
  personLink: { textAlign: 'start', overflowWrap: 'anywhere' },
  narrowCol: { width: '88px' },
  signalCol: { width: '170px' },
  toolbar: { display: 'flex', gap: '12px', alignItems: 'flex-end', flexWrap: 'wrap' },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  // A sortable header's label sits in Fluent's own button; numeric columns keep it, and the arrow, at the right edge.
  numHeader: { justifyContent: 'flex-end', textAlign: 'right' },
  licence: { display: 'inline-flex', alignItems: 'center', justifyContent: 'center', minWidth: '20px' },
  licensed: { color: tokens.colorPaletteGreenForeground1 },
  notLicensed: { color: tokens.colorNeutralForeground3 },
  flags: { display: 'flex', gap: '4px', flexWrap: 'wrap' },
  badge: { whiteSpace: 'nowrap', flexShrink: 0 },
  pager: { display: 'flex', gap: '12px', alignItems: 'center', justifyContent: 'flex-end' },
  muted: { color: tokens.colorNeutralForeground3 },
  dialog: { maxWidth: 'min(960px, calc(100vw - 32px))', width: '100%' },
  detailGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))', gap: '12px' },
  figure: { display: 'flex', flexDirection: 'column', gap: '2px' },
  figureValue: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums' },
  section: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '16px' },
  weeks: { display: 'flex', gap: '3px', alignItems: 'flex-end', height: '44px' },
  week: { flex: '1 1 0', minWidth: '6px', backgroundColor: tokens.colorBrandBackground, borderRadius: '2px' },
  weekEmpty: { backgroundColor: tokens.colorNeutralBackground5 },
  weekAxis: { display: 'flex', justifyContent: 'space-between' },
  chips: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  list: { display: 'grid', gridTemplateColumns: 'minmax(140px, 1fr) auto', gap: '4px 16px' },
  code: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
});

/** A sortable column of the people table, by the server's sort key. */
type PeopleSortKey = 'user' | 'licensed' | 'band' | 'activeDays' | 'breakEven' | 'apps' | 'cost' | 'credits' | 'studioCredits' | 'signal';

/**
 * The columns, in order. A column sorts the whole list on the server - every person the filters keep, before paging - and
 * its first click goes the useful way: names A to Z, everything else largest (or strongest) first.
 */
const COLUMNS: { key: PeopleSortKey; labelKey: TranslationKey; numeric: boolean; firstDescending: boolean }[] = [
  { key: 'user', labelKey: 'copilotInvestment.people.col.person', numeric: false, firstDescending: false },
  { key: 'licensed', labelKey: 'copilotInvestment.people.col.seat', numeric: false, firstDescending: true },
  { key: 'band', labelKey: 'copilotInvestment.people.col.band', numeric: false, firstDescending: true },
  { key: 'activeDays', labelKey: 'copilotInvestment.people.col.activeDays', numeric: true, firstDescending: true },
  { key: 'breakEven', labelKey: 'copilotInvestment.people.col.need', numeric: true, firstDescending: true },
  { key: 'apps', labelKey: 'copilotInvestment.people.col.apps', numeric: true, firstDescending: true },
  { key: 'cost', labelKey: 'copilotInvestment.people.col.cost', numeric: true, firstDescending: true },
  { key: 'credits', labelKey: 'copilotInvestment.people.col.credits', numeric: true, firstDescending: true },
  { key: 'studioCredits', labelKey: 'copilotInvestment.people.col.studio', numeric: true, firstDescending: true },
  { key: 'signal', labelKey: 'copilotInvestment.people.col.signal', numeric: false, firstDescending: true },
];

/** The direction a sort key starts in: the "Investment" ranking and every column but names are read largest first. */
function firstDescending(sortBy: string): boolean {
  return COLUMNS.find((c) => c.key === sortBy)?.firstDescending ?? true;
}

/**
 * Whether a person holds a Microsoft 365 Copilot licence, as a tick or a cross with its words for a screen reader and on
 * hover - never colour alone. Without adoption evidence the licences were never read, which is "not known", not "no".
 */
function LicenceMark({ licensed, known, t }: { licensed: boolean; known: boolean; t: TFunction }) {
  const styles = useStyles();
  if (!known) {
    const label = t('copilotInvestment.people.licence.unknown');
    return (
      <span role="img" aria-label={label} title={label} className={styles.licence}>
        {'\u2014'}
      </span>
    );
  }
  const label = t(licensed ? 'copilotInvestment.people.licence.yes' : 'copilotInvestment.people.licence.no');
  return (
    <span role="img" aria-label={label} title={label} className={`${styles.licence} ${licensed ? styles.licensed : styles.notLicensed}`}>
      {licensed ? <Checkmark16Filled aria-hidden /> : <Dismiss16Regular aria-hidden />}
    </span>
  );
}

export const DEFAULT_PEOPLE_FILTERS: InvestmentPeopleFilters = {
  search: '',
  signals: [],
  flag: null,
  seat: 'all',
  sortBy: 'default',
  sortDesc: true,
};

/** Each person's licence, observed use and modelled value, with a drill-down into what supports the investment. */
export default function PeoplePanel({ query, filters, onFiltersChange, licenceKnown = true }: {
  query: InvestmentQuery;
  filters: InvestmentPeopleFilters;
  onFiltersChange: (filters: InvestmentPeopleFilters) => void;
  /** False when no adoption evidence was read, so nobody's licence is known either way. */
  licenceKnown?: boolean;
}) {
  const styles = useStyles();
  const t = useT();
  const [searchText, setSearchText] = useState(filters.search);
  const [page, setPage] = useState<InvestmentPeoplePage | null>(null);
  const [paging, setPaging] = useState<{ key: string; skip: number }>({ key: '', skip: 0 });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<InvestmentPersonRow | null>(null);

  // Typing is debounced so a search sends one request, not one per keystroke.
  useEffect(() => {
    if (searchText === filters.search) return undefined;
    const timer = setTimeout(() => onFiltersChange({ ...filters, search: searchText }), SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [searchText, filters, onFiltersChange]);

  const queryKey = JSON.stringify(query);
  const filtersKey = JSON.stringify(filters);
  // The page offset belongs to one query and one set of filters: changing either starts again at the top,
  // derived here rather than reset by an effect that would fetch the old offset first.
  const scopeKey = `${queryKey}|${filtersKey}`;
  const skip = paging.key === scopeKey ? paging.skip : 0;
  const setSkip = (next: number) => setPaging({ key: scopeKey, skip: next });

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetchInvestmentPeople(query, filters, skip, PAGE_SIZE, controller.signal)
      .then((result) => setPage(result))
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
    // The keys stand for the objects: a new object with the same content must not refetch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKey, filtersKey, skip]);

  const currency = page?.currency ?? null;
  const total = page?.total ?? 0;
  // Clicking the column the list is sorted by reverses it; another column starts in its own useful direction.
  const sortOn = (key: PeopleSortKey) =>
    onFiltersChange({ ...filters, sortBy: key, sortDesc: filters.sortBy === key ? !filters.sortDesc : firstDescending(key) });

  return (
    <div className={styles.stack}>
      <SectionHeading title={t('copilotInvestment.people.title')} description={t('copilotInvestment.people.description')} />
      <div className={styles.toolbar} data-print="hide">
        <Field label={t('copilotInvestment.people.search')}>
          <Input
            value={searchText}
            onChange={(_e, data) => setSearchText(data.value)}
            placeholder={t('copilotInvestment.people.searchPlaceholder')}
          />
        </Field>
        <Field label={t('copilotInvestment.people.seat')}>
          <Select value={filters.seat} onChange={(_e, data) => onFiltersChange({ ...filters, seat: data.value as InvestmentPeopleFilters['seat'] })}>
            <option value="all">{t('copilotInvestment.people.seat.all')}</option>
            <option value="licensed">{t('copilotInvestment.people.seat.licensed')}</option>
            <option value="unlicensed">{t('copilotInvestment.people.seat.unlicensed')}</option>
          </Select>
        </Field>
        <Field label={t('copilotInvestment.people.signal')}>
          <Select
            value={filters.signals[0] ?? ''}
            onChange={(_e, data) => onFiltersChange({ ...filters, signals: data.value ? [data.value] : [] })}
          >
            <option value="">{t('copilotInvestment.people.signal.any')}</option>
            {Object.entries(SIGNAL_LABEL_KEYS).map(([code, key]) => (
              <option key={code} value={code}>
                {t(key)}
              </option>
            ))}
          </Select>
        </Field>
        <Field label={t('copilotInvestment.people.flag')}>
          <Select value={filters.flag ?? ''} onChange={(_e, data) => onFiltersChange({ ...filters, flag: data.value || null })}>
            <option value="">{t('copilotInvestment.people.flag.any')}</option>
            {Object.entries(FLAG_LABEL_KEYS).map(([code, key]) => (
              <option key={code} value={code}>
                {t(key)}
              </option>
            ))}
          </Select>
        </Field>
        <Field label={t('copilotInvestment.people.sort')}>
          {/* The same sort as the column headers: choosing here moves the header arrow, and a header click shows here. */}
          <Select value={filters.sortBy} onChange={(_e, data) => onFiltersChange({ ...filters, sortBy: data.value, sortDesc: firstDescending(data.value) })}>
            <option value="default">{t('copilotInvestment.people.sort.default')}</option>
            {COLUMNS.map((c) => (
              <option key={c.key} value={c.key}>
                {t(c.labelKey)}
              </option>
            ))}
            <option value="gap">{t('copilotInvestment.people.sort.gap')}</option>
          </Select>
        </Field>
      </div>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      <Card className={styles.card}>
        {loading && !page ? (
          <Spinner size="small" label={t('copilotInvestment.people.loading')} />
        ) : page && page.rows.length === 0 ? (
          <Text>{t('copilotInvestment.people.empty')}</Text>
        ) : page ? (
          <Table size="small" sortable aria-label={t('copilotInvestment.people.title')} className={styles.table}>
            <TableHeader>
              <TableRow>
                {COLUMNS.map((c) => {
                  const label = t(c.labelKey);
                  return (
                    <TableHeaderCell
                      key={c.key}
                      className={c.key === 'user' ? styles.personCol : c.key === 'signal' ? styles.signalCol : c.numeric ? styles.num : styles.narrowCol}
                      button={c.numeric ? { className: styles.numHeader } : undefined}
                      sortDirection={filters.sortBy === c.key ? (filters.sortDesc ? 'descending' : 'ascending') : undefined}
                      onClick={() => sortOn(c.key)}
                      title={t('copilotInvestment.people.sortColumn', { column: label })}
                    >
                      {label}
                    </TableHeaderCell>
                  );
                })}
              </TableRow>
            </TableHeader>
            <TableBody>
              {page.rows.map((row) => {
                const name = row.userPrincipalName ?? String(row.userId);
                return (
                  <TableRow key={row.userId}>
                    <TableCell className={styles.personCol}>
                      <Link as="button" className={styles.personLink} onClick={() => setSelected(row)} aria-label={t('copilotInvestment.people.open', { name })}>
                        {name}
                      </Link>
                      {row.department && (
                        <Text size={100} block className={styles.muted}>
                          {row.department}
                        </Text>
                      )}
                    </TableCell>
                    <TableCell>
                      <LicenceMark licensed={row.licensed} known={licenceKnown} t={t} />
                    </TableCell>
                    <TableCell>{row.band && BAND_LABEL_KEYS[row.band] ? t(BAND_LABEL_KEYS[row.band]) : '\u2014'}</TableCell>
                    <TableCell className={styles.num}>{formatDecimal(row.activeDaysPerMonth)}</TableCell>
                    <TableCell className={styles.num}>{formatDecimal(row.breakEvenDaysPerMonth)}</TableCell>
                    <TableCell className={styles.num}>{formatNumber(row.appsUsed)}</TableCell>
                    <TableCell className={styles.num}>{formatMoney(row.cost, currency)}</TableCell>
                    <TableCell className={styles.num}>{formatCredits(row.credits)}</TableCell>
                    <TableCell className={styles.num}>{formatCredits(row.studioCredits)}</TableCell>
                    <TableCell className={styles.signalCol}>
                      <div className={styles.flags}>
                        <Badge className={styles.badge} appearance="tint" color={signalColour(row.signal)}>
                          {SIGNAL_LABEL_KEYS[row.signal] ? t(SIGNAL_LABEL_KEYS[row.signal]) : row.signal}
                        </Badge>
                        {row.flags
                          .filter((f) => FLAG_LABEL_KEYS[f])
                          .map((f) => (
                            <Badge key={f} className={styles.badge} appearance="outline" color={f === 'highSpendThinEvidence' || f === 'limitExhausted' ? 'warning' : 'informative'}>
                              {t(FLAG_LABEL_KEYS[f])}
                            </Badge>
                          ))}
                      </div>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        ) : null}
        {page && page.total > 0 && (
          <div className={styles.pager} data-print="hide">
            <Text size={200}>
              {t('copilotInvestment.people.count', { shown: formatNumber(Math.min(total, skip + page.rows.length)), total: formatNumber(total) })}
            </Text>
            <Button size="small" disabled={skip === 0 || loading} onClick={() => setSkip(Math.max(0, skip - PAGE_SIZE))}>
              {t('copilotInvestment.people.previous')}
            </Button>
            <Button size="small" disabled={skip + PAGE_SIZE >= total || loading} onClick={() => setSkip(skip + PAGE_SIZE)}>
              {t('copilotInvestment.people.next')}
            </Button>
          </div>
        )}
      </Card>

      {selected && <PersonDialog query={query} person={selected} licenceKnown={licenceKnown} onClose={() => setSelected(null)} />}
    </div>
  );
}

function signalColour(signal: string): 'success' | 'warning' | 'danger' | 'informative' | 'brand' {
  switch (signal) {
    case 'supported':
    case 'strongEvidence':
      return 'success';
    case 'plausible':
    case 'developing':
    case 'expandCandidate':
      return 'brand';
    case 'belowBreakEven':
    case 'lightEvidence':
      return 'warning';
    case 'noEvidence':
      return 'danger';
    default:
      return 'informative';
  }
}

/** One person's evidence: where, how often and with what they use Copilot, and what their balance is. */
function PersonDialog({ query, person, licenceKnown, onClose }: {
  query: InvestmentQuery;
  person: InvestmentPersonRow;
  licenceKnown: boolean;
  onClose: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const [detail, setDetail] = useState<InvestmentPersonDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const name = person.userPrincipalName ?? String(person.userId);
  const queryKey = JSON.stringify(query);

  useEffect(() => {
    const controller = new AbortController();
    setDetail(null);
    setError(null);
    fetchInvestmentPerson(query, person.userId, controller.signal)
      .then(setDetail)
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e));
      });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKey, person.userId]);

  const maxWeek = useMemo(() => Math.max(1, ...(detail?.weeks ?? []).map((w) => w.activeDays)), [detail]);
  const currency = detail?.currency ?? null;
  const row = detail?.person ?? person;

  return (
    <Dialog open onOpenChange={(_e, data) => !data.open && onClose()}>
      <DialogSurface className={styles.dialog}>
        <DialogBody>
          <DialogTitle>{t('copilotInvestment.person.title', { name })}</DialogTitle>
          <DialogContent>
            <Text size={200} block className={styles.muted}>
              {t('copilotInvestment.person.caveat')}
            </Text>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            {!detail && !error && <Spinner size="small" label={t('copilotInvestment.person.loading')} />}
            {detail && (
              <>
                <div className={styles.section}>
                  <div className={styles.detailGrid}>
                    <Figure
                      label={t('copilotInvestment.person.summary.seat')}
                      value={t(!licenceKnown ? 'copilotInvestment.people.licence.unknown' : row.licensed ? 'copilotInvestment.people.seatHeld' : 'copilotInvestment.people.noSeat')}
                    />
                    <Figure label={t('copilotInvestment.person.summary.activeDays')} value={formatNumber(row.activeDays)} />
                    <Figure
                      label={t('copilotInvestment.person.summary.weeks')}
                      value={t('copilotInvestment.person.summary.weeksValue', { active: formatNumber(detail.activeWeeks), total: formatNumber(detail.weeks.length) })}
                    />
                    {row.cost !== null && <Figure label={t('copilotInvestment.person.summary.cost')} value={formatMoney(row.cost, currency)} />}
                    {row.breakEvenDaysPerMonth !== null && (
                      <Figure
                        label={t('copilotInvestment.person.summary.need')}
                        value={t('copilotInvestment.person.summary.needValue', {
                          need: formatDecimal(row.breakEvenDaysPerMonth),
                          actual: formatDecimal(row.activeDaysPerMonth),
                        })}
                      />
                    )}
                    {row.valueBase !== null && (
                      <Figure
                        label={t('copilotInvestment.person.summary.value')}
                        value={formatMoney(row.valueBase, currency)}
                        note={t('copilotInvestment.person.summary.valueRange', {
                          low: formatMoney(row.valueConservative, currency),
                          high: formatMoney(row.valueUpside, currency),
                        })}
                      />
                    )}
                    {row.hoursBase !== null && <Figure label={t('copilotInvestment.person.summary.hours')} value={formatDecimal(row.hoursBase)} />}
                  </div>
                </div>

                <div className={styles.section}>
                  <Text weight="semibold">{t('copilotInvestment.person.evidenceTitle')}</Text>
                  <div className={styles.chips}>
                    {detail.evidence
                      .filter((code) => EVIDENCE_LABEL_KEYS[code])
                      .map((code) => (
                        <Badge key={code} appearance="tint" color={code === 'declining' || code === 'noActivity' || code === 'rare' ? 'warning' : 'informative'}>
                          {t(EVIDENCE_LABEL_KEYS[code])}
                        </Badge>
                      ))}
                  </div>
                </div>

                {detail.weeks.length > 0 && (
                  <div className={styles.section}>
                    <Text weight="semibold">{t('copilotInvestment.person.weeks.title')}</Text>
                    <div className={styles.weeks} role="list" aria-label={t('copilotInvestment.person.weeks.title')}>
                      {detail.weeks.map((w) => {
                        const label = t('copilotInvestment.person.weeks.cell', {
                          week: formatShortDate(w.weekStart),
                          days: formatNumber(w.activeDays),
                          turns: formatNumber(w.turns),
                        });
                        return (
                          <div
                            key={w.weekStart}
                            role="listitem"
                            aria-label={label}
                            title={label}
                            className={`${styles.week} ${w.activeDays === 0 ? styles.weekEmpty : ''}`}
                            style={{ height: `${Math.max(8, (w.activeDays / maxWeek) * 100)}%` }}
                          />
                        );
                      })}
                    </div>
                    <div className={styles.weekAxis}>
                      <Text size={100} className={styles.muted}>
                        {formatShortDate(detail.weeks[0].weekStart)}
                      </Text>
                      <Text size={100} className={styles.muted}>
                        {formatShortDate(detail.weeks[detail.weeks.length - 1].weekStart)}
                      </Text>
                    </div>
                    <Text size={100} className={styles.muted}>
                      {t('copilotInvestment.person.weeks.caption')}
                    </Text>
                  </div>
                )}

                <div className={styles.section}>
                  <Text weight="semibold">{t('copilotInvestment.person.apps.title')}</Text>
                  {detail.apps.length === 0 ? (
                    <Text size={200}>{t('copilotInvestment.person.apps.none')}</Text>
                  ) : (
                    <div className={styles.list}>
                      {detail.apps.map((app) => (
                        <AppLine key={app.app} app={app.app} text={t('copilotInvestment.person.apps.row', { turns: formatNumber(app.turns), days: formatNumber(app.days) })} />
                      ))}
                    </div>
                  )}
                </div>

                <div className={styles.section}>
                  <Text weight="semibold">{t('copilotInvestment.person.agents.title')}</Text>
                  <div className={styles.list}>
                    <Text size={200}>{t('copilotInvestment.person.agents.totalTurns')}</Text>
                    <Text size={200} weight="semibold">{formatNumber(detail.totalTurns)}</Text>
                    <Text size={200}>{t('copilotInvestment.person.agents.agentTurns')}</Text>
                    <Text size={200} weight="semibold">{formatNumber(detail.agentTurns)}</Text>
                    <Text size={200}>{t('copilotInvestment.person.agents.coworkTurns')}</Text>
                    <Text size={200} weight="semibold">{formatNumber(detail.coworkTurns)}</Text>
                  </div>
                </div>

                <div className={styles.section}>
                  <Text weight="semibold">{t('copilotInvestment.person.prompts.title')}</Text>
                  {detail.promptsAssessed ? (
                    <>
                      <Text size={200}>
                        {t('copilotInvestment.person.prompts.counts', { prompts: formatNumber(detail.prompts), responses: formatNumber(detail.responses) })}
                      </Text>
                      {detail.promptCategories.length > 0 && (
                        <>
                          <Text size={200} weight="semibold">
                            {t('copilotInvestment.person.prompts.categories')}
                          </Text>
                          <div className={styles.list}>
                            {detail.promptCategories.slice(0, 8).map((c) => (
                              <AppLine key={c.categoryId} app={c.categoryId} text={formatNumber(c.prompts)} />
                            ))}
                          </div>
                        </>
                      )}
                    </>
                  ) : (
                    <Text size={200}>{t('copilotInvestment.person.prompts.notAssessed')}</Text>
                  )}
                </div>

                <div className={styles.section}>
                  <Text weight="semibold">{t('copilotInvestment.person.credits.title')}</Text>
                  {detail.creditServices.length === 0 ? (
                    <Text size={200}>{t('copilotInvestment.person.credits.none')}</Text>
                  ) : (
                    <div className={styles.list}>
                      {detail.creditServices.map((s) => (
                        <AppLine
                          key={s.category}
                          app={s.category}
                          text={
                            s.unevaluatedReason
                              ? `${t('copilotInvestment.person.credits.unevaluated')} \u00b7 ${t(REASON_LABEL_KEYS[s.unevaluatedReason] ?? 'copilotInvestment.reason.other')}`
                              : [
                                  formatCredits(s.consumed),
                                  s.decisionReason ? t(REASON_LABEL_KEYS[s.decisionReason] ?? 'copilotInvestment.reason.other') : null,
                                  s.perUserLimitKind === 'limited' && s.perUserLimit !== null
                                    ? t('copilotInvestment.person.credits.limit', { limit: formatCredits(s.perUserLimit) })
                                    : s.perUserLimitKind === 'unlimited'
                                      ? t('copilotInvestment.value.unlimited')
                                      : null,
                                  s.remaining !== null ? t('copilotInvestment.person.credits.remaining', { remaining: formatCredits(s.remaining) }) : null,
                                ]
                                  .filter(Boolean)
                                  .join(' \u00b7 ')
                          }
                        />
                      ))}
                    </div>
                  )}
                  {detail.balances.length > 1 && (
                    <>
                      <Text size={200} weight="semibold">
                        {t('copilotInvestment.person.credits.history')}
                      </Text>
                      <Text size={200}>
                        {detail.balances
                          .map((b) => t('copilotInvestment.person.credits.historyPoint', { date: formatShortDate(b.date), credits: formatCredits(b.credits) }))
                          .join(' \u00b7 ')}
                      </Text>
                    </>
                  )}
                </div>

                {detail.studioEnvironments.length > 0 && (
                  <div className={styles.section}>
                    <Text weight="semibold">{t('copilotInvestment.person.studio.title')}</Text>
                    <div className={styles.list}>
                      {detail.studioEnvironments.map((e) => (
                        <AppLine
                          key={e.environmentId ?? '-'}
                          app={e.environmentName ?? e.environmentId ?? t('copilotInvestment.person.studio.unnamed')}
                          text={t('copilotInvestment.person.studio.row', { credits: formatCredits(e.credits), days: formatNumber(e.activeDays) })}
                        />
                      ))}
                    </div>
                  </div>
                )}
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>
              {t('copilotInvestment.person.close')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function Figure({ label, value, note }: { label: string; value: string; note?: string }) {
  const styles = useStyles();
  return (
    <div className={styles.figure}>
      <Text size={200} className={styles.muted}>
        {label}
      </Text>
      <span className={styles.figureValue}>{value}</span>
      {note && <Text size={100}>{note}</Text>}
    </div>
  );
}

/** A name from the tenant (an app, a service, an agent) shown verbatim, beside a translated description. */
function AppLine({ app, text }: { app: string; text: string }) {
  const styles = useStyles();
  return (
    <>
      <span className={styles.code}>{app}</span>
      <Text size={200}>{text}</Text>
    </>
  );
}
