import { useMemo, useState, type ReactNode } from 'react';
import {
  Card,
  Link,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  ToggleButton,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { formatNumber, plural, useT, type TFunction } from '../../i18n';
import type { SpendDistribution, SpendParetoPoint, SpendRankedPerson } from '../../types/copilotSpend';
import { LabelledBars, SectionHeading, formatCredits, formatDecimal, formatShare, formatShareAboveZero } from '../copilotInvestment/investmentShared';

type Source = 'studio' | 'balances';

const useStyles = makeStyles({
  card: { display: 'flex', flexDirection: 'column', gap: '14px', padding: '16px' },
  toggles: { display: 'flex', flexWrap: 'wrap', gap: '8px', alignItems: 'center' },
  headline: { fontSize: tokens.fontSizeBase500, lineHeight: tokens.lineHeightBase500, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  layout: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(min(320px, 100%), 1fr))',
    gap: '20px',
    alignItems: 'start',
  },
  figure: { margin: 0, display: 'flex', flexDirection: 'column', gap: '6px', minWidth: 0 },
  pareto: { display: 'block', width: '100%', maxWidth: '560px', height: 'auto', overflow: 'visible' },
  axisText: { fill: tokens.colorNeutralForeground3, fontSize: '11px', fontVariantNumeric: 'tabular-nums' },
  axisTitle: { fill: tokens.colorNeutralForeground2, fontSize: '12px' },
  legend: { display: 'flex', flexWrap: 'wrap', gap: '4px 16px' },
  legendItem: { display: 'inline-flex', alignItems: 'center', gap: '6px' },
  swatchLine: { width: '16px', height: '3px', borderRadius: '2px', backgroundColor: tokens.colorBrandForeground1, flexShrink: 0 },
  swatchDashed: { width: '16px', height: '0px', borderTop: `2px dashed ${tokens.colorNeutralStroke1}`, flexShrink: 0 },
  swatchDot: { width: '9px', height: '9px', borderRadius: '50%', backgroundColor: tokens.colorBrandForeground1, flexShrink: 0 },
  tiles: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))', gap: '8px' },
  tile: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  tileValue: { fontSize: tokens.fontSizeBase500, lineHeight: tokens.lineHeightBase500, fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums' },
  section: { display: 'flex', flexDirection: 'column', gap: '8px', minWidth: 0 },
  tables: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(340px, 100%), 1fr))', gap: '20px', alignItems: 'start' },
  tableWrap: { overflowX: 'auto' },
  // Three columns, with the department under the person: these tables sit two to a row, and a fourth column left
  // "Departamento" and "Créditos facturados" overlapping on a phone and touching on a 1,100px laptop.
  colPerson: { width: '50%' },
  colCredits: { width: '25%' },
  colShare: { width: '25%' },
  personCell: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '2px', paddingBlock: '4px', minWidth: 0 },
  personLink: { textAlign: 'start', overflowWrap: 'anywhere' },
  department: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, lineHeight: tokens.lineHeightBase200, overflowWrap: 'anywhere' },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
  numHeader: { textAlign: 'right', justifyContent: 'flex-end' },
  // The figure sits above its bar rather than beside it, so a narrow cell cannot push the bar over the credits column.
  shareCell: { display: 'flex', flexDirection: 'column', alignItems: 'stretch', gap: '4px', minWidth: 0 },
  shareTrack: { height: '6px', borderRadius: '3px', backgroundColor: tokens.colorNeutralBackground4, overflow: 'hidden' },
  shareBar: { height: '100%', backgroundColor: tokens.colorBrandBackground },
});

interface SpendDistributionPanelProps {
  studio?: SpendDistribution | null;
  balances?: SpendDistribution | null;
  canSeePii: boolean;
  onOpenPerson: (userId: number, name: string) => void;
}

/**
 * Who consumed the credits: whether a few people account for most of them or many people a little each. One source at a
 * time - billed Studio credits sum over the period, a reading is a balance on a date - so the two are never combined.
 */
export default function SpendDistributionPanel({ studio, balances, canSeePii, onOpenPerson }: SpendDistributionPanelProps) {
  const t = useT();
  const styles = useStyles();
  const preferred: Source = studio && studio.consumers > 0 ? 'studio' : balances ? 'balances' : 'studio';
  const [chosen, setChosen] = useState<Source | null>(null);
  if (!studio && !balances) return null;
  const source: Source = chosen === 'studio' && studio ? 'studio' : chosen === 'balances' && balances ? 'balances' : preferred;
  const distribution = (source === 'studio' ? studio : balances)!;

  return (
    <Card className={styles.card}>
      <SectionHeading title={t('copilotSpend.distribution.title')} description={t('copilotSpend.distribution.description')} />
      {studio && balances && (
        <div role="group" aria-label={t('copilotSpend.distribution.sourceLabel')} className={styles.toggles}>
          <ToggleButton size="small" checked={source === 'studio'} onClick={() => setChosen('studio')}>
            {t('copilotSpend.people.source.studio')}
          </ToggleButton>
          <ToggleButton size="small" checked={source === 'balances'} onClick={() => setChosen('balances')}>
            {t('copilotSpend.people.source.balances')}
          </ToggleButton>
        </div>
      )}
      <DistributionView distribution={distribution} source={source} canSeePii={canSeePii} onOpenPerson={onOpenPerson} />
    </Card>
  );
}

/** The one-sentence answer to "is the spend concentrated?", or why it cannot be given. */
export function distributionHeadline(t: TFunction, d: SpendDistribution): string {
  if (d.withheld) return t('copilotSpend.distribution.withheld', { count: formatNumber(d.minPeople) });
  if (d.consumers === 0) return t('copilotSpend.distribution.nobody');
  if (d.peopleForHalf !== null) {
    return t(plural(d.peopleForHalf, 'copilotSpend.distribution.half.one', 'copilotSpend.distribution.half.other'), {
      people: formatNumber(d.peopleForHalf),
      consumers: formatNumber(d.consumers),
      share: formatShare(d.peopleForHalf / d.consumers),
    });
  }
  if (d.halfFromFewerThanMin) {
    return t('copilotSpend.distribution.halfFewer', { count: formatNumber(d.minPeople), consumers: formatNumber(d.consumers) });
  }
  return t('copilotSpend.distribution.consumers', { consumers: formatNumber(d.consumers) });
}

function DistributionView({
  distribution: d,
  source,
  canSeePii,
  onOpenPerson,
}: {
  distribution: SpendDistribution;
  source: Source;
  canSeePii: boolean;
  onOpenPerson: (userId: number, name: string) => void;
}) {
  const t = useT();
  const styles = useStyles();
  const headline = distributionHeadline(t, d);
  const showFigures = !d.withheld && d.consumers > 0;
  // One scale for both tables, so the smallest consumers' bars read against the largest consumer's.
  const barScale = d.max !== null && d.max > 0 ? d.max : Math.max(0, ...d.top.map((row) => row.credits ?? 0));

  const credits = (value: number | null) => t('copilotSpend.distribution.creditsValue', { credits: formatCredits(value) });
  const tiles: { key: string; label: string; value: string; hint?: string }[] = [];
  if (showFigures) {
    if (d.median !== null) tiles.push({ key: 'median', label: t('copilotSpend.distribution.stat.median'), value: formatCredits(d.median) });
    if (d.mean !== null) tiles.push({ key: 'mean', label: t('copilotSpend.distribution.stat.mean'), value: formatCredits(d.mean) });
    if (d.p90 !== null) tiles.push({ key: 'p90', label: t('copilotSpend.distribution.stat.p90'), value: formatCredits(d.p90), hint: t('copilotSpend.distribution.stat.p90Hint') });
    if (d.top10Share !== null) tiles.push({ key: 'top10', label: t('copilotSpend.distribution.stat.top10'), value: formatShare(d.top10Share) });
    if (d.top20Share !== null) tiles.push({ key: 'top20', label: t('copilotSpend.distribution.stat.top20'), value: formatShare(d.top20Share) });
    if (d.gini !== null) {
      tiles.push({ key: 'gini', label: t('copilotSpend.distribution.stat.gini'), value: formatDecimal(d.gini, 2), hint: t('copilotSpend.distribution.stat.giniHint') });
    }
    if (source === 'balances' && d.zeroPeople !== null) {
      tiles.push({ key: 'zero', label: t('copilotSpend.distribution.stat.zero'), value: formatNumber(d.zeroPeople), hint: t('copilotSpend.distribution.stat.zeroHint') });
    }
    if (d.min !== null) tiles.push({ key: 'min', label: t('copilotSpend.distribution.stat.min'), value: formatCredits(d.min) });
    if (d.max !== null) tiles.push({ key: 'max', label: t('copilotSpend.distribution.stat.max'), value: formatCredits(d.max) });
  }

  const bands = d.bands.map((b) => ({
    key: `${b.from}-${b.to}`,
    label: t('copilotSpend.distribution.band', { from: formatCredits(b.from), to: formatCredits(b.to) }),
    value: b.people,
    display: t(plural(b.people, 'copilotSpend.distribution.bandPeople.one', 'copilotSpend.distribution.bandPeople.other'), { people: formatNumber(b.people) }),
    note: b.credits === null ? undefined : t('copilotSpend.distribution.bandCredits', { credits: formatCredits(b.credits) }),
  }));

  return (
    <>
      <Text as="p" className={styles.headline}>
        {headline}
      </Text>
      {showFigures && (
        <div className={styles.layout}>
          {d.pareto.length > 1 && <ParetoChart points={d.pareto} ariaLabel={t('copilotSpend.distribution.paretoAria', { headline })} />}
          <div className={styles.section}>
            <div className={styles.tiles}>
              {tiles.map((tile) => (
                <div key={tile.key} className={styles.tile}>
                  <Text size={200} className={styles.muted}>
                    {tile.label}
                  </Text>
                  <Text className={styles.tileValue}>{tile.value}</Text>
                  {tile.hint && (
                    <Text size={100} className={styles.muted}>
                      {tile.hint}
                    </Text>
                  )}
                </div>
              ))}
            </div>
            {bands.length > 0 && (
              <div className={styles.section}>
                <Text weight="semibold">{t('copilotSpend.distribution.bandsTitle')}</Text>
                <LabelledBars rows={bands} />
              </div>
            )}
          </div>
        </div>
      )}
      {showFigures && canSeePii && (d.top.length > 0 || d.lowest.length > 0) && (
        <>
          <div className={styles.tables}>
            {d.top.length > 0 && (
              <RankedTable
                title={t('copilotSpend.distribution.topTitle')}
                rows={d.top}
                scale={barScale}
                source={source}
                credits={credits}
                onOpenPerson={onOpenPerson}
              />
            )}
            {d.lowest.length > 0 && (
              <RankedTable
                title={t('copilotSpend.distribution.lowestTitle')}
                rows={d.lowest}
                scale={barScale}
                source={source}
                credits={credits}
                onOpenPerson={onOpenPerson}
              />
            )}
          </div>
          <Text size={200} className={styles.muted}>
            {t('copilotSpend.distribution.barsNote')}
          </Text>
        </>
      )}
      {showFigures && !canSeePii && (
        <Text size={200} className={styles.muted}>
          {t('copilotSpend.distribution.anonymised', { count: formatNumber(d.minPeople) })}
        </Text>
      )}
    </>
  );
}

function RankedTable({
  title,
  rows,
  scale,
  source,
  credits,
  onOpenPerson,
}: {
  title: string;
  rows: SpendRankedPerson[];
  scale: number;
  source: Source;
  credits: (value: number | null) => string;
  onOpenPerson: (userId: number, name: string) => void;
}) {
  const t = useT();
  const styles = useStyles();
  return (
    <div className={styles.section}>
      <Text weight="semibold">{title}</Text>
      <div className={styles.tableWrap}>
        <Table size="small" aria-label={title}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell className={styles.colPerson}>{t('copilotSpend.people.col.person')}</TableHeaderCell>
              <TableHeaderCell className={mergeClasses(styles.colCredits, styles.numHeader)} button={{ className: styles.numHeader }}>
                {t(source === 'studio' ? 'copilotSpend.studio.col.credits' : 'copilotSpend.people.col.balance')}
              </TableHeaderCell>
              <TableHeaderCell className={mergeClasses(styles.colShare, styles.numHeader)} button={{ className: styles.numHeader }}>
                {t('copilotSpend.distribution.col.share')}
              </TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((row) => {
              const name = row.userPrincipalName ?? String(row.userId);
              const bar = scale > 0 && row.credits !== null ? Math.min(1, Math.max(0, row.credits / scale)) : 0;
              return (
                <TableRow key={row.userId}>
                  <TableCell>
                    <div className={styles.personCell}>
                      <Link
                        as="button"
                        className={styles.personLink}
                        onClick={() => onOpenPerson(row.userId, name)}
                        aria-label={t('copilotSpend.people.open', { name })}
                      >
                        {breakableName(name)}
                      </Link>
                      <span className={styles.department}>{row.department ?? t('copilotSpend.distribution.noDepartment')}</span>
                    </div>
                  </TableCell>
                  <TableCell className={styles.num} aria-label={credits(row.credits)}>
                    {formatCredits(row.credits)}
                  </TableCell>
                  <TableCell>
                    <div className={styles.shareCell}>
                      <span className={styles.num}>{formatShareAboveZero(row.share)}</span>
                      <div className={styles.shareTrack} aria-hidden="true">
                        <div className={styles.shareBar} style={{ width: `${bar * 100}%` }} />
                      </div>
                    </div>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </div>
    </div>
  );
}

/** A sign-in name that may wrap before its domain rather than spill into the next column. */
export function breakableName(name: string): ReactNode {
  const at = name.indexOf('@');
  if (at <= 0) return name;
  return (
    <>
      {name.slice(0, at)}
      <wbr />
      {name.slice(at)}
    </>
  );
}

const PW = 420;
const PH = 270;
const PL = 58;
const PR = 14;
const PT = 12;
const PB = 46;

/**
 * The cumulative share of the credits against the share of the people, largest consumers first. The points are not evenly
 * spaced along the people axis, so each is placed at its own share rather than at its index.
 */
function ParetoChart({ points, ariaLabel }: { points: SpendParetoPoint[]; ariaLabel: string }) {
  const t = useT();
  const styles = useStyles();
  const pts = useMemo(() => {
    const sorted = points
      .filter((p) => Number.isFinite(p.peopleShare) && Number.isFinite(p.creditShare))
      .sort((a, b) => a.peopleShare - b.peopleShare || a.creditShare - b.creditShare);
    if (sorted.length === 0 || sorted[0].peopleShare > 0) sorted.unshift({ people: 0, peopleShare: 0, creditShare: 0 });
    return sorted;
  }, [points]);

  const iw = PW - PL - PR;
  const ih = PH - PT - PB;
  const x = (v: number) => PL + Math.min(1, Math.max(0, v)) * iw;
  const y = (v: number) => PT + (1 - Math.min(1, Math.max(0, v))) * ih;
  const line = pts.map((p, i) => `${i ? 'L' : 'M'}${x(p.peopleShare).toFixed(1)},${y(p.creditShare).toFixed(1)}`).join(' ');
  const last = pts[pts.length - 1];
  const area = `${line} L${x(last.peopleShare).toFixed(1)},${y(0).toFixed(1)} L${x(0).toFixed(1)},${y(0).toFixed(1)} Z`;

  let half: number | null = null;
  for (let i = 1; i < pts.length && half === null; i++) {
    const a = pts[i - 1];
    const b = pts[i];
    if (a.creditShare < 0.5 && b.creditShare >= 0.5) {
      const rise = b.creditShare - a.creditShare;
      half = rise > 0 ? a.peopleShare + ((0.5 - a.creditShare) * (b.peopleShare - a.peopleShare)) / rise : b.peopleShare;
    }
  }

  const ticks = [0, 0.25, 0.5, 0.75, 1];
  return (
    <figure className={styles.figure}>
      <Text weight="semibold">{t('copilotSpend.distribution.paretoTitle')}</Text>
      <svg viewBox={`0 0 ${PW} ${PH}`} role="img" aria-label={ariaLabel} className={styles.pareto}>
        {ticks.map((v) => (
          <g key={`y${v}`}>
            <line x1={x(0)} x2={x(1)} y1={y(v)} y2={y(v)} stroke={tokens.colorNeutralStroke3} strokeWidth={1} />
            <text x={PL - 8} y={y(v)} textAnchor="end" dominantBaseline="middle" className={styles.axisText}>
              {formatShare(v)}
            </text>
          </g>
        ))}
        {ticks.map((v) => (
          <text key={`x${v}`} x={x(v)} y={PT + ih + 16} textAnchor="middle" className={styles.axisText}>
            {formatShare(v)}
          </text>
        ))}
        <line x1={x(0)} y1={y(0)} x2={x(1)} y2={y(1)} stroke={tokens.colorNeutralStroke1} strokeWidth={1.5} strokeDasharray="5 4" />
        <path d={area} fill={tokens.colorBrandBackground} fillOpacity={0.12} />
        <path d={line} fill="none" stroke={tokens.colorBrandForeground1} strokeWidth={2.5} strokeLinejoin="round" strokeLinecap="round" />
        {half !== null && (
          <>
            <line x1={x(0)} x2={x(half)} y1={y(0.5)} y2={y(0.5)} stroke={tokens.colorNeutralForeground3} strokeWidth={1} strokeDasharray="3 3" />
            <line x1={x(half)} x2={x(half)} y1={y(0.5)} y2={y(0)} stroke={tokens.colorNeutralForeground3} strokeWidth={1} strokeDasharray="3 3" />
            <circle cx={x(half)} cy={y(0.5)} r={5} fill={tokens.colorBrandForeground1} stroke={tokens.colorNeutralBackground1} strokeWidth={2} />
          </>
        )}
        <text x={PL + iw / 2} y={PH - 6} textAnchor="middle" className={styles.axisTitle}>
          {t('copilotSpend.distribution.paretoX')}
        </text>
        <text transform={`translate(12 ${PT + ih / 2}) rotate(-90)`} textAnchor="middle" className={styles.axisTitle}>
          {t('copilotSpend.distribution.paretoY')}
        </text>
      </svg>
      <div className={styles.legend} aria-hidden="true">
        <span className={styles.legendItem}>
          <span className={styles.swatchLine} />
          <Text size={200}>{t('copilotSpend.distribution.paretoCurve')}</Text>
        </span>
        <span className={styles.legendItem}>
          <span className={styles.swatchDashed} />
          <Text size={200}>{t('copilotSpend.distribution.paretoEqual')}</Text>
        </span>
        {half !== null && (
          <span className={styles.legendItem}>
            <span className={styles.swatchDot} />
            <Text size={200}>{t('copilotSpend.distribution.paretoHalf')}</Text>
          </span>
        )}
      </div>
    </figure>
  );
}
