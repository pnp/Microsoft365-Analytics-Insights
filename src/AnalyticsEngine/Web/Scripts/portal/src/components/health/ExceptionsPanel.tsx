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
  const shared = useHealthStyles();
  const styles = useStyles();
  const state = useHealthSection(fetchHealthExceptions, active);

  return (
    <SectionFrame
      title="Exceptions overview (last 24h)"
      description="A cheap catch-all: every web-job logs errors into Application Insights, so a rising count is an early warning of failures no specific check anticipates."
      state={state}
    >
      {(data) => {
        if (!data.appInsightsConfigured) {
          return (
            <MessageBar intent="info">
              <MessageBarBody>Application Insights is not configured, so the exceptions overview is unavailable.</MessageBarBody>
            </MessageBar>
          );
        }
        if (data.exceptionsError) {
          return (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load exceptions: {data.exceptionsError}</MessageBarBody>
            </MessageBar>
          );
        }

        const hourBuckets = buildHourBuckets(data.exceptionsPerHour);
        const maxHourCount = Math.max(1, ...hourBuckets.map((h) => h.count));

        return (
          <>
            <div>
              <span className={styles.bigNumber}>{data.exceptionsLast24h.toLocaleString()}</span>{' '}
              <Text>exceptions in the last 24 hours</Text>
            </div>

            {data.sqlCapacityExceptions24h > 0 && (
              <div style={{ marginTop: 8 }}>
                <MessageBar intent="error">
                  <MessageBarBody>
                    {data.sqlCapacityExceptions24h.toLocaleString()} of these look like SQL capacity / read-only
                    failures - check the database storage / edition. This usually means data has stopped being written.
                  </MessageBarBody>
                </MessageBar>
              </div>
            )}

            <Text className={shared.subHeading}>Per hour</Text>
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

            <Text className={shared.subHeading}>Top exception types</Text>
            {data.topExceptionTypes.length > 0 ? (
              <Table size="small" aria-label="Top exception types">
                <colgroup>
                  <col style={{ width: '30%' }} />
                  <col />
                  <col style={{ width: '90px' }} />
                </colgroup>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Type</TableHeaderCell>
                    <TableHeaderCell>Problem id</TableHeaderCell>
                    <TableHeaderCell className={shared.numeric}>Count</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.topExceptionTypes.map((t, i) => (
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
                      <TableCell className={shared.numeric}>{t.count.toLocaleString()}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            ) : (
              <Text style={{ color: tokens.colorPaletteGreenForeground1 }}>No exceptions recorded in the last 24 hours.</Text>
            )}
          </>
        );
      }}
    </SectionFrame>
  );
}
