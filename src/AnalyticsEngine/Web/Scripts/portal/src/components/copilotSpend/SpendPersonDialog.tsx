import { useEffect, useState, type ReactNode } from 'react';
import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarBody,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { EN_CATALOG, formatDateParts, formatNumber, plural, useT, type TFunction, type TranslationKey } from '../../i18n';
import { fetchSpendPerson, type SpendQuery } from '../../api/copilotSpendApi';
import type { SpendDay, SpendPersonDetail, SpendPersonRow, SpendPersonUsage } from '../../types/copilotSpend';
import {
  LabelledBars,
  REASON_LABEL_KEYS,
  SectionHeading,
  formatCredits,
  formatDecimal,
  formatShare,
  formatShareAboveZero,
  formatShortDate,
  parseUtcDate,
  type BarRowData,
} from '../copilotInvestment/investmentShared';
import { agentOriginLabel } from '../copilotAdoption/serverText';
import ResourceTypesPanel from '../copilotAdoption/ResourceTypesPanel';
import WordCloud from '../charts/WordCloud';
import SpendLineChart, { type SpendLinePoint } from './SpendLineChart';

/** The server's agent kinds (`CopilotSpendAgentKindKeys`), each with its wording. */
export const SPEND_AGENT_KIND_KEYS = {
  customEngine: 'copilotSpend.person.usage.kind.customEngine',
  declarative: 'copilotSpend.person.usage.kind.declarative',
  unknown: 'copilotSpend.person.usage.kind.unknown',
} as const satisfies Record<string, TranslationKey>;

const MODE_LABEL_KEYS: Record<string, TranslationKey> = {
  directing: 'promptCategories.directing',
  supervising: 'promptCategories.supervising',
};

const CREDIT_UNIT = 'copilotCredit';
/** Graph's interaction `appClass` names the app after this prefix, for example `IPM.SkypeTeams.Message.Copilot.Word`. */
const APP_CLASS_PREFIX = 'IPM.SkypeTeams.Message.Copilot.';

const useStyles = makeStyles({
  surface: { maxWidth: 'min(1100px, calc(100vw - 32px))', width: '100%' },
  title: { overflowWrap: 'anywhere' },
  content: { display: 'flex', flexDirection: 'column', gap: '28px' },
  who: { color: tokens.colorNeutralForeground3, overflowWrap: 'anywhere' },
  kpis: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(210px, 100%), 1fr))', gap: '12px' },
  kpi: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    minWidth: 0,
  },
  kpiValue: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  flags: { display: 'flex', gap: '6px', flexWrap: 'wrap', marginTop: '4px' },
  sources: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(380px, 100%), 1fr))', gap: '28px', alignItems: 'start' },
  pair: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(320px, 100%), 1fr))', gap: '24px', alignItems: 'start' },
  column: { display: 'flex', flexDirection: 'column', gap: '18px', minWidth: 0 },
  section: { display: 'flex', flexDirection: 'column', gap: '10px', minWidth: 0 },
  subheading: { margin: 0 },
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))', gap: '10px 18px' },
  fact: { display: 'flex', flexDirection: 'column', gap: '2px' },
  factValue: { fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums' },
  tableWrap: { overflowX: 'auto' },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  numHeader: { justifyContent: 'flex-end', textAlign: 'right' },
  line: { display: 'block', whiteSpace: 'nowrap' },
  nowrap: { whiteSpace: 'nowrap' },
  agentName: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap', overflowWrap: 'anywhere' },
  rule: { borderTop: `1px solid ${tokens.colorNeutralStroke2}`, margin: 0 },
});

/** An app as the source names it; Graph's interaction app class is shortened to the app's own name. */
export function spendAppName(t: TFunction, app: string | null | undefined): string {
  const trimmed = app?.trim();
  if (!trimmed) return t('copilotSpend.person.usage.unknownApp');
  return trimmed.startsWith(APP_CLASS_PREFIX) && trimmed.length > APP_CLASS_PREFIX.length ? trimmed.slice(APP_CLASS_PREFIX.length) : trimmed;
}

function goalLabel(t: TFunction, id: string | null, name: string | null, nameKey: string | null): string {
  if (id === 'other') return t('promptCategories.other');
  if (id === 'not-classified') return t('promptCategories.notClassified');
  if (nameKey && nameKey in EN_CATALOG) return t(nameKey as TranslationKey);
  return name ?? id ?? '\u2014';
}

function promptDay(iso: string): string {
  return formatDateParts(parseUtcDate(iso), { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
}

function promptClock(iso: string): string {
  return formatDateParts(parseUtcDate(iso), { hour: '2-digit', minute: '2-digit', timeZone: 'UTC', timeZoneName: 'short' });
}

function withShare(count: number, of: number): string {
  return of > 0 ? `${formatNumber(count)} (${formatShare(count / of)})` : formatNumber(count);
}

/**
 * One person's Copilot consumption and what it went on: both credit sources side by side and never added, each over
 * time, then what they used Copilot for - apps, agents, goals, key phrases and their latest prompts. Prompts are described
 * by their metadata; their text is never stored, so it is never shown.
 */
export default function SpendPersonDialog({ query, userId, name, onClose }: {
  query: SpendQuery;
  userId: number;
  /** The person's sign-in name, or their id when the directory has none. */
  name: string;
  onClose: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const [detail, setDetail] = useState<SpendPersonDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setDetail(null);
    setError(null);
    fetchSpendPerson(query, userId, controller.signal)
      .then(setDetail)
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e));
      });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query.windowDays, query.userFilter, userId]);

  return (
    <Dialog open onOpenChange={(_e, data) => !data.open && onClose()}>
      <DialogSurface className={styles.surface}>
        <DialogBody>
          <DialogTitle className={styles.title}>{t('copilotSpend.person.title', { name })}</DialogTitle>
          <DialogContent>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            {!detail && !error && <Spinner size="small" label={t('copilotSpend.person.loading')} />}
            {detail && <PersonDetail detail={detail} />}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>
              {t('copilotSpend.person.close')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function PersonDetail({ detail }: { detail: SpendPersonDetail }) {
  const styles = useStyles();
  const t = useT();
  const person = detail.person;
  const usage = detail.usage ?? null;
  const ranks = detail.ranks ?? null;

  const who = [person.department, person.jobTitle, person.manager ? t('copilotSpend.person.managedBy', { manager: person.manager }) : null]
    .filter((part): part is string => !!part)
    .join(' \u00b7 ');

  const rankLine = (rank: number | null | undefined, consumers: number | null | undefined, share: number | null | undefined) =>
    rank && consumers
      ? t(plural(consumers, 'copilotSpend.person.rank.one', 'copilotSpend.person.rank.other'), {
          rank: formatNumber(rank),
          consumers: formatNumber(consumers),
          share: formatShareAboveZero(share),
        })
      : null;

  const turnsKnown = !!usage?.turnsAssessed;
  const prompts = usage?.promptsAssessed ? usage.prompts : null;

  return (
    <div className={styles.content}>
      {who && <Text className={styles.who}>{who}</Text>}

      <div className={styles.kpis}>
        <Kpi
          label={t('copilotSpend.person.kpi.studio')}
          value={formatCredits(person.studioCredits)}
          sub={
            person.studioCredits === null
              ? t('copilotSpend.person.kpi.unavailable')
              : t(plural(person.studioDays ?? 0, 'copilotSpend.person.kpi.studioDays.one', 'copilotSpend.person.kpi.studioDays.other'), {
                  days: formatNumber(person.studioDays ?? 0),
                })
          }
          rank={rankLine(ranks?.studioRank, ranks?.studioConsumers, ranks?.studioShare)}
        />
        <Kpi
          label={t('copilotSpend.person.kpi.balance')}
          value={formatCredits(person.balanceCredits)}
          sub={
            person.balanceCredits === null
              ? t('copilotSpend.people.noReading')
              : t('copilotSpend.person.kpi.balanceDate', { date: formatShortDate(person.balanceDate) })
          }
          rank={rankLine(ranks?.balanceRank, ranks?.balanceConsumers, ranks?.balanceShare)}
          flags={<BalanceFlags person={person} />}
        />
        <Kpi
          label={t('copilotSpend.person.kpi.turns')}
          value={turnsKnown && usage ? formatNumber(usage.turns) : '\u2014'}
          sub={
            turnsKnown && usage
              ? t(plural(usage.activeDays, 'copilotSpend.person.kpi.turnsDays.one', 'copilotSpend.person.kpi.turnsDays.other'), {
                  days: formatNumber(usage.activeDays),
                })
              : t('copilotSpend.person.kpi.unavailable')
          }
        />
        <Kpi
          label={t('copilotSpend.person.kpi.prompts')}
          value={prompts ? formatNumber(prompts.prompts) : '\u2014'}
          sub={
            prompts
              ? t(
                  plural(prompts.conversations, 'copilotSpend.person.kpi.promptsConversations.one', 'copilotSpend.person.kpi.promptsConversations.other'),
                  { conversations: formatNumber(prompts.conversations) },
                )
              : t('copilotSpend.person.kpi.unavailable')
          }
        />
      </div>

      <div className={styles.sources}>
        <StudioColumn detail={detail} />
        <ReadingsColumn detail={detail} />
      </div>

      <hr className={styles.rule} />

      <UsageSection usage={usage} failed={!!detail.usageFailureCode} />
    </div>
  );
}

function Kpi({ label, value, sub, rank, flags }: { label: string; value: string; sub: string; rank?: string | null; flags?: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.kpi}>
      <Text size={200} className={styles.muted}>
        {label}
      </Text>
      <Text className={styles.kpiValue}>{value}</Text>
      <Text size={200}>{sub}</Text>
      {rank && (
        <Text size={200} weight="semibold">
          {rank}
        </Text>
      )}
      {flags}
    </div>
  );
}

/** The latest reading's caveats, worded as on the People table. */
function BalanceFlags({ person }: { person: SpendPersonRow }) {
  const styles = useStyles();
  const t = useT();
  const flags: TranslationKey[] = [];
  if (person.balancePartial) flags.push('copilotSpend.flag.partial');
  if (person.unknownAmounts) flags.push('copilotSpend.flag.unknown');
  if (person.otherUnits) flags.push('copilotSpend.flag.otherUnit');
  if (person.limitExhausted) flags.push('copilotSpend.flag.limit');
  if (person.notEntitled) flags.push('copilotSpend.flag.notEntitled');
  if (flags.length === 0) return null;
  return (
    <div className={styles.flags}>
      {flags.map((key) => (
        <Badge key={key} appearance="tint" color={key === 'copilotSpend.flag.limit' ? 'danger' : 'warning'} size="small">
          {t(key)}
        </Badge>
      ))}
    </div>
  );
}

function SubHeading({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return (
    <Text as="h4" weight="semibold" size={300} className={styles.subheading}>
      {children}
    </Text>
  );
}

function studioDayPoint(t: TFunction, day: SpendDay): SpendLinePoint {
  const date = formatShortDate(day.date);
  if (day.status === 'recorded') {
    return { date: day.date, values: [day.credits], label: t('copilotSpend.person.studioDayPoint', { date, credits: formatCredits(day.credits) }) };
  }
  if (day.status === 'noneRecorded') return { date: day.date, values: [0], label: t('copilotSpend.studio.dayNoneRecorded', { date }) };
  return { date: day.date, values: [null], notReported: true, label: t('copilotSpend.studio.dayNotReported', { date }) };
}

/** Billed Copilot Studio usage: a daily ledger, so it is drawn day by day and adds up over the period. */
function StudioColumn({ detail }: { detail: SpendPersonDetail }) {
  const styles = useStyles();
  const t = useT();
  const days = detail.studioDays ?? [];
  const billed = days.some((d) => d.status === 'recorded' && (d.credits ?? 0) > 0);
  const environments: BarRowData[] = detail.studioEnvironments.map((e, i) => ({
    key: `${e.environmentId ?? '-'}-${i}`,
    label: e.environmentName ?? e.environmentId ?? t('copilotSpend.studio.unnamedEnvironment'),
    value: e.credits,
    display: t(plural(e.activeDays, 'copilotSpend.person.studioRow.one', 'copilotSpend.person.studioRow.other'), {
      credits: formatCredits(e.credits),
      days: formatNumber(e.activeDays),
    }),
  }));

  return (
    <div className={styles.column}>
      {billed && (
        <div className={styles.section}>
          <SectionHeading title={t('copilotSpend.person.studioDaysTitle')} />
          <SpendLineChart
            points={days.map((d) => studioDayPoint(t, d))}
            series={[{ label: t('copilotSpend.person.studioDaysSeries'), colorIndex: 0 }]}
            ariaLabel={t('copilotSpend.person.studioDaysAria')}
            listLabel={t('copilotSpend.person.daysList')}
            height={180}
            area
            notReportedLabel={t('copilotSpend.person.notReported')}
          />
        </div>
      )}
      <div className={styles.section}>
        <SectionHeading title={t('copilotSpend.person.studioTitle')} />
        {environments.length === 0 ? <Text size={200}>{t('copilotSpend.person.noStudio')}</Text> : <LabelledBars rows={environments} />}
      </div>
    </div>
  );
}

/** Copilot credits consumed: readings, each the total as of its date, so they are drawn as a line and never added. */
function ReadingsColumn({ detail }: { detail: SpendPersonDetail }) {
  const styles = useStyles();
  const t = useT();
  // ISO dates sort correctly as plain strings, whatever the reader's language.
  const readings = [...detail.balances].sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));
  const points: SpendLinePoint[] = readings.map((b) => {
    const date = formatShortDate(b.date);
    return {
      date: b.date,
      values: [b.credits],
      marked: b.limitExhausted,
      label:
        b.credits === null
          ? t('copilotSpend.person.historyPointUnknown', { date })
          : t(b.limitExhausted ? 'copilotSpend.person.historyPointLimit' : 'copilotSpend.person.historyPoint', { date, credits: formatCredits(b.credits) }),
    };
  });
  const services: BarRowData[] = detail.services.map((s) => ({
    key: s.category,
    label: s.category,
    value: s.consumed ?? 0,
    display: s.unevaluatedReason
      ? `${t('copilotInvestment.person.credits.unevaluated')} \u00b7 ${t(REASON_LABEL_KEYS[s.unevaluatedReason] ?? 'copilotInvestment.reason.other')}`
      : [
          s.consumed === null ? t('copilotSpend.flag.unknown') : formatCredits(s.consumed),
          s.unit && s.unit !== CREDIT_UNIT ? t('copilotInvestment.services.unitOther', { unit: s.unit }) : null,
          s.decisionReason ? t(REASON_LABEL_KEYS[s.decisionReason] ?? 'copilotInvestment.reason.other') : null,
        ]
          .filter(Boolean)
          .join(' \u00b7 '),
  }));

  return (
    <div className={styles.column}>
      <div className={styles.section}>
        <SectionHeading title={t('copilotSpend.person.historyTitle')} description={t('copilotSpend.person.historyNote')} />
        {points.length === 0 ? (
          <Text size={200}>{t('copilotSpend.person.noBalance')}</Text>
        ) : (
          <SpendLineChart
            points={points}
            series={[{ label: t('copilotSpend.person.historySeries'), colorIndex: 1 }]}
            ariaLabel={t('copilotSpend.person.historyAria')}
            listLabel={t('copilotSpend.person.historyList')}
            height={180}
            area
            markerLabel={t('copilotSpend.person.historyMarker')}
            missingLabel={t('copilotSpend.flag.unknown')}
          />
        )}
      </div>
      {services.length > 0 && (
        <div className={styles.section}>
          <SubHeading>{t('copilotSpend.person.balanceTitle')}</SubHeading>
          <LabelledBars rows={services} />
        </div>
      )}
    </div>
  );
}

/** What the person used Copilot for. Every part that could not be read says so, rather than showing zero. */
function UsageSection({ usage, failed }: { usage: SpendPersonUsage | null; failed: boolean }) {
  const styles = useStyles();
  const t = useT();

  return (
    <div className={styles.section}>
      <SectionHeading title={t('copilotSpend.person.usage.title')} description={t('copilotSpend.person.usage.description')} />
      {failed && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('copilotSpend.person.usage.failed')}</MessageBarBody>
        </MessageBar>
      )}
      {!usage ? (
        !failed && <Text size={200}>{t('copilotSpend.person.usage.notAssessed')}</Text>
      ) : (
        <div className={styles.content}>
          {usage.turnsAssessed ? <TurnsPart usage={usage} /> : <Text size={200}>{t('copilotSpend.person.usage.notAssessed')}</Text>}
          <PromptsPart usage={usage} failed={failed} />
          <div className={styles.section}>
            <SubHeading>{t('copilotSpend.person.usage.resourcesTitle')}</SubHeading>
            {usage.resourcesAssessed ? (
              <ResourceTypesPanel rows={usage.resourceTypes} />
            ) : (
              <Text size={200}>{t(failed ? 'copilotSpend.person.usage.partFailed' : 'copilotSpend.person.usage.resourcesNotAssessed')}</Text>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

function TurnsPart({ usage }: { usage: SpendPersonUsage }) {
  const styles = useStyles();
  const t = useT();

  if (usage.turns <= 0) return <Text size={200}>{t('copilotSpend.person.usage.noTurns')}</Text>;

  const days: SpendLinePoint[] = usage.days.map((d) => ({
    date: d.date,
    values: [d.turns, d.agentTurns],
    label: t(plural(d.turns, 'copilotSpend.person.usage.dayPoint.one', 'copilotSpend.person.usage.dayPoint.other'), {
      date: formatShortDate(d.date),
      turns: formatNumber(d.turns),
      agentTurns: formatNumber(d.agentTurns),
    }),
  }));
  const share = (value: number) => `(${formatShare(value / usage.turns)})`;
  const mix: BarRowData[] = [
    { key: 'noAgent', label: t('copilotSpend.person.usage.mix.noAgent'), value: usage.noAgentTurns, always: true },
    { key: 'agent', label: t('copilotSpend.person.usage.mix.agent'), value: usage.agentTurns, always: true },
    { key: 'cowork', label: t('copilotSpend.person.usage.mix.cowork'), value: usage.coworkTurns, always: false },
    { key: 'makerTest', label: t('copilotSpend.person.usage.mix.makerTest'), value: usage.makerTestTurns, always: false },
  ]
    .filter((row) => row.always || row.value > 0)
    .map(({ key, label, value }) => ({
      key,
      label,
      value,
      display: t(plural(value, 'copilotSpend.person.usage.turnsValue.one', 'copilotSpend.person.usage.turnsValue.other'), { count: formatNumber(value) }),
      note: share(value),
    }));
  const apps: BarRowData[] = usage.apps.map((a) => ({
    key: a.app,
    label: spendAppName(t, a.app),
    value: a.count,
    display: t(plural(a.count, 'copilotSpend.person.usage.turnsValue.one', 'copilotSpend.person.usage.turnsValue.other'), { count: formatNumber(a.count) }),
    note: t(plural(a.days, 'copilotSpend.person.usage.onDays.one', 'copilotSpend.person.usage.onDays.other'), { days: formatNumber(a.days) }),
  }));
  const showTestTurns = usage.agents.some((a) => a.testTurns > 0);

  return (
    <>
      {days.length > 0 && (
        <div className={styles.section}>
          <SubHeading>{t('copilotSpend.person.usage.daysTitle')}</SubHeading>
          <SpendLineChart
            points={days}
            series={[
              { label: t('copilotSpend.person.usage.seriesAll'), colorIndex: 0 },
              { label: t('copilotSpend.person.usage.seriesAgent'), colorIndex: 2 },
            ]}
            ariaLabel={t('copilotSpend.person.usage.daysAria')}
            listLabel={t('copilotSpend.person.daysList')}
            height={190}
            formatValue={(value) => formatNumber(value)}
            area
          />
        </div>
      )}

      <div className={styles.pair}>
        <div className={styles.section}>
          <SubHeading>{t('copilotSpend.person.usage.mixTitle')}</SubHeading>
          <LabelledBars rows={mix} />
        </div>
        <div className={styles.section}>
          <SubHeading>{t('copilotSpend.person.usage.appsTitle')}</SubHeading>
          {apps.length === 0 ? <Text size={200}>{t('copilotSpend.person.usage.unknownApp')}</Text> : <LabelledBars rows={apps} />}
        </div>
      </div>

      <div className={styles.section}>
        <SubHeading>{t('copilotSpend.person.usage.agentsTitle')}</SubHeading>
        {usage.agents.length === 0 ? (
          <Text size={200}>{t('copilotSpend.person.usage.noAgents')}</Text>
        ) : (
          <div className={styles.tableWrap}>
            <Table size="small" aria-label={t('copilotSpend.person.usage.agentsTitle')} style={{ minWidth: '720px' }}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell style={{ width: '30%' }}>{t('copilotSpend.studio.col.agent')}</TableHeaderCell>
                  <TableHeaderCell>{t('copilotSpend.person.usage.col.kind')}</TableHeaderCell>
                  <TableHeaderCell>{t('copilotSpend.person.usage.col.origin')}</TableHeaderCell>
                  <NumHeader>{t('copilotSpend.person.usage.col.turns')}</NumHeader>
                  {showTestTurns && <NumHeader>{t('copilotSpend.person.usage.col.testTurns')}</NumHeader>}
                  <NumHeader>{t('copilotSpend.person.usage.col.estimate')}</NumHeader>
                  <TableHeaderCell>{t('copilotSpend.person.usage.col.lastUsed')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {usage.agents.map((a, i) => (
                  <TableRow key={`${a.agentId ?? '-'}-${i}`}>
                    <TableCell>
                      <span className={styles.agentName}>
                        {a.name ?? t('copilotSpend.studio.unnamedAgent')}
                        {a.cowork && (
                          <Badge appearance="tint" color="brand" size="small">
                            {t('copilotSpend.person.usage.coworkBadge')}
                          </Badge>
                        )}
                      </span>
                    </TableCell>
                    <TableCell>{t(SPEND_AGENT_KIND_KEYS[a.kindKey as keyof typeof SPEND_AGENT_KIND_KEYS] ?? SPEND_AGENT_KIND_KEYS.unknown)}</TableCell>
                    <TableCell>{agentOriginLabel(t, a.originKey)}</TableCell>
                    <TableCell className={styles.num}>{formatNumber(a.turns)}</TableCell>
                    {showTestTurns && <TableCell className={styles.num}>{formatNumber(a.testTurns)}</TableCell>}
                    <TableCell className={styles.num}>{formatCredits(a.estimateCredits)}</TableCell>
                    <TableCell className={styles.nowrap}>{formatShortDate(a.lastDate)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
        {usage.estimateCredits !== null && usage.pricedTurns > 0 && (
          <Text size={200} className={styles.muted}>
            {t('copilotSpend.person.usage.estimate', { credits: formatCredits(usage.estimateCredits), turns: formatNumber(usage.pricedTurns) })}
          </Text>
        )}
      </div>
    </>
  );
}

/**
 * `failed`: the read behind the prompt details failed. Prompts that were imported are then unknown for that reason; prompts
 * that were never imported still say so.
 */
function PromptsPart({ usage, failed }: { usage: SpendPersonUsage; failed: boolean }) {
  const styles = useStyles();
  const t = useT();
  const totals = usage.promptsAssessed ? usage.prompts : null;

  if (!totals) {
    return (
      <div className={styles.section}>
        <SubHeading>{t('copilotSpend.person.usage.promptsTitle')}</SubHeading>
        <Text size={200}>
          {t(failed && usage.promptsAssessed ? 'copilotSpend.person.usage.partFailed' : 'copilotSpend.person.usage.promptsNotAssessed')}
        </Text>
      </div>
    );
  }

  const promptCount = (count: number) =>
    t(plural(count, 'copilotSpend.person.usage.promptsValue.one', 'copilotSpend.person.usage.promptsValue.other'), { count: formatNumber(count) });
  const goals: BarRowData[] = usage.categories.map((c) => ({
    key: c.categoryId,
    label: goalLabel(t, c.categoryId, c.name, c.nameKey),
    value: c.prompts,
    display: promptCount(c.prompts),
    note: c.humanMode && MODE_LABEL_KEYS[c.humanMode] ? `\u00b7 ${t(MODE_LABEL_KEYS[c.humanMode])}` : undefined,
  }));
  if (usage.notClassifiedPrompts > 0) {
    goals.push({
      key: 'not-classified',
      label: t('promptCategories.notClassified'),
      value: usage.notClassifiedPrompts,
      display: promptCount(usage.notClassifiedPrompts),
    });
  }
  const promptApps: BarRowData[] = usage.promptApps.map((a, i) => ({
    key: `${a.app}-${i}`,
    label: spendAppName(t, a.app),
    value: a.count,
    display: promptCount(a.count),
  }));

  return (
    <div className={styles.section}>
      <SubHeading>{t('copilotSpend.person.usage.promptsTitle')}</SubHeading>
      <div className={styles.facts}>
        <Fact label={t('copilotSpend.person.usage.fact.days')} value={formatNumber(totals.days)} />
        <Fact label={t('copilotSpend.person.usage.fact.words')} value={formatDecimal(totals.averageWords)} />
        <Fact label={t('copilotSpend.person.usage.fact.attachments')} value={withShare(totals.withAttachments, totals.prompts)} />
        <Fact label={t('copilotSpend.person.usage.fact.links')} value={withShare(totals.withLinks, totals.prompts)} />
        <Fact label={t('copilotSpend.person.usage.fact.mentions')} value={withShare(totals.withMentions, totals.prompts)} />
        <Fact label={t('copilotSpend.person.usage.fact.context')} value={withShare(totals.withContext, totals.prompts)} />
        <Fact label={t('copilotSpend.person.usage.fact.first')} value={formatShortDate(totals.firstUtc)} />
        <Fact label={t('copilotSpend.person.usage.fact.last')} value={formatShortDate(totals.lastUtc)} />
      </div>

      <div className={styles.pair}>
        <div className={styles.section}>
          <SubHeading>{t('copilotSpend.person.usage.goalsTitle')}</SubHeading>
          {!usage.categoriesAssessed ? (
            <Text size={200}>{t('copilotSpend.person.usage.goalsNotAssessed')}</Text>
          ) : goals.length === 0 ? (
            <Text size={200}>{t('copilotSpend.person.usage.noGoals')}</Text>
          ) : (
            <LabelledBars rows={goals} />
          )}
        </div>
        <div className={styles.section}>
          <SubHeading>{t('copilotSpend.person.usage.promptAppsTitle')}</SubHeading>
          {promptApps.length === 0 ? <Text size={200}>{t('copilotSpend.person.usage.noRecent')}</Text> : <LabelledBars rows={promptApps} />}
        </div>
      </div>

      <div className={styles.section}>
        <SubHeading>{t('copilotSpend.person.usage.phrasesTitle')}</SubHeading>
        {usage.phrasesAssessed ? (
          <WordCloud
            categories={usage.phrases.map((p) => ({ label: p.phrase, value: p.prompts }))}
            valueLabel={t('copilotSpend.person.usage.fact.prompts')}
            literalLabels
          />
        ) : (
          <Text size={200}>{t('copilotSpend.person.usage.phrasesNotAssessed')}</Text>
        )}
      </div>

      <div className={styles.section}>
        <SubHeading>{t('copilotSpend.person.usage.recentTitle')}</SubHeading>
        {usage.recent.length === 0 ? (
          <Text size={200}>{t('copilotSpend.person.usage.noRecent')}</Text>
        ) : (
          <div className={styles.tableWrap}>
            <Table size="small" aria-label={t('copilotSpend.person.usage.recentTitle')} style={{ minWidth: '860px' }}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell style={{ width: '13%' }}>{t('copilotSpend.person.usage.col.when')}</TableHeaderCell>
                  <TableHeaderCell style={{ width: '10%' }}>{t('copilotSpend.person.usage.col.app')}</TableHeaderCell>
                  <TableHeaderCell style={{ width: '14%' }}>{t('copilotSpend.person.usage.col.goal')}</TableHeaderCell>
                  <TableHeaderCell style={{ width: '24%' }}>{t('copilotSpend.person.usage.col.phrases')}</TableHeaderCell>
                  <NumHeader width="7%">{t('copilotSpend.person.usage.col.words')}</NumHeader>
                  <NumHeader width="7%">{t('copilotSpend.person.usage.col.files')}</NumHeader>
                  <NumHeader width="7%">{t('copilotSpend.person.usage.col.links')}</NumHeader>
                  <NumHeader width="9%">{t('copilotSpend.person.usage.col.mentions')}</NumHeader>
                  <NumHeader width="9%">{t('copilotSpend.person.usage.col.context')}</NumHeader>
                </TableRow>
              </TableHeader>
              <TableBody>
                {usage.recent.map((r, i) => (
                  <TableRow key={`${r.createdUtc}-${i}`}>
                    <TableCell>
                      <span className={styles.line}>{promptDay(r.createdUtc)}</span>
                      <span className={mergeClasses(styles.line, styles.muted)}>{promptClock(r.createdUtc)}</span>
                    </TableCell>
                    <TableCell>{spendAppName(t, r.appClass)}</TableCell>
                    <TableCell>{r.categoryId ? goalLabel(t, r.categoryId, r.categoryName, r.categoryNameKey) : '\u2014'}</TableCell>
                    <TableCell>{r.phrases.length > 0 ? r.phrases.join(', ') : '\u2014'}</TableCell>
                    <TableCell className={styles.num}>{formatNumber(r.words)}</TableCell>
                    <TableCell className={styles.num}>{formatNumber(r.attachments)}</TableCell>
                    <TableCell className={styles.num}>{formatNumber(r.links)}</TableCell>
                    <TableCell className={styles.num}>{formatNumber(r.mentions)}</TableCell>
                    <TableCell className={styles.num}>{formatNumber(r.context)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </div>
    </div>
  );
}

/** A numeric column's header, aligned with its right-aligned figures: the header's content is laid out by its button slot. */
function NumHeader({ children, width }: { children: ReactNode; width?: string }) {
  const styles = useStyles();
  return (
    <TableHeaderCell className={styles.num} style={width ? { width } : undefined} button={{ className: styles.numHeader }}>
      {children}
    </TableHeaderCell>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  const styles = useStyles();
  return (
    <div className={styles.fact}>
      <Text size={200} className={styles.muted}>
        {label}
      </Text>
      <Text className={styles.factValue}>{value}</Text>
    </div>
  );
}
