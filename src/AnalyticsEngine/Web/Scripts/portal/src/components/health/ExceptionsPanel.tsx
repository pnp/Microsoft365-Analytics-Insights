import {
  MessageBar,
  MessageBarBody,
  Table,
  TableHeader,
  TableRow,
  TableHeaderCell,
  TableBody,
  TableCell,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { fetchHealthExceptions } from '../../api/healthApi';
import { formatNumber, useT } from '../../i18n';
import { SectionFrame, buildHourBuckets, useHealthSection, useHealthStyles } from './healthShared';
import { shortenProblemId } from './problemId';

const useStyles = makeStyles({
  bigNumber: {
    fontSize: '40px',
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: '1',
  },
  spark: {
    display: 'flex',
    alignItems: 'flex-end',
    height: '90px',
    gap: '2px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    marginBottom: '12px',
  },
});

/** Exceptions overview (App Insights): a cheap catch-all early-warning of failures. */
export default function ExceptionsPanel({ active }: { active: boolean }) {
  const t = useT();
  const shared = useHealthStyles();
  const styles = useStyles();
  const state = useHealthSection(fetchHealthExceptions, active);

  return (
    <SectionFrame
      title={t('health.exceptions.title')}
      description={t('health.exceptions.description')}
      state={state}
    >
      {(data: any) => {
        if (!data.appInsightsConfigured) {
          return (
            <MessageBar intent="info">
              <MessageBarBody>{t('health.exceptions.appInsightsNotConfigured')}</MessageBarBody>
            </MessageBar>
          );
        }
        if (data.exceptionsError) {
          return (
            <MessageBar intent="warning">
              <MessageBarBody>{t('health.exceptions.loadError', { error: data.exceptionsError })}</MessageBarBody>
            </MessageBar>
          );
        }

        const hourBuckets = buildHourBuckets(data.exceptionsPerHour);
        const maxHourCount = Math.max(1, ...hourBuckets.map((h) => h.count));

        return (
          <>
            <div>
              <span className={styles.bigNumber}>{formatNumber(data.exceptionsLast24h)}</span>{' '}
              <Text>{t('health.exceptions.last24hLabel')}</Text>
            </div>

            {data.sqlCapacityExceptions24h > 0 && (
              <div style={{ marginTop: 8 }}>
                <MessageBar intent="error">
                  <MessageBarBody>
                    {t('health.exceptions.sqlCapacityWarning', { count: formatNumber(data.sqlCapacityExceptions24h) })}
                  </MessageBarBody>
                </MessageBar>
              </div>
            )}

            <Text className={shared.subHeading}>{t('health.exceptions.perHourHeading')}</Text>
            <div className={styles.spark}>
              {hourBuckets.map((h, i) => {
                const pct = Math.round((100 * h.count) / maxHourCount);
                const label = `${h.hourUtc ? new Date(h.hourUtc).toISOString().slice(11, 16) : '?'} UTC: ${h.count}`;
                return (
                  <div
                    key={h.hourUtc ?? i}
                    title={label}
                    style={{
                      flex: 1,
                      minWidth: '4px',
                      height: `${Math.max(pct, 2)}%`,
                      backgroundColor: h.count > 0 ? '#c50f1f' : '#e0e0e0',
                    }}
                  />
                );
              })}
            </div>

            <Text className={shared.subHeading}>{t('health.exceptions.topTypesHeading')}</Text>
            {data.topExceptionTypes.length > 0 ? (
              <Table size="small" aria-label={t('health.exceptions.topTypesAriaLabel')}>
                <colgroup>
                  <col style={{ width: '30%' }} />
                  <col />
                  <col style={{ width: '90px' }} />
                </colgroup>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>{t('health.exceptions.columnType')}</TableHeaderCell>
                    <TableHeaderCell>{t('health.exceptions.columnProblemId')}</TableHeaderCell>
                    <TableHeaderCell className={shared.numeric}>{t('health.exceptions.columnCount')}</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.topExceptionTypes.map((t: any, i: number) => (
                    <TableRow key={(t.type ?? '') + (t.problemId ?? '') + i}>
                      <TableCell className={shared.breakAnywhere}>
                        <Text font="monospace" size={200}>
                          {t.type}
                        </Text>
                      </TableCell>
                      <TableCell className={shared.breakAnywhere}>
                        <Text size={200} title={t.problemId ?? undefined}>
                          {shortenProblemId(t.problemId, t.type)}
                        </Text>
                      </TableCell>
                      <TableCell className={shared.numeric}>{formatNumber(t.count)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            ) : (
              <Text style={{ color: tokens.colorPaletteGreenForeground1 }}>{t('health.exceptions.empty')}</Text>
            )}
          </>
        );
      }}
    </SectionFrame>
  );
}
