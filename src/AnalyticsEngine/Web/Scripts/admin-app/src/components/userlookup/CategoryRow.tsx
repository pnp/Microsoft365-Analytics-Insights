import { useState } from 'react';
import {
  Badge,
  Button,
  Tooltip,
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
  Info16Regular,
  ChevronDown16Regular,
  ChevronRight16Regular,
} from '@fluentui/react-icons';
import toast from 'react-hot-toast';
import type { UserDataCategory, UserDataDetailRow } from '../../types/userData';
import { fetchUserDetail } from '../../api/userLookupApi';
import Spinner from '../Spinner';

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
  sqlTooltip: {
    fontFamily: 'Consolas, Menlo, Monaco, "Courier New", monospace',
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    margin: 0,
    maxWidth: '520px',
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
        setError(e instanceof Error ? e.message : 'Failed to load detail.');
      } finally {
        setLoading(false);
      }
    }
  };

  const copySql = async () => {
    try {
      await navigator.clipboard.writeText(category.sqlQuery);
      toast.success('SQL copied to clipboard');
    } catch {
      toast.error('Could not copy to clipboard');
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
            <Text size={200}>Source: {category.workloads.join(', ') || 'n/a'}</Text>
            {!category.workloadsEnabled && (
              <Tooltip
                relationship="description"
                content={`This data isn't being imported (workload${
                  category.workloads.length === 1 ? '' : 's'
                } "${category.workloads.join('", "')}" disabled), so a count of 0 is expected.`}
              >
                <Badge appearance="tint" color="warning" size="small">
                  import off
                </Badge>
              </Tooltip>
            )}
          </div>
        </div>

        <Text className={styles.count} weight="semibold">
          {category.count.toLocaleString()}
        </Text>

        <div className={styles.actions}>
          <Tooltip
            relationship="description"
            content={<pre className={styles.sqlTooltip}>{category.sqlQuery}</pre>}
          >
            <Button
              appearance="subtle"
              size="small"
              icon={<Info16Regular />}
              aria-label="Show the SQL query for this count (click to copy)"
              onClick={copySql}
            />
          </Tooltip>
          {canDrill && (
            <Button
              appearance="subtle"
              size="small"
              icon={expanded ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
              onClick={toggle}
            >
              {expanded ? 'Hide' : 'View recent'}
            </Button>
          )}
        </div>
      </div>

      {expanded && (
        <div className={styles.detail}>
          {loading && <Spinner size={24} label="Loading recent rows..." />}
          {error && <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>}
          {rows &&
            (rows.length === 0 ? (
              <Text style={{ color: tokens.colorNeutralForeground3 }}>No rows.</Text>
            ) : (
              <>
                <Text size={200} block style={{ marginBottom: '8px', color: tokens.colorNeutralForeground3 }}>
                  Showing {rows.length} most recent of {(totalCount ?? category.count).toLocaleString()}.
                </Text>
                <Table size="small" aria-label={`${category.label} recent rows`}>
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell style={{ width: 200 }}>When</TableHeaderCell>
                      <TableHeaderCell>Detail</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {rows.map((r, i) => (
                      <TableRow key={i}>
                        <TableCell>{r.timestamp ? new Date(r.timestamp).toLocaleString() : '—'}</TableCell>
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
