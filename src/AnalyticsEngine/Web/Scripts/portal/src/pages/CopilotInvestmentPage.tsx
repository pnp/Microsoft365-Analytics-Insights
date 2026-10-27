import { useCallback, useEffect, useMemo, useState } from 'react';
import { useInRouterContext, useSearchParams } from 'react-router-dom';
import {
  Body1,
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Select,
  Tab,
  TabList,
  Text,
  Title3,
  Tooltip,
  makeStyles,
  tokens,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular, ArrowDownload16Regular, Beaker20Regular } from '@fluentui/react-icons';
import { formatNumber, useI18n, useT, type TranslationKey } from '../i18n';
import { usePortalAccess } from '../access';
import Spinner from '../components/Spinner';
import PrintButton from '../components/shared/PrintButton';
import GlobalFilterBar from '../components/globalFilter/GlobalFilterBar';
import UserFilterBar from '../components/userFilter/UserFilterBar';
import UserFilterPrintSummary from '../components/userFilter/UserFilterPrintSummary';
import { useUserFilterDimensions } from '../components/userFilter/useUserFilterDimensions';
import { parseUserFilter, serializeUserFilter } from '../components/userFilter/userFilterModel';
import { EMPTY_USER_FILTER, type UserFilter } from '../types/userFilter';
import {
  fetchInvestmentAvailability,
  fetchInvestmentSummary,
  fetchInvestmentTrend,
  investmentWorkbookUrl,
  type InvestmentQuery,
} from '../api/copilotInvestmentApi';
import type {
  CopilotInvestmentAvailability,
  CopilotInvestmentSummary,
  InvestmentAssumptions,
  InvestmentPeopleFilters,
  InvestmentScenarioOverrides,
  InvestmentTrend,
} from '../types/copilotInvestment';
import ExecutivePanel from '../components/copilotInvestment/ExecutivePanel';
import ValuePanel from '../components/copilotInvestment/ValuePanel';
import OrganisationPanel from '../components/copilotInvestment/OrganisationPanel';
import CreditsPanel from '../components/copilotInvestment/CreditsPanel';
import PeoplePanel, { DEFAULT_PEOPLE_FILTERS } from '../components/copilotInvestment/PeoplePanel';
import MethodPanel from '../components/copilotInvestment/MethodPanel';
import WhatIfDialog, { countOverrides } from '../components/copilotInvestment/WhatIfDialog';
import { AVAILABILITY_NOTE_KEYS, FIELD_LABEL_KEYS, formatShortDate } from '../components/copilotInvestment/investmentShared';

type InvestmentTab = 'executive' | 'value' | 'organisation' | 'credits' | 'people' | 'method';

const TAB_LABEL_KEYS: Record<InvestmentTab, TranslationKey> = {
  executive: 'copilotInvestment.tab.executive',
  value: 'copilotInvestment.tab.value',
  organisation: 'copilotInvestment.tab.organisation',
  credits: 'copilotInvestment.tab.credits',
  people: 'copilotInvestment.tab.people',
  method: 'copilotInvestment.tab.method',
};

const WINDOWS: { days: number; labelKey: TranslationKey }[] = [
  { days: 7, labelKey: 'copilotInvestment.period.last7' },
  { days: 28, labelKey: 'copilotInvestment.period.last28' },
  { days: 90, labelKey: 'copilotInvestment.period.last90' },
  { days: 180, labelKey: 'copilotInvestment.period.last180' },
];

/** The administration page where the organisation's assumptions are kept. */
export const ASSUMPTIONS_SETTINGS_HREF = '#/admin/copilot-investment-assumptions';

const useStyles = makeStyles({
  header: { display: 'flex', flexDirection: 'column', gap: '8px' },
  toolbar: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', marginTop: '12px' },
  spacer: { flexGrow: 1 },
  scope: { display: 'flex', gap: '12px', flexWrap: 'wrap', color: tokens.colorNeutralForeground3, marginTop: '8px' },
  // The tab strip scrolls sideways on a narrow screen rather than widening the page past it.
  tabs: { marginTop: '16px', marginBottom: '16px', overflowX: 'auto', maxWidth: '100%' },
  notes: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '12px' },
});

/** The URL parameter the reader's own filter is kept in, as on the Copilot Adoption page. */
export const USER_FILTER_URL_PARAM = 'filter';

export default function CopilotInvestmentPage() {
  return useInRouterContext() ? <RoutedInvestmentPage /> : <InvestmentView />;
}

function RoutedInvestmentPage() {
  const [params, setParams] = useSearchParams();
  const raw = params.get(USER_FILTER_URL_PARAM);
  const userFilter = useMemo(() => parseUserFilter(raw), [raw]);
  const onUserFilterChange = useCallback(
    (filter: UserFilter) => {
      setParams(
        (current) => {
          const next = new URLSearchParams(current);
          const serialized = serializeUserFilter(filter);
          if (serialized) next.set(USER_FILTER_URL_PARAM, serialized);
          else next.delete(USER_FILTER_URL_PARAM);
          return next;
        },
        { replace: true },
      );
    },
    [setParams],
  );
  return <InvestmentView userFilter={userFilter} onUserFilterChange={onUserFilterChange} />;
}

function InvestmentView({ userFilter: controlledFilter, onUserFilterChange }: {
  userFilter?: UserFilter;
  onUserFilterChange?: (filter: UserFilter) => void;
}) {
  const styles = useStyles();
  const t = useT();
  const { language } = useI18n();
  const access = usePortalAccess();
  const canSeePii = access.seePii;
  const isAdmin = access.administration;

  const [availability, setAvailability] = useState<CopilotInvestmentAvailability | null>(null);
  const [availabilityError, setAvailabilityError] = useState<string | null>(null);
  const [windowDays, setWindowDays] = useState(28);
  const [dimension, setDimension] = useState('department');
  const [scenario, setScenario] = useState<InvestmentScenarioOverrides | null>(null);
  // True while the figures in force are the illustrative example, so the page says so beside every modelled amount.
  const [exampleInUse, setExampleInUse] = useState(false);
  const [whatIfOpen, setWhatIfOpen] = useState(false);
  const [tab, setTab] = useState<InvestmentTab>('executive');
  const [reloadKey, setReloadKey] = useState(0);
  const [peopleFilters, setPeopleFilters] = useState<InvestmentPeopleFilters>(DEFAULT_PEOPLE_FILTERS);

  // A reader without See PII sees aggregates only: no filter of their own, no manager grouping.
  const [ownFilter, setOwnFilter] = useState<UserFilter>(EMPTY_USER_FILTER);
  const requestedFilter = controlledFilter ?? ownFilter;
  const userFilter = canSeePii ? requestedFilter : EMPTY_USER_FILTER;
  const setUserFilter = useCallback(
    (next: UserFilter) => {
      if (controlledFilter === undefined) setOwnFilter(next);
      onUserFilterChange?.(next);
    },
    [controlledFilter, onUserFilterChange],
  );
  const userFilterParam = serializeUserFilter(userFilter);
  const effectiveDimension = !canSeePii && (dimension === 'manager' || dimension.startsWith('org:')) ? 'department' : dimension;
  const { list: filterDimensions } = useUserFilterDimensions(canSeePii);

  const query: InvestmentQuery = useMemo(
    () => ({ windowDays, userFilter: userFilterParam, dimension: effectiveDimension, scenario }),
    [windowDays, userFilterParam, effectiveDimension, scenario],
  );
  const queryKey = JSON.stringify(query);

  const [summary, setSummary] = useState<CopilotInvestmentSummary | null>(null);
  const [summaryKey, setSummaryKey] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // The organisation's own figures, remembered from the last report read without what-if figures, so the
  // what-if dialog can show what each figure replaces.
  const [tenantDefaults, setTenantDefaults] = useState<InvestmentAssumptions | null>(null);

  const [trend, setTrend] = useState<InvestmentTrend | null>(null);
  const [trendKey, setTrendKey] = useState<string | null>(null);
  const [trendLoading, setTrendLoading] = useState(false);
  const [trendError, setTrendError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchInvestmentAvailability(controller.signal)
      .then(setAvailability)
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setAvailabilityError(e instanceof Error ? e.message : String(e));
      });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetchInvestmentSummary(query, controller.signal)
      .then((result) => {
        setSummary(result);
        setSummaryKey(queryKey);
        if (!result.assumptions.scenarioApplied && result.assumptions.values) setTenantDefaults(result.assumptions.values);
      })
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
    // The key stands for the query: a new object with the same content must not refetch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKey, reloadKey]);

  // The balance history is only read when someone opens the tab that shows it.
  const trendScope = `${windowDays}|${userFilterParam ?? ''}|${reloadKey}`;
  useEffect(() => {
    if (tab !== 'credits' || trendKey === trendScope) return undefined;
    const controller = new AbortController();
    setTrendLoading(true);
    setTrendError(null);
    fetchInvestmentTrend({ windowDays, userFilter: userFilterParam }, controller.signal)
      .then((result) => {
        setTrend(result);
        setTrendKey(trendScope);
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setTrendError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => {
        if (!controller.signal.aborted) setTrendLoading(false);
      });
    return () => controller.abort();
  }, [tab, trendScope, trendKey, windowDays, userFilterParam]);

  const visibleTabs = (Object.keys(TAB_LABEL_KEYS) as InvestmentTab[]).filter((value) => canSeePii || value !== 'people');
  const activeTab: InvestmentTab = visibleTabs.includes(tab) ? tab : 'executive';
  const onTabSelect: SelectTabEventHandler = (_e, data) => setTab(data.value as InvestmentTab);
  // Only the figures for the scope on screen are shown: a stale report under a new filter would be read as the new one.
  const current = summary && summaryKey === queryKey ? summary : null;
  const overrides = countOverrides(scenario);

  return (
    <div>
      <div className={styles.header}>
        <Title3>{t('copilotInvestment.page.title')}</Title3>
        <Body1 block>{t('copilotInvestment.page.intro')}</Body1>
      </div>

      <div className={styles.toolbar} data-print="hide">
        <Select value={String(windowDays)} onChange={(_e, data) => setWindowDays(Number(data.value))} aria-label={t('copilotInvestment.period.ariaLabel')}>
          {WINDOWS.map((w) => (
            <option key={w.days} value={w.days}>
              {t(w.labelKey)}
            </option>
          ))}
        </Select>
        <Button
          appearance={overrides > 0 ? 'primary' : 'secondary'}
          icon={<Beaker20Regular />}
          onClick={() => setWhatIfOpen(true)}
        >
          {overrides > 0 ? t('copilotInvestment.controls.whatIfActive', { count: formatNumber(overrides) }) : t('copilotInvestment.controls.whatIf')}
        </Button>
        <Button appearance="subtle" icon={<ArrowClockwise16Regular />} onClick={() => setReloadKey((k) => k + 1)} disabled={loading}>
          {t('common.action.refresh')}
        </Button>
        <span className={styles.spacer} />
        <PrintButton tooltip={t('copilotInvestment.controls.print')} />
        <Tooltip
          relationship="description"
          content={
            current
              ? t(canSeePii ? 'copilotInvestment.controls.excelReady' : 'copilotInvestment.controls.excelReadyNoPii')
              : t('copilotInvestment.controls.excelLoading')
          }
        >
          <Button
            appearance="primary"
            icon={<ArrowDownload16Regular />}
            as="a"
            href={current ? investmentWorkbookUrl(query, language) : undefined}
            disabled={!current}
          >
            {t('copilotInvestment.controls.excel')}
          </Button>
        </Tooltip>
      </div>

      {current && (
        <div className={styles.scope}>
          <Text size={200}>
            {t('copilotInvestment.page.range', {
              from: formatShortDate(current.fromUtc),
              to: formatShortDate(current.toUtc),
              days: formatNumber(current.windowDays),
            })}
          </Text>
          <Text size={200}>{t('copilotInvestment.page.generated', { when: formatShortDate(current.generatedUtc) })}</Text>
          {current.filtered && <Text size={200}>{t('copilotInvestment.page.filtered')}</Text>}
        </div>
      )}

      <GlobalFilterBar />

      {canSeePii && (
        <>
          <UserFilterBar filter={userFilter} onChange={setUserFilter} echoNames={current?.userFilter?.dimensionNames} />
          <UserFilterPrintSummary
            filter={userFilter}
            echo={current?.userFilter ?? undefined}
            dimensions={filterDimensions?.dimensions}
            withinGlobalFilter={!!current?.globalFilter}
          />
        </>
      )}

      <div className={styles.notes}>
        {exampleInUse && scenario && (
          <MessageBar intent="warning">
            <MessageBarBody>{t('copilotInvestment.whatIf.example.active')}</MessageBarBody>
            <MessageBarActions>
              <Button
                size="small"
                onClick={() => {
                  setScenario(null);
                  setExampleInUse(false);
                }}
              >
                {t('copilotInvestment.whatIf.example.clear')}
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}
        {availabilityError && (
          <MessageBar intent="error">
            <MessageBarBody>{availabilityError}</MessageBarBody>
          </MessageBar>
        )}
        {(availability?.notes ?? [])
          .filter((note) => AVAILABILITY_NOTE_KEYS[note])
          .map((note) => (
            <MessageBar key={note} intent="info">
              <MessageBarBody>{t(AVAILABILITY_NOTE_KEYS[note])}</MessageBarBody>
            </MessageBar>
          ))}
        {error && (
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
      </div>

      <div className={styles.tabs} data-print="hide">
        <TabList selectedValue={activeTab} onTabSelect={onTabSelect}>
          {visibleTabs.map((value) => (
            <Tab key={value} value={value}>
              {t(TAB_LABEL_KEYS[value])}
            </Tab>
          ))}
        </TabList>
      </div>

      {loading && !current && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={64} label={t('copilotInvestment.page.loading')} />
        </div>
      )}

      {current && (
        <div data-print="flow">
          {activeTab === 'executive' && (
            <ExecutivePanel
              summary={current}
              canSeePii={canSeePii}
              navigation={{
                seePeople: canSeePii
                  ? ({ signal, flag }) => {
                      setPeopleFilters({ ...DEFAULT_PEOPLE_FILTERS, signals: signal ? [signal] : [], flag: flag ?? null });
                      setTab('people');
                    }
                  : undefined,
                openTab: (next) => setTab(next),
                openWhatIf: () => setWhatIfOpen(true),
                settingsHref: isAdmin ? ASSUMPTIONS_SETTINGS_HREF : undefined,
              }}
            />
          )}
          {activeTab === 'value' && <ValuePanel summary={current} />}
          {activeTab === 'organisation' && (
            <OrganisationPanel
              summary={current}
              dimension={effectiveDimension}
              onDimensionChange={setDimension}
              canSeePii={canSeePii}
              customDimensions={filterDimensions?.dimensions ?? []}
            />
          )}
          {activeTab === 'credits' && (
            <CreditsPanel summary={current} trend={trendKey === trendScope ? trend : null} trendLoading={trendLoading} trendError={trendError} />
          )}
          {activeTab === 'people' && canSeePii && (
            <PeoplePanel
              query={query}
              filters={peopleFilters}
              onFiltersChange={setPeopleFilters}
              licenceKnown={current.sources.adoption.available}
            />
          )}
          {activeTab === 'method' && <MethodPanel summary={current} />}
        </div>
      )}

      {whatIfOpen && (
        <WhatIfDialog
          open
          scenario={scenario}
          defaults={tenantDefaults}
          missing={(summary?.assumptions.scenarioApplied ? [] : summary?.assumptions.missingForValue ?? []).map((f) =>
            FIELD_LABEL_KEYS[f] ? t(FIELD_LABEL_KEYS[f]) : f,
          )}
          settingsHref={isAdmin ? ASSUMPTIONS_SETTINGS_HREF : undefined}
          onApply={(next, origin) => {
            setScenario(next);
            setExampleInUse(origin === 'example' && next !== null);
            setWhatIfOpen(false);
          }}
          onClose={() => setWhatIfOpen(false)}
        />
      )}
    </div>
  );
}
