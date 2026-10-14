import { Fragment, useEffect, useState } from 'react';
import {
  Title3,
  Body1,
  Text,
  Card,
  Link,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  MessageBar,
  MessageBarBody,
  Button,
  Select,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular, ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons';
import { fetchDlpAvailability, fetchDlpGovernance, fetchDlpSummary } from '../api/dlpApi';
import type {
  DlpAvailability,
  DlpGovernanceMixRow,
  DlpGovernanceRate,
  DlpGovernanceSummary,
  DlpImpactRow,
  DlpSummary,
} from '../types/dlp';
import Spinner from '../components/Spinner';
import { formatNumber, useT, useTNode, type TFunction, type TranslationKey } from '../i18n';
import { usePortalAccess } from '../access';
import PiiHiddenNote from '../components/shared/PiiHiddenNote';
import GlobalFilterBar from '../components/globalFilter/GlobalFilterBar';
import { useGlobalFilter } from '../components/globalFilter/GlobalFilterProvider';

const WINDOWS = [
  { days: 7, labelKey: 'dlp.period.last7Days' },
  { days: 28, labelKey: 'dlp.period.last28Days' },
  { days: 90, labelKey: 'dlp.period.last90Days' },
  { days: 180, labelKey: 'dlp.period.last180Days' },
] satisfies { days: number; labelKey: TranslationKey }[];


function availabilityReasons(availability: DlpAvailability, t: TFunction): string[] {
  const reasons: string[] = [];
  if (!availability.copilotDlpAvailable) reasons.push(t('dlp.availability.reason.copilotImportOff'));
  if (!availability.tenantDlpAvailable) reasons.push(t('dlp.availability.reason.tenantImportOff'));
  return reasons;
}

const useStyles = makeStyles({
  intro: { marginTop: '8px' },
  sectionTitle: { marginTop: '24px' },
  card: { marginTop: '12px', display: 'flex', flexDirection: 'column', gap: '4px' },
  muted: { color: tokens.colorNeutralForeground3 },
  kpiRow: { display: 'flex', gap: '12px', flexWrap: 'wrap', marginTop: '12px' },
  kpi: { flex: '1 1 160px', minWidth: '160px', padding: '12px', display: 'flex', flexDirection: 'column', gap: '2px' },
  kpiValue: { fontSize: tokens.fontSizeHero700, lineHeight: tokens.lineHeightHero700, fontWeight: tokens.fontWeightSemibold },
  blocked: { color: tokens.colorPaletteRedForeground1 },
  toolbar: { display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap' },
  spacer: { flexGrow: 1 },
  // Table cells inherit fontSizeBase300 from a bare value; pin the smaller size explicitly.
  td: { fontSize: tokens.fontSizeBase200 },
  bar: { display: 'flex', height: '8px', borderRadius: '4px', overflow: 'hidden', minWidth: '80px' },
  // Row expander: a real button so it is keyboard reachable and announces its expanded state.
  expander: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    background: 'none',
    border: 'none',
    padding: '0',
    cursor: 'pointer',
    font: 'inherit',
    color: 'inherit',
    textAlign: 'left',
  },
  nested: { paddingLeft: '20px', paddingTop: '4px', paddingBottom: '4px' },
  // Governance section (#648)
  governanceCard: { flex: '1 1 220px', minWidth: '220px', padding: '12px', display: 'flex', flexDirection: 'column', gap: '4px' },
  governanceValue: { fontSize: tokens.fontSizeBase600, lineHeight: tokens.lineHeightBase600, fontWeight: tokens.fontWeightSemibold },
  mixRow: { display: 'flex', gap: '12px', flexWrap: 'wrap', marginTop: '12px' },
  mixCard: { flex: '1 1 320px', minWidth: '280px', padding: '12px', display: 'flex', flexDirection: 'column', gap: '4px' },
  agentList: { margin: '4px 0 0 0', paddingLeft: '18px', fontSize: tokens.fontSizeBase200 },
});

/** A 0-1 share as a percentage in the reader's language. */
function percent(value: number): string {
  return formatNumber(value, { style: 'percent', maximumFractionDigits: 1 });
}

/**
 * A per-interaction flag as a rate per 10,000, always beside what it is a rate of: the flagged interactions,
 * the interactions that reported the flag at all, and how much of the period that is. An interaction whose
 * flag was never reported is in none of those, because "not reported" is not "clean".
 */
function GovernanceRateCard({
  title,
  hint,
  rate,
  interactions,
}: {
  title: string;
  hint: string;
  rate: DlpGovernanceRate;
  interactions: number;
}) {
  const styles = useStyles();
  const t = useT();
  const known = rate.ratePer10000 !== null && rate.ratePer10000 !== undefined && rate.reportedInteractions > 0;

  return (
    <Card className={styles.governanceCard}>
      <Text size={200} className={styles.muted}>
        {title}
      </Text>
      <span className={styles.governanceValue}>
        {known
          ? t('dlp.governance.rate.value', { rate: formatNumber(rate.ratePer10000 as number, { maximumFractionDigits: 1 }) })
          : t('dlp.governance.rate.notReported')}
      </span>
      {known ? (
        <>
          <Text size={200}>
            {t('dlp.governance.rate.fraction', {
              flagged: formatNumber(rate.flaggedInteractions),
              reported: formatNumber(rate.reportedInteractions),
            })}
          </Text>
          <Text size={100} className={styles.muted}>
            {t('dlp.governance.rate.coverage', {
              reported: formatNumber(rate.reportedInteractions),
              interactions: formatNumber(interactions),
              share: percent(interactions > 0 ? rate.reportedInteractions / interactions : 0),
            })}
          </Text>
        </>
      ) : (
        <Text size={200}>{t('dlp.governance.rate.noneReported', { interactions: formatNumber(interactions) })}</Text>
      )}
      <Text size={100} className={styles.muted}>
        {hint}
      </Text>
    </Card>
  );
}

/** The models or the plugins, as Microsoft named them, each as a share of every interaction in the period. */
function GovernanceMixTable({
  title,
  description,
  nameHeader,
  emptyMessage,
  rows,
}: {
  title: string;
  description: string;
  nameHeader: string;
  emptyMessage: string;
  rows: DlpGovernanceMixRow[] | null | undefined;
}) {
  const styles = useStyles();
  const t = useT();
  const safeRows = rows ?? [];

  return (
    <Card className={styles.mixCard}>
      <Text weight="semibold" size={400}>
        {title}
      </Text>
      <Text size={200} block className={styles.muted} style={{ marginBottom: '8px' }}>
        {description}
      </Text>
      {safeRows.length === 0 ? (
        <Text size={200} className={styles.muted}>
          {emptyMessage}
        </Text>
      ) : (
        <Table size="small" aria-label={title}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{nameHeader}</TableHeaderCell>
              <TableHeaderCell style={{ width: 110 }}>{t('dlp.governance.column.interactions')}</TableHeaderCell>
              <TableHeaderCell style={{ width: 150 }}>{t('dlp.governance.column.share')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {safeRows.map((r) => (
              <TableRow key={r.name}>
                {/* Microsoft's own identifier, shown exactly as reported. */}
                <TableCell className={styles.td}>{r.name}</TableCell>
                <TableCell className={styles.td}>{formatNumber(r.interactions)}</TableCell>
                <TableCell className={styles.td}>{r.share === null || r.share === undefined ? '—' : percent(r.share)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </Card>
  );
}

/**
 * The page's governance section (#648): Microsoft's prompt-safety and grounding signals for the same period,
 * and the agents DLP blocked. Loaded by its own call, so the DLP figures above never wait for it.
 *
 * The agents card reads the DLP summary the page already has rather than a query of its own, and points to
 * Copilot Adoption for the Retire / Review verdicts instead of joining to them: that analysis is separate and
 * expensive, and a link costs nothing.
 */
function GovernanceSection({
  copilotAvailable,
  governance,
  loading,
  error,
  summary,
}: {
  copilotAvailable: boolean;
  governance: DlpGovernanceSummary | null;
  loading: boolean;
  error: string | null;
  summary: DlpSummary;
}) {
  const styles = useStyles();
  const t = useT();
  const tNode = useTNode();
  const blockedAgents = (summary.topAgents ?? []).filter((a) => a.blockedCount > 0).slice(0, 5);

  return (
    <section aria-label={t('dlp.governance.title')}>
      <Text className={styles.sectionTitle} weight="semibold" size={500} block>
        {t('dlp.governance.title')}
      </Text>
      <Body1 block className={styles.muted} style={{ marginTop: '4px' }}>
        {t('dlp.governance.description')}
      </Body1>

      {!copilotAvailable && (
        <Text size={200} className={styles.muted} block style={{ marginTop: '8px' }}>
          {t('dlp.governance.importOff')}
        </Text>
      )}

      {copilotAvailable && loading && (
        <div style={{ textAlign: 'center', padding: '16px' }}>
          <Spinner size={32} label={t('dlp.governance.loading')} />
        </div>
      )}

      {copilotAvailable && error && (
        <MessageBar intent="error" style={{ marginTop: '12px' }}>
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {copilotAvailable && !loading && governance && governance.interactions === 0 && (
        <Text size={200} className={styles.muted} block style={{ marginTop: '8px' }}>
          {t('dlp.governance.noInteractions')}
        </Text>
      )}

      {copilotAvailable && !loading && governance && governance.interactions > 0 && (
        <>
          <div className={styles.kpiRow}>
            <GovernanceRateCard
              title={t('dlp.governance.jailbreak.title')}
              hint={t('dlp.governance.jailbreak.hint')}
              rate={governance.jailbreak}
              interactions={governance.interactions}
            />
            <GovernanceRateCard
              title={t('dlp.governance.xpia.title')}
              hint={t('dlp.governance.xpia.hint')}
              rate={governance.xpia}
              interactions={governance.interactions}
            />
            <Card className={styles.governanceCard}>
              <Text size={200} className={styles.muted}>
                {t('dlp.governance.labels.title')}
              </Text>
              <span className={styles.governanceValue}>
                {governance.sensitivityLabels.share === null || governance.sensitivityLabels.share === undefined
                  ? t('dlp.governance.labels.none')
                  : percent(governance.sensitivityLabels.share)}
              </span>
              <Text size={200}>
                {t('dlp.governance.labels.fraction', {
                  labelled: formatNumber(governance.sensitivityLabels.labelledResources),
                  resources: formatNumber(governance.sensitivityLabels.resources),
                })}
              </Text>
              <Text size={100} className={styles.muted}>
                {t('dlp.governance.labels.coverage', {
                  withResources: formatNumber(governance.sensitivityLabels.interactionsWithResources),
                  interactions: formatNumber(governance.interactions),
                })}
              </Text>
            </Card>
            <Card className={styles.governanceCard}>
              <Text size={200} className={styles.muted}>
                {t('dlp.governance.agents.title')}
              </Text>
              <span className={styles.governanceValue}>
                {formatNumber(summary.agentsImpacted)}
              </span>
              {blockedAgents.length === 0 ? (
                <Text size={200}>{t('dlp.governance.agents.none')}</Text>
              ) : (
                <>
                  <Text size={100} className={styles.muted}>
                    {t('dlp.governance.agents.hint')}
                  </Text>
                  <ul className={styles.agentList}>
                    {blockedAgents.map((a, i) => (
                      <li key={a.id ?? `${a.name}-${i}`}>
                        {a.name ?? '—'} · {t('dlp.governance.agents.blocked', { count: formatNumber(a.blockedCount) })}
                      </li>
                    ))}
                  </ul>
                </>
              )}
              <Text size={100} className={styles.muted}>
                {tNode('dlp.governance.agents.pointer', {
                  link: <Link href="#/insights/copilot-adoption">{t('dlp.governance.agents.pointerLink')}</Link>,
                })}
              </Text>
            </Card>
          </div>

          <div className={styles.mixRow}>
            <GovernanceMixTable
              title={t('dlp.governance.models.title')}
              description={t('dlp.governance.models.description', {
                interactions: formatNumber(governance.interactions),
                withModel: formatNumber(governance.interactionsWithModel),
              })}
              nameHeader={t('dlp.governance.column.model')}
              emptyMessage={t('dlp.governance.models.empty')}
              rows={governance.models}
            />
            <GovernanceMixTable
              title={t('dlp.governance.plugins.title')}
              description={t('dlp.governance.plugins.description', {
                interactions: formatNumber(governance.interactions),
                withPlugin: formatNumber(governance.interactionsWithPlugin),
              })}
              nameHeader={t('dlp.governance.column.plugin')}
              emptyMessage={t('dlp.governance.plugins.empty')}
              rows={governance.plugins}
            />
          </div>
        </>
      )}
    </section>
  );
}

function KpiCard({ label, value, hint, danger }: { label: string; value: number; hint?: string; danger?: boolean }) {
  const styles = useStyles();
  return (
    <Card className={styles.kpi}>
      <Text size={200} className={styles.muted}>
        {label}
      </Text>
      <span className={`${styles.kpiValue} ${danger ? styles.blocked : ''}`}>{formatNumber(value)}</span>
      {hint && (
        <Text size={100} className={styles.muted}>
          {hint}
        </Text>
      )}
    </Card>
  );
}

/**
 * A ranked "top offenders" table. Blocked and audited are shown as separate columns on purpose:
 * a policy running in simulation ("Audit only") matches without withholding anything, and merging
 * the two would tell an admin their users are blocked when they are not.
 */
function ImpactTable({
  title,
  description,
  nameHeader,
  rows,
  showUsers,
  expandable,
}: {
  title: string;
  description: string;
  nameHeader: string;
  rows: DlpImpactRow[] | null | undefined;
  showUsers: boolean;
  /** Allow an agent row to expand into the policies that affected it. */
  expandable?: boolean;
}) {
  const styles = useStyles();
  const t = useT();
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  // Defensive: a ranked list is only ever absent if the API contract has drifted. Rendering "nothing
  // in this period" beats taking the whole page down with a TypeError, which is exactly what an
  // unguarded .map() did when these fields were serialised under the wrong names.
  const safeRows = rows ?? [];
  const columnCount = 3 + (showUsers ? 1 : 0);

  const toggle = (key: string) =>
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  return (
    <Card className={styles.card}>
      <Text weight="semibold" size={400}>
        {title}
      </Text>
      <Text size={200} block className={styles.muted} style={{ marginBottom: '8px' }}>
        {description}
      </Text>
      {safeRows.length === 0 ? (
        <Text size={200} className={styles.muted}>
          {t('dlp.table.empty')}
        </Text>
      ) : (
        <Table size="small" aria-label={title}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{nameHeader}</TableHeaderCell>
              <TableHeaderCell style={{ width: 110 }}>{t('dlp.column.blocked')}</TableHeaderCell>
              <TableHeaderCell style={{ width: 110 }}>{t('dlp.column.auditedOnly')}</TableHeaderCell>
              {showUsers && <TableHeaderCell style={{ width: 110 }}>{t('dlp.column.users')}</TableHeaderCell>}
            </TableRow>
          </TableHeader>
          <TableBody>
            {safeRows.map((r, i) => {
              const key = r.id ?? `${r.name}-${i}`;
              const policies = r.policies ?? [];
              const canExpand = expandable === true && policies.length > 0;
              const isOpen = expanded.has(key);

              return (
                <Fragment key={key}>
                  <TableRow>
                    <TableCell className={styles.td}>
                      {canExpand ? (
                        <button
                          type="button"
                          className={styles.expander}
                          aria-expanded={isOpen}
                          onClick={() => toggle(key)}
                        >
                          {isOpen ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
                          <span>{r.name ?? '—'}</span>
                        </button>
                      ) : (
                        (r.name ?? '—')
                      )}
                    </TableCell>
                    <TableCell className={`${styles.td} ${r.blockedCount > 0 ? styles.blocked : ''}`}>
                      {formatNumber(r.blockedCount)}
                    </TableCell>
                    <TableCell className={styles.td}>{formatNumber(r.auditedCount)}</TableCell>
                    {showUsers && <TableCell className={styles.td}>{formatNumber(r.usersAffected ?? 0)}</TableCell>}
                  </TableRow>

                  {canExpand && isOpen && (
                    <TableRow>
                      <TableCell className={styles.td} colSpan={columnCount}>
                        <div className={styles.nested}>
                          <Text size={200} className={styles.muted} block style={{ marginBottom: '4px' }}>
                            {t('dlp.policyDrilldown.title', { name: r.name ?? t('dlp.policyDrilldown.thisAgent') })}
                          </Text>
                          <Table
                            size="extra-small"
                            aria-label={t('dlp.policyDrilldown.title', { name: r.name ?? t('dlp.policyDrilldown.thisAgent') })}
                          >
                            <TableHeader>
                              <TableRow>
                                <TableHeaderCell>{t('dlp.column.policy')}</TableHeaderCell>
                                <TableHeaderCell style={{ width: 110 }}>{t('dlp.column.blocked')}</TableHeaderCell>
                                <TableHeaderCell style={{ width: 110 }}>{t('dlp.column.auditedOnly')}</TableHeaderCell>
                              </TableRow>
                            </TableHeader>
                            <TableBody>
                              {policies.map((p, pi) => (
                                <TableRow key={p.id ?? `${p.name}-${pi}`}>
                                  <TableCell className={styles.td}>{p.name ?? '—'}</TableCell>
                                  <TableCell className={`${styles.td} ${p.blockedCount > 0 ? styles.blocked : ''}`}>
                                    {formatNumber(p.blockedCount)}
                                  </TableCell>
                                  <TableCell className={styles.td}>{formatNumber(p.auditedCount)}</TableCell>
                                </TableRow>
                              ))}
                            </TableBody>
                          </Table>
                        </div>
                      </TableCell>
                    </TableRow>
                  )}
                </Fragment>
              );
            })}
          </TableBody>
        </Table>
      )}
    </Card>
  );
}

/**
 * Insights page: which Copilot actions and agents are being blocked by Microsoft Purview DLP
 * policies, how often, and who is most affected.
 *
 * The per-agent figures come from the DLP detail Microsoft embeds in each Copilot interaction
 * record, which is the only place a Copilot DLP block can be tied to a specific agent. The
 * tenant-wide DLP.All figures are shown in their own section because those records carry no agent
 * identity at all - presenting them together would imply an attribution the audit data cannot
 * support.
 */
export default function DlpPage() {
  const styles = useStyles();
  const t = useT();
  const { seePii } = usePortalAccess();
  // The DLP.All section is tenant-wide only while no administrator's filter narrows it.
  const narrowedByAdmin = useGlobalFilter().effective?.applied === true;

  const [days, setDays] = useState(28);
  const [reloadKey, setReloadKey] = useState(0);

  const [availability, setAvailability] = useState<DlpAvailability | null>(null);
  const [summary, setSummary] = useState<DlpSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // The governance section loads on its own, so the DLP figures never wait for it and its failure stays in it.
  const [governance, setGovernance] = useState<DlpGovernanceSummary | null>(null);
  const [governanceLoading, setGovernanceLoading] = useState(false);
  const [governanceError, setGovernanceError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setGovernance(null);
    setGovernanceError(null);
    setGovernanceLoading(false);

    fetchDlpAvailability()
      .then(async (a) => {
        if (cancelled) return;
        setAvailability(a);

        // The signals are carried on the Copilot interaction records, so they need only the Copilot import.
        if (a.copilotDlpAvailable) {
          setGovernanceLoading(true);
          Promise.resolve()
            .then(() => fetchDlpGovernance(days))
            .then((g) => {
              if (!cancelled) setGovernance(g);
            })
            .catch((e: unknown) => {
              if (!cancelled) setGovernanceError(e instanceof Error ? e.message : t('dlp.governance.error'));
            })
            .finally(() => {
              if (!cancelled) setGovernanceLoading(false);
            });
        }

        // Still fetch when only one source is on - the other simply reports zero.
        if (a.available) {
          const s = await fetchDlpSummary(days);
          if (!cancelled) setSummary(s);
        } else {
          setSummary(null);
        }
      })
      .catch((e: any) => {
        if (!cancelled) setError(e instanceof Error ? e.message : t('dlp.error.loadData'));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [days, reloadKey]);

  const totalCopilot = (summary?.copilotBlockedCount ?? 0) + (summary?.copilotAuditedCount ?? 0);

  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap' }}>
        <Title3>{t('dlp.title')}</Title3>
        <div className={styles.toolbar}>
          <Select
            value={String(days)}
            onChange={(_e: any, data: any) => setDays(Number(data.value))}
            aria-label={t('dlp.period.ariaLabel')}
          >
            {WINDOWS.map((w) => (
              <option key={w.days} value={w.days}>
                {t(w.labelKey)}
              </option>
            ))}
          </Select>
          <Button
            appearance="subtle"
            icon={<ArrowClockwise16Regular />}
            onClick={() => setReloadKey((k) => k + 1)}
            disabled={loading}
          >
            {t('common.action.refresh')}
          </Button>
        </div>
      </div>

      <Body1 block className={styles.intro}>
        {t('dlp.intro')}
      </Body1>

      <GlobalFilterBar />

      {error && (
        <MessageBar intent="error" style={{ marginTop: '12px' }}>
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {(availability ? availabilityReasons(availability, t) : []).map((r) => (
        <MessageBar key={r} intent={availability?.available ? 'info' : 'warning'} style={{ marginTop: '12px' }}>
          <MessageBarBody>{r}</MessageBarBody>
        </MessageBar>
      ))}

      {loading && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={64} label={t('dlp.loading')} />
        </div>
      )}

      {!loading && summary && (
        <>
          <div className={styles.kpiRow}>
            <KpiCard
              label={t('dlp.kpi.blocked.label')}
              value={summary.copilotBlockedCount}
              hint={t('dlp.kpi.blocked.hint')}
              danger
            />
            <KpiCard
              label={t('dlp.kpi.auditedOnly.label')}
              value={summary.copilotAuditedCount}
              hint={t('dlp.kpi.auditedOnly.hint')}
            />
            <KpiCard label={t('dlp.kpi.peopleAffected.label')} value={summary.usersImpacted} hint={t('dlp.kpi.peopleAffected.hint')} />
            <KpiCard label={t('dlp.kpi.agentsAffected.label')} value={summary.agentsImpacted} hint={t('dlp.kpi.agentsAffected.hint')} />
            <KpiCard label={t('dlp.kpi.policiesInvolved.label')} value={summary.policiesInvolved} hint={t('dlp.kpi.policiesInvolved.hint')} />
          </div>

          {totalCopilot === 0 && (
            <MessageBar intent="success" style={{ marginTop: '12px' }}>
              <MessageBarBody>
                {t('dlp.noCopilotActivity')}
              </MessageBarBody>
            </MessageBar>
          )}

          <Text className={styles.sectionTitle} weight="semibold" size={500} block>
            {t('dlp.affected.title')}
          </Text>

          <ImpactTable
            title={t('dlp.affected.agents.title')}
            description={t('dlp.affected.agents.description')}
            nameHeader={t('dlp.column.agent')}
            rows={summary.topAgents}
            showUsers
            expandable
          />
          {seePii ? (
            <ImpactTable
              title={t('dlp.affected.people.title')}
              description={t('dlp.affected.people.description')}
              nameHeader={t('dlp.column.user')}
              rows={summary.topUsers}
              showUsers={false}
              expandable
            />
          ) : <PiiHiddenNote />}
          <ImpactTable
            title={t('dlp.affected.policies.title')}
            description={t('dlp.affected.policies.description')}
            nameHeader={t('dlp.column.policy')}
            rows={summary.topPolicies}
            showUsers
          />
          <ImpactTable
            title={t('dlp.affected.sensitivityLabels.title')}
            description={t('dlp.affected.sensitivityLabels.description')}
            nameHeader={t('dlp.column.sensitivityLabel')}
            rows={summary.topSensitivityLabels}
            showUsers
          />

          <GovernanceSection
            copilotAvailable={availability?.copilotDlpAvailable === true}
            governance={governance}
            loading={governanceLoading}
            error={governanceError}
            summary={summary}
          />

          <Text className={styles.sectionTitle} weight="semibold" size={500} block>
            {narrowedByAdmin ? t('dlp.tenant.titleFiltered') : t('dlp.tenant.title')}
          </Text>
          <Body1 block className={styles.muted} style={{ marginTop: '4px' }}>
            {t('dlp.tenant.description')}
          </Body1>

          {!availability?.tenantDlpAvailable ? (
            <Text size={200} className={styles.muted} block style={{ marginTop: '8px' }}>
              {t('dlp.tenant.importOff')}
            </Text>
          ) : (
            <>
              <div className={styles.kpiRow}>
                <KpiCard label={t('dlp.kpi.blocked.label')} value={summary.tenantBlockedCount} hint={t('dlp.tenant.blocked.hint')} danger />
                <KpiCard label={t('dlp.kpi.auditedOnly.label')} value={summary.tenantAuditedCount} hint={t('dlp.tenant.auditedOnly.hint')} />
              </div>
              <ImpactTable
                title={narrowedByAdmin ? t('dlp.tenant.policies.titleFiltered') : t('dlp.tenant.policies.title')}
                description={narrowedByAdmin ? t('dlp.tenant.policies.descriptionFiltered') : t('dlp.tenant.policies.description')}
                nameHeader={t('dlp.column.policy')}
                rows={summary.tenantTopPolicies}
                showUsers={false}
              />
            </>
          )}
        </>
      )}
    </div>
  );
}
