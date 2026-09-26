import { Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text } from '@fluentui/react-components';
import type { WebActivityPageRow } from '../../types/webActivity';
import { useT } from '../../i18n';
import {
  dwellCaveat,
  formatCount,
  formatDuration,
  formatPct,
  formatSeconds,
  shortenUrl,
  useWebActivityStyles,
} from './webActivityShared';

/**
 * Which optional columns a page table shows.
 *
 * The main value column is always shown, and `valueHeading` says what it counts. On the entry and
 * exit tables that count IS the number of entries or exits, so those tables leave `entries` /
 * `exits` off rather than printing the same number twice under two headings.
 */
export type PageTableColumns = {
  site?: boolean;
  uniquePageViews?: boolean;
  dwell?: boolean;
  load?: boolean;
  entries?: boolean;
  exits?: boolean;
  bounce?: boolean;
};

/**
 * The one table every page list on this page renders through.
 *
 * Page lists appear on four tabs - most viewed, slowest, quiet, entry and exit - and they all answer
 * the same question about different rows. A single table keeps the column meanings identical
 * everywhere, so a reader who has understood "unique page views" once has understood it everywhere,
 * and a formatting fix lands in all five at the same time.
 *
 * The page title links to the page itself. On a content-pruning list in particular, the very next
 * thing an intranet owner does is open the page and look at it.
 */
export default function PageTable({
  rows,
  columns,
  valueHeading,
  dwellFootnote,
  label,
}: {
  rows: WebActivityPageRow[];
  columns?: PageTableColumns;
  valueHeading?: string;
  /** Shown under the table when it carries a dwell column that needs its caveat repeating. */
  dwellFootnote?: boolean;
  /** Accessible name. Five different tables render through here, so each needs its own. */
  label: string;
}) {
  const styles = useWebActivityStyles();
  const t = useT();
  const show = {
    site: true,
    uniquePageViews: true,
    dwell: true,
    load: false,
    entries: false,
    exits: false,
    bounce: false,
    ...(columns ?? {}),
  };

  return (
    <div className={styles.tableWrap}>
      <Table size="small" aria-label={label}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{t('webActivity.common.page')}</TableHeaderCell>
            {show.site && <TableHeaderCell>{t('webActivity.common.site')}</TableHeaderCell>}
            <TableHeaderCell className={styles.numeric}>{valueHeading ?? t('webActivity.common.pageViews')}</TableHeaderCell>
            {show.uniquePageViews && <TableHeaderCell className={styles.numeric}>{t('webActivity.common.unique')}</TableHeaderCell>}
            {show.dwell && <TableHeaderCell className={styles.numeric}>{t('webActivity.common.avgTime')}</TableHeaderCell>}
            {show.load && <TableHeaderCell className={styles.numeric}>{t('webActivity.common.avgLoad')}</TableHeaderCell>}
            {show.entries && <TableHeaderCell className={styles.numeric}>{t('webActivity.common.entries')}</TableHeaderCell>}
            {show.exits && <TableHeaderCell className={styles.numeric}>{t('webActivity.common.exits')}</TableHeaderCell>}
            {show.bounce && <TableHeaderCell className={styles.numeric}>{t('webActivity.common.bounce')}</TableHeaderCell>}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={row.url}>
              <TableCell className={styles.td}>
                <div className={styles.ellipsis} title={row.url}>
                  <a href={row.url} target="_blank" rel="noopener noreferrer">
                    {row.title || shortenUrl(row.url)}
                  </a>
                </div>
                <Text size={100} className={styles.muted}>
                  {shortenUrl(row.url)}
                </Text>
              </TableCell>
              {show.site && (
                <TableCell className={styles.td}>
                  <div className={styles.ellipsis} title={row.site ?? ''}>
                    {row.site ?? '\u2014'}
                  </div>
                </TableCell>
              )}
              <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.pageViews)}</TableCell>
              {show.uniquePageViews && (
                <TableCell className={`${styles.td} ${styles.numeric}`}>
                  {formatCount(row.uniquePageViews)}
                </TableCell>
              )}
              {show.dwell && (
                <TableCell className={`${styles.td} ${styles.numeric}`}>
                  {formatDuration(row.averageSecondsOnPage)}
                </TableCell>
              )}
              {show.load && (
                <TableCell className={`${styles.td} ${styles.numeric}`}>
                  {formatSeconds(row.averageLoadSeconds)}
                </TableCell>
              )}
              {show.entries && (
                <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.entries)}</TableCell>
              )}
              {show.exits && (
                <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.exits)}</TableCell>
              )}
              {show.bounce && (
                <TableCell className={`${styles.td} ${styles.numeric}`} title={t('webActivity.pageTable.bouncesTitle', { count: formatCount(row.bounces) })}>
                  {formatPct(row.bouncePct)}
                </TableCell>
              )}
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {dwellFootnote && show.dwell && (
        <Text size={100} className={styles.muted} style={{ display: 'block', marginTop: '6px' }}>
          {dwellCaveat(t)}
        </Text>
      )}
    </div>
  );
}
