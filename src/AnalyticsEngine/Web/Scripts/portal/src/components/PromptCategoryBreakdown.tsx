import { Badge, Card, Text, makeStyles, tokens } from '@fluentui/react-components';
import type { PromptCategoryReport } from '../api/promptCategoriesApi';
import { formatNumber, useT } from '../i18n';
import TreemapChart from './charts/TreemapChart';
import { seriesColor } from './charts/chartCommon';

const MODES = ['directing', 'supervising', 'unassigned'] as const;
type Mode = typeof MODES[number];
const modeColor = (mode: Mode) => seriesColor(mode === 'directing' ? 0 : mode === 'supervising' ? 7 : 5);
const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px' },
  cards: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '12px' },
  tile: { display: 'flex', flexDirection: 'column', gap: '6px' },
  heading: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  marker: { width: '12px', height: '12px', borderRadius: tokens.borderRadiusCircular, flexShrink: 0 },
  muted: { color: tokens.colorNeutralForeground2 },
  list: { listStyleType: 'none', padding: 0, margin: 0, display: 'grid', gap: '8px' },
  row: { display: 'flex', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap', overflowWrap: 'anywhere' },
});

export default function PromptCategoryBreakdown({ report, label }: {
  report: PromptCategoryReport; label: (id: string) => string;
}) {
  const styles = useStyles();
  const t = useT();
  const modeLabel = (mode: Mode) => t(mode === 'directing' ? 'promptCategories.directing'
    : mode === 'supervising' ? 'promptCategories.supervising' : 'promptCategories.unassigned');
  const definitions = new Map(report.categories.map(category => [category.id, category]));
  const rows = report.mix.filter(row => row.categoryId !== 'not-classified' && row.prompts > 0)
    .map(row => {
      const assigned = definitions.get(row.categoryId)?.humanMode;
      const mode: Mode = assigned === 'directing' || assigned === 'supervising' ? assigned : 'unassigned';
      return { ...row, mode };
    }).sort((a, b) => b.prompts - a.prompts || (a.categoryId < b.categoryId ? -1 : a.categoryId > b.categoryId ? 1 : 0));
  const total = rows.reduce((sum, row) => sum + row.prompts, 0);
  const modes = MODES.map(mode => ({ mode, prompts: rows.filter(row => row.mode === mode).reduce((sum, row) => sum + row.prompts, 0) }));
  const assigned = modes.filter(row => row.mode !== 'unassigned');
  const maxAssigned = Math.max(...assigned.map(row => row.prompts));
  const leaders = assigned.filter(row => row.prompts === maxAssigned && row.prompts > 0);
  const share = (count: number) => formatNumber(total > 0 ? count / total : 0, { style: 'percent', maximumFractionDigits: 1 });
  const notClassified = report.mix.find(row => row.categoryId === 'not-classified')?.prompts ?? 0;

  return <div className={styles.root}>
    <Text weight="semibold">{t('promptCategories.breakdown.title')}</Text>
    <Text className={styles.muted}>{t('promptCategories.breakdown.basis', { count: formatNumber(total) })}</Text>
    {total > 0 ? <>
      <Text size={500} weight="semibold">{leaders.length === 1
        ? t('promptCategories.breakdown.leading', { mode: modeLabel(leaders[0].mode), share: share(leaders[0].prompts) })
        : t(leaders.length > 1 ? 'promptCategories.breakdown.tied' : 'promptCategories.breakdown.noMode')}</Text>
      <div className={styles.cards}>
        {modes.map(row => <Card key={row.mode} className={styles.tile} size="small">
          <div className={styles.heading}>
            <span className={styles.marker} style={{ backgroundColor: modeColor(row.mode) }} aria-hidden="true" />
            <Text weight="semibold">{modeLabel(row.mode)}</Text>
            {leaders.length === 1 && leaders[0].mode === row.mode &&
              <Badge appearance="tint" color="informative">{t('promptCategories.breakdown.mostPopular')}</Badge>}
          </div>
          <Text size={600} weight="semibold">{share(row.prompts)}</Text>
          <Text>{t('promptCategories.breakdown.count', { count: formatNumber(row.prompts) })}</Text>
        </Card>)}
      </div>
      <Text className={styles.muted}>{t('promptCategories.breakdown.modeNote')}</Text>
      <TreemapChart literalLabels showShare height={340} valueLabel={t('promptCategories.prompts')}
        categories={rows.map(row => ({ label: label(row.categoryId), value: row.prompts, color: modeColor(row.mode) }))} />
      <ul className={styles.list} aria-label={t('promptCategories.breakdown.values')}>
        {rows.map(row => <li key={row.categoryId} className={styles.row}>
          <Text>{label(row.categoryId)}: {formatNumber(row.prompts)} {t('promptCategories.prompts')}</Text>
          <Text>{t('promptCategories.breakdown.row', { share: share(row.prompts), mode: modeLabel(row.mode) })}</Text>
        </li>)}
      </ul>
    </> : <Text>{t('promptCategories.breakdown.noClassified')}</Text>}
    {notClassified > 0 && <Text className={styles.muted}>{t('promptCategories.breakdown.notClassified', { count: formatNumber(notClassified) })}</Text>}
  </div>;
}
