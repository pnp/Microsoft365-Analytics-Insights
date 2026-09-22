import { useState } from 'react';
import {
  Badge,
  Button,
  Tooltip,
  Popover,
  PopoverTrigger,
  PopoverSurface,
  Text,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  Code16Regular,
  Copy16Regular,
  ChevronDown16Regular,
  ChevronRight16Regular,
} from '@fluentui/react-icons';
import toast from '../toast';
import type { UserDataCategory, UserDataDetailRow } from '../../types/userData';
import { fetchUserDetail } from '../../api/userLookupApi';
import Spinner from '../Spinner';
import { formatDateParts, formatNumber, plural, useT } from '../../i18n';

const useStyles = makeStyles({
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    paddingBlock: '10px',
    paddingInline: '12px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  main: {
    flexGrow: 1,
    minWidth: 0,
  },
  desc: {
    color: tokens.colorNeutralForeground3,
  },
  source: {
    color: tokens.colorNeutralForeground3,
    marginTop: '2px',
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap',
  },
  count: {
    minWidth: '64px',
    textAlign: 'right',
    fontVariantNumeric: 'tabular-nums',
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
  },
  sqlBlock: {
    fontFamily: 'Consolas, Menlo, Monaco, "Courier New", monospace',
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    userSelect: 'text',
    margin: 0,
    padding: '8px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  sqlSurface: {
    maxWidth: '560px',
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  detail: {
    paddingInline: '12px',
    paddingBottom: '16px',
    backgroundColor: tokens.colorNeutralBackground2,
  },
});

type CategoryRowProps = {
  upn: string;
  category: UserDataCategory;
};

/** A single category row that can expand to lazily load & show its most recent rows. */
export default function CategoryRow({ upn, category }: CategoryRowProps) {
  const styles = useStyles();
  const t = useT();
  const [expanded, setExpanded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [rows, setRows] = useState<UserDataDetailRow[] | null>(null);
  const [totalCount, setTotalCount] = useState<number | null>(null);

  const canDrill = category.supportsDetail && category.count > 0;

  const toggle = async () => {
    if (expanded) {
      setExpanded(false);
      return;
    }
    setExpanded(true);

    if (rows === null && !loading) {
      setLoading(true);
      setError(null);
      try {
        const resp = await fetchUserDetail(upn, category.key, 50);
        setRows(resp.rows);
        setTotalCount(resp.totalCount);
      } catch (e) {
        setError(e instanceof Error ? e.message : t('admin.userLookup.categoryRow.loadDetailFailed'));
      } finally {
        setLoading(false);
      }
    }
  };

  const copySql = async () => {
    try {
      await navigator.clipboard.writeText(category.sqlQuery);
      toast.success(t('admin.userLookup.categoryRow.sqlCopied'));
    } catch {
      toast.error(t('admin.userLookup.categoryRow.sqlCopyFailed'));
    }
  };

  return (
    <>
      <div className={styles.row}>
        <div className={styles.main}>
          <Text weight="semibold">{category.label}</Text>
          <Text size={200} block className={styles.desc}>
            {category.description}
          </Text>
          <div className={styles.source}>
            <Text size={200}>
              {t('admin.userLookup.categoryRow.source', { source: category.workloads.join(', ') || 'n/a' })}
            </Text>
            {!category.workloadsEnabled && (
              <Tooltip
                relationship="description"
                content={t(
                  plural(
                    category.workloads.length,
                    'admin.userLookup.categoryRow.importOffTooltip.one',
                    'admin.userLookup.categoryRow.importOffTooltip.other',
                  ),
                  { workloads: category.workloads.join('", "') },
                )}
              >
                <Badge appearance="tint" color="warning" size="small">
                  {t('admin.userLookup.categoryRow.importOff')}
                </Badge>
              </Tooltip>
            )}
          </div>
        </div>

        <Text className={styles.count} weight="semibold">
          {formatNumber(category.count)}
        </Text>

        <div className={styles.actions}>
          <Popover withArrow trapFocus>
            <PopoverTrigger disableButtonEnhancement>
              <Button appearance="subtle" size="small" icon={<Code16Regular />}>
                SQL
              </Button>
            </PopoverTrigger>
            <PopoverSurface>
              <div className={styles.sqlSurface}>
                <Text size={200} weight="semibold">
                  {t('admin.userLookup.categoryRow.sqlTitle')}
                </Text>
                <pre className={styles.sqlBlock}>{category.sqlQuery}</pre>
                <div>
                  <Button appearance="primary" size="small" icon={<Copy16Regular />} onClick={copySql}>
                    {t('admin.userLookup.categoryRow.copyToClipboard')}
                  </Button>
                </div>
              </div>
            </PopoverSurface>
          </Popover>
          {canDrill && (
            <Button
              appearance="subtle"
              size="small"
              icon={expanded ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
              onClick={toggle}
            >
              {expanded ? t('admin.userLookup.categoryRow.hideRecent') : t('admin.userLookup.categoryRow.viewRecent')}
            </Button>
          )}
        </div>
      </div>

      {expanded && (
        <div className={styles.detail}>
          {loading && <Spinner size={24} label={t('admin.userLookup.categoryRow.loadingRecentRows')} />}
          {error && <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>}
          {rows &&
            (rows.length === 0 ? (
              <Text style={{ color: tokens.colorNeutralForeground3 }}>{t('admin.userLookup.categoryRow.noRows')}</Text>
            ) : (
              <>
                <Text size={200} block style={{ marginBottom: '8px', color: tokens.colorNeutralForeground3 }}>
                  {t('admin.userLookup.categoryRow.showingRecent', {
                    count: formatNumber(rows.length),
                    total: formatNumber(totalCount ?? category.count),
                  })}
                </Text>
                <Table
                  size="small"
                  aria-label={t('admin.userLookup.categoryRow.recentRowsAriaLabel', { category: category.label })}
                >
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell style={{ width: 200 }}>{t('admin.userLookup.categoryRow.columnWhen')}</TableHeaderCell>
                      <TableHeaderCell>{t('admin.userLookup.categoryRow.columnDetail')}</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {rows.map((r, i) => (
                      <TableRow key={i}>
                        <TableCell>
                          {r.timestamp
                            ? formatDateParts(new Date(r.timestamp), {
                                dateStyle: 'short',
                                timeStyle: 'medium',
                              })
                            : '—'}
                        </TableCell>
                        <TableCell>
                          {r.title ? <strong>{r.title}</strong> : null}
                          {r.title && r.detail ? ' — ' : ''}
                          {r.detail}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </>
            ))}
        </div>
      )}
    </>
  );
}
