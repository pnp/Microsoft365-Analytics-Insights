import { useEffect, useState } from 'react';
import {
  Badge,
  Body1,
  Button,
  Card,
  Checkbox,
  MessageBar,
  MessageBarBody,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular, ChevronLeft16Regular, ChevronRight16Regular } from '@fluentui/react-icons';
import Spinner from '../components/Spinner';
import { fetchAgent365Catalog } from '../api/agent365CatalogApi';
import type { Agent365CatalogPackage, Agent365CatalogResponse } from '../types/agent365Catalog';
import { formatDateParts, formatList, formatNumber, useT } from '../i18n';

const PAGE_SIZE = 50;

const useStyles = makeStyles({
  page: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  heading: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  intro: {
    marginTop: '4px',
  },
  summary: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '20px',
  },
  summaryItem: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  toolbar: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
  },
  spacer: {
    flexGrow: 1,
  },
  tableWrap: {
    overflowX: 'auto',
  },
  pager: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
    marginTop: '12px',
  },
});

function formatDate(value: string | null): string {
  return value
    ? formatDateParts(new Date(value), { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' })
    : '';
}

function usageText(agent: Agent365CatalogPackage, t: ReturnType<typeof useT>): string {
  if (agent.knownNeverUsed) return t('admin.agent365Catalog.usage.neverUsed');
  if (!agent.lastUsedDateTimeProvided) return t('admin.agent365Catalog.usage.unknown');
  return agent.lastUsedUtc ? formatDate(agent.lastUsedUtc) : t('admin.agent365Catalog.usage.unknown');
}

export default function Agent365CatalogPage() {
  const styles = useStyles();
  const t = useT();
  const [data, setData] = useState<Agent365CatalogResponse | null>(null);
  const [offset, setOffset] = useState(0);
  const [neverUsedOnly, setNeverUsedOnly] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const loadError = t('admin.agent365Catalog.error.load');

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetchAgent365Catalog(offset, PAGE_SIZE, neverUsedOnly)
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((reason: unknown) => {
        if (!cancelled) setError(reason instanceof Error ? reason.message : loadError);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [offset, neverUsedOnly, refresh, loadError]);

  const pagePackages = data?.packages ?? [];
  const canGoNext = Boolean(data && data.offset + pagePackages.length < data.totalCount);
  const attemptStatus = data?.lastAttemptSucceeded === true
    ? t('admin.agent365Catalog.status.succeeded')
    : data?.lastAttemptSucceeded === false
      ? t('admin.agent365Catalog.status.failed')
      : t('admin.agent365Catalog.status.none');

  return (
    <div className={styles.page}>
      <div>
        <div className={styles.heading}>
          <Title3>{t('admin.agent365Catalog.title')}</Title3>
          <Button
            appearance="subtle"
            icon={<ArrowClockwise16Regular />}
            onClick={() => setRefresh((value) => value + 1)}
            disabled={loading}
          >
            {t('admin.agent365Catalog.refresh')}
          </Button>
        </div>
        <Body1 className={styles.intro}>{t('admin.agent365Catalog.description')}</Body1>
      </div>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {data && !data.importEnabled && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('admin.agent365Catalog.importDisabled')}</MessageBarBody>
        </MessageBar>
      )}
      {data?.lastAttemptSucceeded === false && (
        <MessageBar intent="error">
          <MessageBarBody>{t('admin.agent365Catalog.error.failed')}</MessageBarBody>
        </MessageBar>
      )}

      {data && (
        <Card className={styles.summary}>
          <div className={styles.summaryItem}>
            <Text weight="semibold">{t('admin.agent365Catalog.summary.importStatus')}</Text>
            <Text>{attemptStatus}</Text>
          </div>
          <div className={styles.summaryItem}>
            <Text weight="semibold">{t('admin.agent365Catalog.summary.lastAttempt')}</Text>
            <Text>{formatDate(data.lastAttemptUtc) || t('admin.agent365Catalog.status.none')}</Text>
          </div>
          <div className={styles.summaryItem}>
            <Text weight="semibold">{t('admin.agent365Catalog.summary.lastSuccess')}</Text>
            <Text>{formatDate(data.lastSuccessfulImportUtc) || t('admin.agent365Catalog.status.none')}</Text>
          </div>
          <div className={styles.summaryItem}>
            <Text weight="semibold">{t('admin.agent365Catalog.summary.packages')}</Text>
            <Text>{formatNumber(data.packageCount)}</Text>
          </div>
          <div className={styles.summaryItem}>
            <Text weight="semibold">{t('admin.agent365Catalog.summary.neverUsed')}</Text>
            <Text>{formatNumber(data.neverUsedCount)}</Text>
          </div>
        </Card>
      )}

      <div className={styles.toolbar}>
        <Checkbox
          checked={neverUsedOnly}
          label={t('admin.agent365Catalog.filter.neverUsed')}
          onChange={(_event, data) => {
            setNeverUsedOnly(data.checked === true);
            setOffset(0);
          }}
        />
        <span className={styles.spacer} />
        {data && (
          <Text size={200} className={styles.muted}>
            {t('admin.agent365Catalog.pagination.count', {
              from: formatNumber(data.totalCount === 0 ? 0 : offset + 1),
              to: formatNumber(offset + pagePackages.length),
              total: formatNumber(data.totalCount),
            })}
          </Text>
        )}
      </div>

      {loading && !data ? (
        <Spinner label={t('admin.agent365Catalog.loading')} />
      ) : data && data.totalCount === 0 ? (
        <Card>
          <Text>{data.packageCount === 0 ? t('admin.agent365Catalog.empty.noSnapshot') : t('admin.agent365Catalog.empty.noMatches')}</Text>
        </Card>
      ) : data ? (
        <>
          <div className={styles.tableWrap}>
            <Table size="small" aria-label={t('admin.agent365Catalog.table.aria')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('admin.agent365Catalog.table.name')}</TableHeaderCell>
                  <TableHeaderCell>{t('admin.agent365Catalog.table.type')}</TableHeaderCell>
                  <TableHeaderCell>{t('admin.agent365Catalog.table.publisher')}</TableHeaderCell>
                  <TableHeaderCell>{t('admin.agent365Catalog.table.lastUsed')}</TableHeaderCell>
                  <TableHeaderCell>{t('admin.agent365Catalog.table.activeUsers')}</TableHeaderCell>
                  <TableHeaderCell>{t('admin.agent365Catalog.table.sessions')}</TableHeaderCell>
                  <TableHeaderCell>{t('admin.agent365Catalog.table.hosts')}</TableHeaderCell>
                  <TableHeaderCell>{t('admin.agent365Catalog.table.state')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {pagePackages.map((agent) => (
                  <TableRow key={agent.packageId}>
                    <TableCell>
                      <Text weight="semibold">{agent.displayName || agent.packageId}</Text>
                      {agent.version && <Text block size={200} className={styles.muted}>{agent.version}</Text>}
                    </TableCell>
                    <TableCell>{agent.packageType || agent.platform || t('admin.common.unknown')}</TableCell>
                    <TableCell>{agent.publisher || t('admin.common.unknown')}</TableCell>
                    <TableCell>{usageText(agent, t)}</TableCell>
                    <TableCell>{agent.activeUsers == null ? t('admin.common.unknown') : formatNumber(agent.activeUsers)}</TableCell>
                    <TableCell>{agent.totalSessions == null ? t('admin.common.unknown') : formatNumber(agent.totalSessions)}</TableCell>
                    <TableCell>
                      {agent.supportedHosts.length
                        ? formatList(agent.supportedHosts)
                        : t('admin.common.unknown')}
                    </TableCell>
                    <TableCell>
                      {agent.isBlocked == null
                        ? t('admin.common.unknown')
                        : <Badge color={agent.isBlocked ? 'danger' : 'success'}>
                            {t(agent.isBlocked ? 'admin.agent365Catalog.state.blocked' : 'admin.agent365Catalog.state.available')}
                          </Badge>}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
          <div className={styles.pager}>
            <Button
              appearance="secondary"
              icon={<ChevronLeft16Regular />}
              onClick={() => setOffset((value) => Math.max(0, value - PAGE_SIZE))}
              disabled={loading || offset === 0}
            >
              {t('admin.agent365Catalog.pagination.previous')}
            </Button>
            <Button
              appearance="secondary"
              icon={<ChevronRight16Regular />}
              iconPosition="after"
              onClick={() => setOffset((value) => value + PAGE_SIZE)}
              disabled={loading || !canGoNext}
            >
              {t('admin.agent365Catalog.pagination.next')}
            </Button>
          </div>
        </>
      ) : null}
    </div>
  );
}
