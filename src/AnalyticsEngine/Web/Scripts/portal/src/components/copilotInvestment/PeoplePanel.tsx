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
import { formatNumber, useT } from '../../i18n';
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
  formatMultiple,
  formatShortDate,
} from './investmentShared';

const PAGE_SIZE = 50;
const SEARCH_DEBOUNCE_MS = 350;

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  card: { display: 'flex', flexDirection: 'column', gap: '12px', padding: '16px', overflowX: 'auto' },
  toolbar: { display: 'flex', gap: '12px', alignItems: 'flex-end', flexWrap: 'wrap' },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  flags: { display: 'flex', gap: '4px', flexWrap: 'wrap' },
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
  chips: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  list: { display: 'grid', gridTemplateColumns: 'minmax(140px, 1fr) auto', gap: '4px 16px' },
  code: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
});

export const DEFAULT_PEOPLE_FILTERS: InvestmentPeopleFilters = {
  search: '',
  signals: [],
  flag: null,
  seat: 'all',
  sortBy: 'default',
  sortDesc: true,
};

/** Each person's seat, observed use and modelled value, with a drill-down into what supports the investment. */
export default function PeoplePanel({ query, filters, onFiltersChange }: {
  query: InvestmentQuery;
  filters: InvestmentPeopleFilters;
  onFiltersChange: (filters: InvestmentPeopleFilters) => void;
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
          <Select value={filters.sortBy} onChange={(_e, data) => onFiltersChange({ ...filters, sortBy: data.value, sortDesc: data.value !== 'user' })}>
            <option value="default">{t('copilotInvestment.people.sort.default')}</option>
            <option value="activeDays">{t('copilotInvestment.people.sort.activeDays')}</option>
            <option value="credits">{t('copilotInvestment.people.sort.credits')}</option>
            <option value="valueToCost">{t('copilotInvestment.people.sort.valueToCost')}</option>
            <option value="gap">{t('copilotInvestment.people.sort.gap')}</option>
            <option value="user">{t('copilotInvestment.people.sort.user')}</option>
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
          <Table size="small" aria-label={t('copilotInvestment.people.title')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('copilotInvestment.people.col.person')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.people.col.seat')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.people.col.band')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.people.col.activeDays')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.people.col.apps')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.people.col.cost')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.people.col.value')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.people.col.ratio')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.people.col.credits')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.people.col.studio')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.people.col.signal')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {page.rows.map((row) => {
                const name = row.userPrincipalName ?? String(row.userId);
                return (
                  <TableRow key={row.userId}>
                    <TableCell>
                      <Link as="button" onClick={() => setSelected(row)} aria-label={t('copilotInvestment.people.open', { name })}>
                        {name}
                      </Link>
                      {row.department && (
                        <Text size={100} block className={styles.muted}>
                          {row.department}
                        </Text>
                      )}
                    </TableCell>
                    <TableCell>{t(row.licensed ? 'copilotInvestment.people.seatHeld' : 'copilotInvestment.people.noSeat')}</TableCell>
                    <TableCell>{row.band && BAND_LABEL_KEYS[row.band] ? t(BAND_LABEL_KEYS[row.band]) : '\u2014'}</TableCell>
                    <TableCell className={styles.num}>{formatDecimal(row.activeDaysPerMonth)}</TableCell>
                    <TableCell className={styles.num}>{formatNumber(row.appsUsed)}</TableCell>
                    <TableCell className={styles.num}>{formatMoney(row.cost, currency)}</TableCell>
                    <TableCell className={styles.num}>{formatMoney(row.valueBase, currency)}</TableCell>
                    <TableCell className={styles.num}>{row.valueToCostBase === null ? '\u2014' : formatMultiple(t, row.valueToCostBase)}</TableCell>
                    <TableCell className={styles.num}>{formatCredits(row.credits)}</TableCell>
                    <TableCell className={styles.num}>{formatCredits(row.studioCredits)}</TableCell>
                    <TableCell>
                      <div className={styles.flags}>
                        <Badge appearance="tint" color={signalColour(row.signal)}>
                          {SIGNAL_LABEL_KEYS[row.signal] ? t(SIGNAL_LABEL_KEYS[row.signal]) : row.signal}
                        </Badge>
                        {row.flags
                          .filter((f) => FLAG_LABEL_KEYS[f])
                          .map((f) => (
                            <Badge key={f} appearance="outline" color={f === 'highSpendThinEvidence' || f === 'limitExhausted' ? 'warning' : 'informative'}>
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

      {selected && <PersonDialog query={query} person={selected} onClose={() => setSelected(null)} />}
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
function PersonDialog({ query, person, onClose }: { query: InvestmentQuery; person: InvestmentPersonRow; onClose: () => void }) {
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
                    <Figure label={t('copilotInvestment.person.summary.seat')} value={t(row.licensed ? 'copilotInvestment.people.seatHeld' : 'copilotInvestment.people.noSeat')} />
                    <Figure label={t('copilotInvestment.person.summary.activeDays')} value={formatNumber(row.activeDays)} />
                    <Figure
                      label={t('copilotInvestment.person.summary.weeks')}
                      value={t('copilotInvestment.person.summary.weeksValue', { active: formatNumber(detail.activeWeeks), total: formatNumber(detail.weeks.length) })}
                    />
                    {row.cost !== null && <Figure label={t('copilotInvestment.person.summary.cost')} value={formatMoney(row.cost, currency)} />}
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

                {detail.studioAgents.length > 0 && (
                  <div className={styles.section}>
                    <Text weight="semibold">{t('copilotInvestment.person.studio.title')}</Text>
                    <div className={styles.list}>
                      {detail.studioAgents.map((a) => (
                        <AppLine
                          key={a.agentId}
                          app={a.agentName ?? a.agentId}
                          text={t('copilotInvestment.person.studio.row', { credits: formatCredits(a.credits), days: formatNumber(a.activeDays) })}
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
