import {
  Button,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from '@fluentui/react-components';
import { ArrowDownload16Regular } from '@fluentui/react-icons';
import CategoryBarChart from '../charts/CategoryBarChart';
import DonutChart from '../charts/DonutChart';
import StackedAreaChart from '../charts/StackedAreaChart';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import { seriesColor } from '../charts/chartCommon';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import type { WebActivityPlatformRow, WebActivityTechnology } from '../../types/webActivity';
import { useT } from '../../i18n';
import {
  dwellCaveat,
  FailedQueryNote,
  SectionCard,
  WindowNote,
  formatCount,
  formatDecimal,
  formatDuration,
  formatPct,
  formatSeconds,
  loadTone,
  queryFor,
  toStackedSeries,
  useWebActivityStyles,
  withRemainder,
} from './webActivityShared';

/**
 * The Technology tab - the in-app replacement for the Power BI report's technology page, including
 * its browser x device x OS x city detail table.
 *
 * Adds the figure that turns the tab from an inventory into a diagnosis: the 95th-percentile page
 * load. An average of 1.8 seconds with a p95 of 12 is not a fast intranet - it is a fast intranet
 * for most people and an unusable one for a minority, and only the second number will match what
 * that minority tells you.
 */
export default function TechnologyPanel({
  data,
  onExportDetail,
  exporting,
}: {
  data: WebActivityTechnology;
  onExportDetail: () => void;
  exporting: boolean;
}) {
  const styles = useWebActivityStyles();
  const t = useT();
  const kpis = data.kpis;

  // Listed rows plus an explicit remainder, so a truncated leaderboard cannot imply it accounts
  // for every page view.
  const deviceCategories = withRemainder(
    data.devices.map((d) => ({ label: d.name, value: d.pageViews })),
    kpis.knownDevicePageViews,
    t('webActivity.technology.otherDevices'),
  );
  const osCategories = withRemainder(
    data.operatingSystems.map((o) => ({ label: o.name, value: o.pageViews })),
    kpis.pageViews,
    t('webActivity.common.otherUnknown'),
  );

  const items: KpiDefinition[] = [
    {
      key: 'browsers',
      label: t('webActivity.technology.browsers'),
      value: formatCount(kpis.browsers),
      hint:
        kpis.unknownBrowserPageViews > 0
          ? t('webActivity.technology.kpi.viewsUnidentifiedHint', { count: formatCount(kpis.unknownBrowserPageViews) })
          : undefined,
      info: {
        what: t('webActivity.technology.kpi.browsersWhat'),
        how: t('webActivity.technology.kpi.browsersHow'),
      },
    },
    {
      key: 'os',
      label: t('webActivity.technology.operatingSystems'),
      value: formatCount(kpis.operatingSystems),
      info: { what: t('webActivity.technology.kpi.operatingSystemsWhat'), how: t('webActivity.technology.kpi.operatingSystemsHow') },
    },
    {
      key: 'devices',
      label: t('webActivity.technology.kpi.deviceTypes'),
      value: formatCount(kpis.devices),
      info: {
        what: t('webActivity.technology.kpi.deviceTypesWhat'),
        how: t('webActivity.technology.kpi.deviceTypesHow'),
      },
    },
    {
      key: 'mobile',
      label: t('webActivity.technology.kpi.mobileShare'),
      value: formatPct(kpis.mobilePct),
      info: {
        what: t('webActivity.technology.kpi.mobileShareWhat'),
        how: t('webActivity.technology.kpi.mobileHow'),
      },
    },
    {
      key: 'load',
      label: t('webActivity.technology.kpi.averageLoad'),
      value: formatSeconds(kpis.averageLoadSeconds),
      tone: loadTone(kpis.averageLoadSeconds),
      info: {
        what: t('webActivity.technology.kpi.averageLoadWhat'),
        how: t('webActivity.technology.kpi.averageLoadHow'),
      },
    },
    {
      key: 'p95',
      label: t('webActivity.technology.kpi.p95Load'),
      value: kpis.p95AtCeiling
        ? t('webActivity.technology.kpi.p95AtLeast', { seconds: formatSeconds(kpis.loadCeilingSeconds) })
        : formatSeconds(kpis.p95LoadSeconds),
      tone: loadTone(kpis.p95LoadSeconds),
      info: {
        what: t('webActivity.technology.kpi.p95LoadWhat'),
        how: t('webActivity.technology.kpi.p95How'),
      },
    },
  ];

  return (
    <div>
      <div style={{ marginTop: '12px' }}>
        <WindowNote window={data.window} />
      </div>

      <FailedQueryNote queries={data.queries} />

      <div style={{ marginTop: '12px' }}>
        <KpiGrid items={items} />
      </div>

      <div className={styles.grid}>
        <SectionCard
          title={t('webActivity.technology.browsers')}
          description={t('webActivity.technology.browsersDescription')}
          query={queryFor(data.queries, 'tech-browsers')}
          isEmpty={data.browsers.length === 0}
        >
          <PlatformTable rows={data.browsers} heading={t('webActivity.technology.browser')} />
        </SectionCard>

        <SectionCard
          title={t('webActivity.technology.devices')}
          query={queryFor(data.queries, 'tech-devices')}
          isEmpty={data.devices.length === 0}
        >
          <DonutChart
            categories={deviceCategories}
            colours={deviceCategories.map((_, i) => seriesColor(i))}
            centreValue={formatPct(kpis.mobilePct)}
            centreLabel={t('webActivity.technology.mobileLower')}
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.technology.operatingSystems')}
          query={queryFor(data.queries, 'tech-os')}
          isEmpty={data.operatingSystems.length === 0}
        >
          <CategoryBarChart categories={osCategories} valueLabel={t('webActivity.common.pageViews')} showShare />
        </SectionCard>

        <SectionCard
          title={t('webActivity.technology.loadByBrowser.title')}
          description={t('webActivity.technology.loadByBrowser.description')}
          note={t('webActivity.technology.loadByBrowser.note')}
          query={queryFor(data.queries, 'tech-browsers')}
          isEmpty={data.browsers.every((b) => b.averageLoadSeconds === null)}
        >
          <CategoryBarChart
            categories={data.browsers
              .filter((b) => b.averageLoadSeconds !== null)
              .map((b) => ({ label: b.name, value: Math.round((b.averageLoadSeconds ?? 0) * 100) / 100 }))}
            valueLabel={t('webActivity.technology.seconds')}
          />
        </SectionCard>
      </div>

      <div className={styles.stack}>
        <SectionCard
          title={t('webActivity.technology.deviceMix.title')}
          description={t('webActivity.technology.deviceMix.description')}
          query={queryFor(data.queries, 'tech-device-over-time')}
          isEmpty={data.deviceOverTime.length === 0}
        >
          <StackedAreaChart series={toStackedSeries(data.deviceOverTime)} valueLabel={t('webActivity.common.pageViews')} />
        </SectionCard>

        <SectionCard
          title={t('webActivity.technology.detail.title')}
          description={t('webActivity.technology.detail.description')}
          note={dwellCaveat(t)}
          query={queryFor(data.queries, 'tech-detail')}
          isEmpty={data.detail.length === 0}
          actions={
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportDetail}
              disabled={exporting}
            >
              {t('webActivity.common.export')}
            </Button>
          }
        >
          <div className={styles.tableWrap}>
            <Table size="small" aria-label={t('webActivity.technology.detail.aria')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('webActivity.technology.browser')}</TableHeaderCell>
                  <TableHeaderCell>{t('webActivity.technology.device')}</TableHeaderCell>
                  <TableHeaderCell>{t('webActivity.technology.os')}</TableHeaderCell>
                  <TableHeaderCell>{t('webActivity.geography.city')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.visits')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.visitors')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.pageViews')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.perVisit')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.avgTime')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.avgLoad')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.detail.map((row, index) => (
                  <TableRow key={`${row.browser}|${row.device}|${row.operatingSystem}|${row.city}|${index}`}>
                    <TableCell className={styles.td}>{serverPlaceholderText(t, row.browser)}</TableCell>
                    <TableCell className={styles.td}>{serverPlaceholderText(t, row.device)}</TableCell>
                    <TableCell className={styles.td}>{serverPlaceholderText(t, row.operatingSystem)}</TableCell>
                    <TableCell className={styles.td}>{serverPlaceholderText(t, row.city)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.visits)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.visitors)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.pageViews)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>
                      {row.pageViewsPerVisit != null ? formatDecimal(row.pageViewsPerVisit) : '\u2014'}
                    </TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>
                      {formatDuration(row.averageSecondsOnPage)}
                    </TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>
                      {formatSeconds(row.averageLoadSeconds)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>
      </div>
    </div>
  );
}

function PlatformTable({ rows, heading }: { rows: WebActivityPlatformRow[]; heading: string }) {
  const styles = useWebActivityStyles();
  const t = useT();

  return (
    <div className={styles.tableWrap}>
      <Table size="small" aria-label={heading}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{heading}</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>{t('webActivity.common.pageViews')}</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>{t('webActivity.common.share')}</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>{t('webActivity.common.avgLoad')}</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={row.name}>
              <TableCell className={styles.td}>
                <div className={styles.ellipsis} title={serverPlaceholderText(t, row.name)}>
                  {serverPlaceholderText(t, row.name)}
                </div>
              </TableCell>
              <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.pageViews)}</TableCell>
              <TableCell className={`${styles.td} ${styles.numeric}`}>{formatPct(row.sharePct)}</TableCell>
              <TableCell className={`${styles.td} ${styles.numeric}`}>
                {formatSeconds(row.averageLoadSeconds)}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
