import {
  Badge,
  Card,
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
import type { CopilotInvestmentSummary, InvestmentQuantity, InvestmentTrend } from '../../types/copilotInvestment';
import {
  BILLING_METHOD_KEYS,
  POLICY_STATUS_KEYS,
  SectionHeading,
  formatCredits,
  formatMoney,
  formatShare,
  formatShortDate,
} from './investmentShared';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '20px' },
  card: { display: 'flex', flexDirection: 'column', gap: '12px', padding: '16px', overflowX: 'auto' },
  sources: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))', gap: '12px' },
  source: { display: 'flex', flexDirection: 'column', gap: '6px', padding: '14px 16px' },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  chart: { display: 'flex', alignItems: 'flex-end', gap: '6px', height: '160px', paddingTop: '8px', borderBottom: `1px solid ${tokens.colorNeutralStroke1}` },
  column: { flex: '1 1 0', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'flex-end', height: '100%', minWidth: '18px' },
  bar: { width: '100%', maxWidth: '36px', backgroundColor: tokens.colorBrandBackground, borderRadius: '4px 4px 0 0', minHeight: '2px' },
  axis: { display: 'flex', gap: '6px' },
  axisLabel: { flex: '1 1 0', textAlign: 'center', minWidth: '18px', fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3, overflow: 'hidden' },
  facts: { display: 'grid', gridTemplateColumns: 'minmax(200px, 1fr) auto', gap: '6px 16px', alignItems: 'baseline' },
  metered: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(170px, 1fr))', gap: '12px' },
  figure: { display: 'flex', flexDirection: 'column', gap: '2px' },
  figureValue: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums' },
  muted: { color: tokens.colorNeutralForeground3 },
  code: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
});

const CREDIT_UNIT = 'copilotCredit';

function quantityText(t: ReturnType<typeof useT>, quantity: InvestmentQuantity | null): string {
  if (!quantity) return t('copilotInvestment.value.unknown');
  if (quantity.kind === 'unlimited') return t('copilotInvestment.value.unlimited');
  if (quantity.kind === 'limited' && quantity.value !== null) return formatCredits(quantity.value);
  return t('copilotInvestment.value.unknown');
}

/** The sources behind the report, the balance history, and the tenant-wide policy and pool context. */
export default function CreditsPanel({ summary, trend, trendLoading, trendError }: {
  summary: CopilotInvestmentSummary;
  trend: InvestmentTrend | null;
  trendLoading: boolean;
  trendError: string | null;
}) {
  const styles = useStyles();
  const t = useT();
  const currency = summary.investment.currency;
  const sources = summary.sources;
  const credits = sources.credits;
  const metered = summary.metered;
  const points = trend?.points ?? [];
  const maxCredits = Math.max(1, ...points.map((p) => p.credits ?? 0));

  return (
    <div className={styles.stack}>
      <div className={styles.sources}>
        <Card className={styles.source}>
          <Text weight="semibold">{t('copilotInvestment.sources.adoption')}</Text>
          <Text size={200}>
            {sources.adoption.available
              ? t('copilotInvestment.sources.adoption.ok', {
                  holders: formatNumber(summary.evidence.analysedHolders),
                  when: formatShortDate(sources.adoption.analysisGeneratedUtc),
                })
              : t('copilotInvestment.sources.adoption.off')}
          </Text>
          {(summary.investment.seats.otherPeriodHolders > 0 || summary.investment.seats.notAnalysedHolders > 0) && (
            <Text size={200}>
              {t('copilotInvestment.sources.adoption.unmatched', {
                otherPeriod: formatNumber(summary.investment.seats.otherPeriodHolders),
                notAnalysed: formatNumber(summary.investment.seats.notAnalysedHolders),
              })}
            </Text>
          )}
        </Card>
        <Card className={styles.source}>
          <div style={{ display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' }}>
            <Text weight="semibold">{t('copilotInvestment.sources.credits')}</Text>
            <Badge size="small" appearance="outline">
              {t('copilotInvestment.badge.preview')}
            </Badge>
          </div>
          <Text size={200}>
            {/* A failed read is reported as such - before "not upgraded" or "no data", which would read as confirmed absence. */}
            {credits.failureCode
              ? t('copilotInvestment.sources.credits.failed')
              : !credits.tablesPresent
                ? t('copilotInvestment.sources.credits.tablesMissing')
                : credits.hasData
                  ? t('copilotInvestment.sources.credits.ok', {
                      date: formatShortDate(credits.snapshotDate),
                      observed: formatNumber(credits.observedUsers),
                      sweeps: formatNumber(credits.completedSweepsInWindow),
                      started: formatNumber(credits.sweepsInWindow),
                    })
                  : t('copilotInvestment.sources.credits.none')}
          </Text>
          {!credits.collectionEnabled && <Text size={200}>{t('copilotInvestment.availability.creditsDisabled')}</Text>}
        </Card>
        <Card className={styles.source}>
          <Text weight="semibold">{t('copilotInvestment.sources.studio')}</Text>
          <Text size={200}>
            {sources.studio.failureCode
              ? t('copilotInvestment.sources.studio.unreadable')
              : sources.studio.lastImportFailed
                ? t('copilotInvestment.sources.studio.failed')
                : sources.studio.hasData
                  ? t('copilotInvestment.sources.studio.ok', { when: formatShortDate(sources.studio.lastImportUtc) })
                  : t('copilotInvestment.sources.studio.none')}
          </Text>
          {!sources.studio.importEnabled && <Text size={200}>{t('copilotInvestment.availability.studioDisabled')}</Text>}
        </Card>
      </div>

      <Card className={styles.card}>
        <SectionHeading
          title={t('copilotInvestment.trend.title')}
          description={t('copilotInvestment.trend.description')}
          badge={
            <Badge size="small" appearance="outline">
              {t('copilotInvestment.badge.observed')}
            </Badge>
          }
        />
        {trendLoading && <Spinner size="small" label={t('copilotInvestment.trend.loading')} />}
        {trendError && <Text>{trendError}</Text>}
        {!trendLoading && !trendError && points.length === 0 && <Text size={200}>{t('copilotInvestment.trend.empty')}</Text>}
        {points.length > 0 && (
          <>
            <div className={styles.chart} role="list" aria-label={t('copilotInvestment.trend.title')}>
              {points.map((p) => {
                const label = t('copilotInvestment.trend.point', {
                  date: formatShortDate(p.date),
                  credits: formatCredits(p.credits ?? 0),
                  consumers: formatNumber(p.consumingUsers),
                });
                return (
                  <div key={p.date} className={styles.column} role="listitem" aria-label={label} title={label}>
                    <div className={styles.bar} style={{ height: `${((p.credits ?? 0) / maxCredits) * 100}%` }} />
                  </div>
                );
              })}
            </div>
            <div className={styles.axis} aria-hidden="true">
              {points.map((p) => (
                <span key={p.date} className={styles.axisLabel}>
                  {formatShortDate(p.date)}
                </span>
              ))}
            </div>
            <Table size="extra-small" aria-label={t('copilotInvestment.trend.title')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('copilotInvestment.trend.date')}</TableHeaderCell>
                  <TableHeaderCell className={styles.num}>{t('copilotInvestment.trend.credits')}</TableHeaderCell>
                  <TableHeaderCell className={styles.num}>{t('copilotInvestment.trend.consumers')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {points.map((p) => (
                  <TableRow key={p.date}>
                    <TableCell>{formatShortDate(p.date)}</TableCell>
                    <TableCell className={styles.num}>{formatCredits(p.credits ?? 0)}</TableCell>
                    <TableCell className={styles.num}>{formatNumber(p.consumingUsers)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </>
        )}
      </Card>

      <Card className={styles.card}>
        <SectionHeading title={t('copilotInvestment.services.title')} description={t('copilotInvestment.services.description')} />
        {summary.services.length === 0 ? (
          <Text size={200}>{t('copilotInvestment.services.empty')}</Text>
        ) : (
          <Table size="small" aria-label={t('copilotInvestment.services.title')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('copilotInvestment.services.col.service')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.services.col.unit')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.services.col.consumers')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.services.col.quantity')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.services.col.refused')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.services.col.unevaluated')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.services.col.unknown')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {summary.services.map((s) => (
                <TableRow key={`${s.category}|${s.unit ?? ''}`}>
                  <TableCell>
                    <span className={styles.code}>{s.category}</span>
                  </TableCell>
                  <TableCell>
                    {s.unit === CREDIT_UNIT
                      ? t('copilotInvestment.services.unitCredits')
                      : s.unit
                        ? t('copilotInvestment.services.unitOther', { unit: s.unit })
                        : t('copilotInvestment.services.unitMissing')}
                  </TableCell>
                  <TableCell className={styles.num}>{formatNumber(s.consumers)}</TableCell>
                  <TableCell className={styles.num}>{formatCredits(s.quantity)}</TableCell>
                  <TableCell className={styles.num}>{formatNumber(s.limitExhaustedUsers + s.notEntitledUsers)}</TableCell>
                  <TableCell className={styles.num}>{formatNumber(s.unevaluatedUsers)}</TableCell>
                  <TableCell className={styles.num}>{formatNumber(s.unknownAmountUsers)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>

      <Card className={styles.card}>
        <SectionHeading
          title={t('copilotInvestment.metered.title')}
          description={t('copilotInvestment.metered.description')}
        />
        <div className={styles.metered}>
          <Figure label={t('copilotInvestment.metered.people')} value={formatNumber(metered.people)} />
          <Figure label={t('copilotInvestment.metered.active')} value={formatNumber(metered.activeUsers)} />
          <Figure
            label={t('copilotInvestment.metered.studio')}
            value={formatCredits(metered.studioCredits)}
            note={metered.studioWindowValue !== null ? formatMoney(metered.studioWindowValue, currency) : undefined}
          />
          <Figure
            label={t('copilotInvestment.metered.balances')}
            value={formatCredits(metered.credits)}
            note={metered.creditBalanceValue !== null ? formatMoney(metered.creditBalanceValue, currency) : undefined}
          />
          {metered.evidencedCandidates !== null && (
            <Figure label={t('copilotInvestment.metered.evidenced')} value={formatNumber(metered.evidencedCandidates)} />
          )}
          <Figure label={t('copilotInvestment.metered.recommended')} value={formatNumber(metered.recommendedCandidates)} />
          {metered.studioAboveSeatPrice !== null && (
            <Figure label={t('copilotInvestment.metered.studioAboveSeat')} value={formatNumber(metered.studioAboveSeatPrice)} />
          )}
        </div>
      </Card>

      <Card className={styles.card}>
        <SectionHeading
          title={t('copilotInvestment.policies.title')}
          description={t('copilotInvestment.policies.description')}
          badge={
            <Badge size="small" appearance="outline">
              {t('copilotInvestment.badge.tenantWide')}
            </Badge>
          }
        />
        {summary.policies.length === 0 ? (
          <Text size={200}>{t('copilotInvestment.policies.empty')}</Text>
        ) : (
          <Table size="small" aria-label={t('copilotInvestment.policies.title')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('copilotInvestment.policies.col.name')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.policies.col.scope')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.policies.col.status')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.policies.col.billing')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.policies.col.periodLimit')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.policies.col.perUserLimit')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.policies.col.services')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {summary.policies.map((p) => (
                <TableRow key={p.id}>
                  <TableCell>{p.displayName ?? p.id}</TableCell>
                  <TableCell>
                    {p.type === 'allUsers'
                      ? t('copilotInvestment.policies.scope.allUsers')
                      : p.type === 'selectedGroups'
                        ? p.assignedGroups !== null
                          ? t('copilotInvestment.policies.scope.selectedGroups', { count: formatNumber(p.assignedGroups) })
                          : t('copilotInvestment.policies.scope.selectedGroupsUnknown')
                        : t('copilotInvestment.policies.scope.other')}
                  </TableCell>
                  <TableCell>{p.status && POLICY_STATUS_KEYS[p.status] ? t(POLICY_STATUS_KEYS[p.status]) : p.status ?? t('copilotInvestment.value.unknown')}</TableCell>
                  <TableCell>
                    {(p.billingMethods ?? '')
                      .split(',')
                      .map((m) => m.trim())
                      .filter((m) => m.length > 0)
                      .map((m) => (BILLING_METHOD_KEYS[m] ? t(BILLING_METHOD_KEYS[m]) : m))
                      .join(' \u00b7 ')}
                  </TableCell>
                  <TableCell className={styles.num}>{quantityText(t, p.periodLimit)}</TableCell>
                  <TableCell className={styles.num}>{quantityText(t, p.perUserLimit)}</TableCell>
                  <TableCell>
                    <span className={styles.code}>
                      {p.services.filter((s) => s.state === 'enabled').map((s) => s.serviceId).join(', ')}
                    </span>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>

      {summary.tenantBalances.length > 0 && (
        <Card className={styles.card}>
          <SectionHeading
            title={t('copilotInvestment.tenant.title')}
            description={t('copilotInvestment.tenant.description')}
            badge={
              <Badge size="small" appearance="outline">
                {t('copilotInvestment.badge.tenantWide')}
              </Badge>
            }
          />
          <Table size="small" aria-label={t('copilotInvestment.tenant.title')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('copilotInvestment.tenant.col.method')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.tenant.col.purchased')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.tenant.col.consumed')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.tenant.col.remaining')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.tenant.col.updated')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {summary.tenantBalances.map((b) => (
                <TableRow key={b.billingMethod}>
                  <TableCell>{BILLING_METHOD_KEYS[b.billingMethod] ? t(BILLING_METHOD_KEYS[b.billingMethod]) : b.billingMethod}</TableCell>
                  <TableCell className={styles.num}>{b.purchased === null ? t('copilotInvestment.value.unknown') : formatCredits(b.purchased)}</TableCell>
                  <TableCell className={styles.num}>{b.consumed === null ? t('copilotInvestment.value.unknown') : formatCredits(b.consumed)}</TableCell>
                  <TableCell className={styles.num}>{b.remaining === null ? t('copilotInvestment.value.unknown') : formatCredits(b.remaining)}</TableCell>
                  <TableCell>{formatShortDate(b.lastModified)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      )}

      <Card className={styles.card}>
        <SectionHeading title={t('copilotInvestment.quality.title')} />
        <div className={styles.facts}>
          <Text size={200}>{t('copilotInvestment.quality.period')}</Text>
          <Text weight="semibold">
            {credits.periodStatus === 'verified'
              ? t('copilotInvestment.quality.period.verified')
              : credits.periodStatus === 'mixed'
                ? t('copilotInvestment.quality.period.mixed')
                : t('copilotInvestment.quality.period.unverified')}
          </Text>
          <Text size={200}>{t('copilotInvestment.quality.partial')}</Text>
          <Text weight="semibold">{formatNumber(credits.partialUsers)}</Text>
          <Text size={200}>{t('copilotInvestment.quality.stale')}</Text>
          <Text weight="semibold">{formatNumber(credits.staleUsers)}</Text>
          {credits.unresolvedObjects !== null && (
            <>
              <Text size={200}>{t('copilotInvestment.quality.unresolved')}</Text>
              <Text weight="semibold">{formatNumber(credits.unresolvedObjects)}</Text>
              <Text size={200}>{t('copilotInvestment.quality.unresolvedCredits')}</Text>
              <Text weight="semibold">{formatCredits(credits.unresolvedCredits ?? 0)}</Text>
            </>
          )}
          <Text size={200}>{t('copilotInvestment.quality.collector')}</Text>
          <Text weight="semibold">
            {credits.collectorPhase === 'complete'
              ? t('copilotInvestment.quality.collector.complete')
              : credits.collectorPending
                ? t('copilotInvestment.quality.collector.running', { pending: formatNumber(credits.collectorPending) })
                : t('copilotInvestment.quality.collector.other')}
          </Text>
          {credits.directoryUsers !== null && credits.collectorObserved !== null && credits.directoryUsers > 0 && (
            <>
              <Text size={200}>{t('copilotInvestment.trust.creditCoverage', { coverage: formatShare(credits.collectorObserved / credits.directoryUsers) })}</Text>
              <span />
            </>
          )}
        </div>
        {credits.unresolvedObjects === null && summary.filtered && (
          <Text size={200} className={styles.muted}>
            {t('copilotInvestment.quality.filteredNote')}
          </Text>
        )}
      </Card>
    </div>
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
      {note && <Text size={200}>{note}</Text>}
    </div>
  );
}
