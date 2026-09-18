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
import type { WebActivityPlatformRow, WebActivityTechnology } from '../../types/webActivity';
import {
  FailedQueryNote,
  SectionCard,
  WindowNote,
  formatCount,
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
  const kpis = data.kpis;

  // Listed rows plus an explicit remainder, so a truncated leaderboard cannot imply it accounts
  // for every page view.
  const deviceCategories = withRemainder(
    data.devices.map((d) => ({ label: d.name, value: d.pageViews })),
    kpis.knownDevicePageViews,
    'Other devices',
  );
  const osCategories = withRemainder(
    data.operatingSystems.map((o) => ({ label: o.name, value: o.pageViews })),
    kpis.pageViews,
    'Other / unknown',
  );

  const items: KpiDefinition[] = [
    {
      key: 'browsers',
      label: 'Browsers',
      value: formatCount(kpis.browsers),
      hint:
        kpis.unknownBrowserPageViews > 0
          ? `${formatCount(kpis.unknownBrowserPageViews)} views unidentified`
          : undefined,
      info: {
        what: 'Distinct browser versions seen.',
        how:
          'Application Insights records browser and version together, so "Chrome 128" and '
          + '"Chrome 129" are two entries. That is what makes this list useful for spotting an old '
          + 'build still in the estate, and what makes the count itself large.',
      },
    },
    {
      key: 'os',
      label: 'Operating systems',
      value: formatCount(kpis.operatingSystems),
      info: { what: 'Distinct operating systems seen.', how: 'Derived from the browser user agent.' },
    },
    {
      key: 'devices',
      label: 'Device types',
      value: formatCount(kpis.devices),
      info: {
        what: 'Distinct device descriptions seen.',
        how: 'Free text from Application Insights rather than a fixed list, so spelling varies between browsers.',
      },
    },
    {
      key: 'mobile',
      label: 'Mobile share',
      value: formatPct(kpis.mobilePct),
      info: {
        what: 'Page views from a phone or tablet, as a share of the views whose device is known.',
        how:
          'The denominator is deliberately the KNOWN devices, not all page views: hits with no '
          + 'device are unmeasured, and folding them in would show a FALLING mobile share whenever '
          + 'device detection got worse. Classification is a documented name-matching heuristic - '
          + 'an unrecognised device counts as not-mobile rather than being guessed at.',
      },
    },
    {
      key: 'load',
      label: 'Average load',
      value: formatSeconds(kpis.averageLoadSeconds),
      tone: loadTone(kpis.averageLoadSeconds),
      info: {
        what: 'Mean page load time as reported by the browser.',
        how: 'Views with no browser-reported load time are excluded rather than counted as zero.',
      },
    },
    {
      key: 'p95',
      label: '95th percentile load',
      value: kpis.p95AtCeiling
        ? `\u2265${formatSeconds(kpis.loadCeilingSeconds)}`
        : formatSeconds(kpis.p95LoadSeconds),
      tone: loadTone(kpis.p95LoadSeconds),
      info: {
        what: 'The load time one page view in twenty is worse than.',
        how:
          'Estimated from a quarter-second histogram rather than an exact percentile, because an '
          + 'exact one has to sort every page view in the window and is the single query on this '
          + 'page most likely to time out. The estimate is rounded UP to the bucket edge, so within '
          + 'the measured range it never flatters the slow tail. Loads slower than the histogram '
          + 'ceiling all share one bucket, so past it the figure is shown as "at least" - the real '
          + 'value could be far worse.',
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
          title="Browsers"
          description="Page views per browser version, with what each one experienced."
          query={queryFor(data.queries, 'tech-browsers')}
          isEmpty={data.browsers.length === 0}
        >
          <PlatformTable rows={data.browsers} heading="Browser" />
        </SectionCard>

        <SectionCard
          title="Devices"
          query={queryFor(data.queries, 'tech-devices')}
          isEmpty={data.devices.length === 0}
        >
          <DonutChart
            categories={deviceCategories}
            colours={deviceCategories.map((_, i) => seriesColor(i))}
            centreValue={formatPct(kpis.mobilePct)}
            centreLabel="mobile"
          />
        </SectionCard>

        <SectionCard
          title="Operating systems"
          query={queryFor(data.queries, 'tech-os')}
          isEmpty={data.operatingSystems.length === 0}
        >
          <CategoryBarChart categories={osCategories} valueLabel="Page views" showShare />
        </SectionCard>

        <SectionCard
          title="Load time by browser"
          description="Average seconds to load, per browser version."
          note="A single browser version far above the others is usually an extension or a policy setting, not the page."
          query={queryFor(data.queries, 'tech-browsers')}
          isEmpty={data.browsers.every((b) => b.averageLoadSeconds === null)}
        >
          <CategoryBarChart
            categories={data.browsers
              .filter((b) => b.averageLoadSeconds !== null)
              .map((b) => ({ label: b.name, value: Math.round((b.averageLoadSeconds ?? 0) * 100) / 100 }))}
            valueLabel="Seconds"
          />
        </SectionCard>
      </div>

      <div className={styles.stack}>
        <SectionCard
          title="Device mix over time"
          description="Weekly page views per device, stacked - the trend that decides whether a responsive-design project is worth funding."
          query={queryFor(data.queries, 'tech-device-over-time')}
          isEmpty={data.deviceOverTime.length === 0}
        >
          <StackedAreaChart series={toStackedSeries(data.deviceOverTime)} valueLabel="Page views" />
        </SectionCard>

        <SectionCard
          title="Detail"
          description="Browser, device, operating system and city together, ranked by visits."
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
              Export
            </Button>
          }
        >
          <div className={styles.tableWrap}>
            <Table size="small" aria-label="Technology detail">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Browser</TableHeaderCell>
                  <TableHeaderCell>Device</TableHeaderCell>
                  <TableHeaderCell>OS</TableHeaderCell>
                  <TableHeaderCell>City</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Visits</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Visitors</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Page views</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Per visit</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Avg time</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Avg load</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.detail.map((row, index) => (
                  <TableRow key={`${row.browser}|${row.device}|${row.operatingSystem}|${row.city}|${index}`}>
                    <TableCell className={styles.td}>{row.browser}</TableCell>
                    <TableCell className={styles.td}>{row.device}</TableCell>
                    <TableCell className={styles.td}>{row.operatingSystem}</TableCell>
                    <TableCell className={styles.td}>{row.city}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.visits)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.visitors)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.pageViews)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>
                      {(Math.round(row.pageViewsPerVisit * 10) / 10).toFixed(1)}
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

  return (
    <div className={styles.tableWrap}>
      <Table size="small" aria-label={heading}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{heading}</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>Page views</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>Share</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>Avg load</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={row.name}>
              <TableCell className={styles.td}>
                <div className={styles.ellipsis} title={row.name}>
                  {row.name}
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
