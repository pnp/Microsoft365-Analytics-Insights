import { makeStyles, tokens, Text, MessageBar, MessageBarBody } from '@fluentui/react-components';
import type { DashboardStats } from '../types';
import { formatNumber, formatPercent } from '../format';
import { Section, StatCard, StatCardGrid, Surface, EmptyState } from '../components/layout';
import { BarRow } from '../components/BarRow';

const useStyles = makeStyles({
    bars: { paddingBlock: '6px' },
    note: {
        color: tokens.colorNeutralForeground3,
        paddingInline: '16px',
        paddingBottom: '10px',
    },
    caveat: { marginBottom: '20px' },
    table: {
        width: '100%',
        borderCollapse: 'collapse',
    },
    th: {
        textAlign: 'left',
        padding: '10px 16px',
        borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
        color: tokens.colorNeutralForeground3,
        fontSize: tokens.fontSizeBase200,
        textTransform: 'uppercase',
        letterSpacing: '0.04em',
        whiteSpace: 'nowrap',
    },
    td: {
        padding: '9px 16px',
        borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    },
    num: {
        textAlign: 'right',
        fontVariantNumeric: 'tabular-nums',
        whiteSpace: 'nowrap',
    },
});

/**
 * Copilot and licence adoption across the reporting install base.
 *
 * Deliberately a separate tab from "Adoption", which is about which BUILDS and IMPORTS installations
 * run. Folding the two together would make both ambiguous.
 */
export default function CopilotTab({ stats }: { stats: DashboardStats }) {
    const styles = useStyles();
    const a = stats.adoption;

    if (!a || a.clientsReporting === 0) {
        return (
            <Section title="Copilot adoption">
                <Surface>
                    <EmptyState
                        message={
                            'No installation has reported adoption metrics yet. They are only sent by builds that ' +
                            'support them, from tenants that import user metadata plus at least one Copilot source, ' +
                            'and they are recalculated weekly rather than daily.'
                        }
                    />
                </Surface>
            </Section>
        );
    }

    return (
        <>
            <MessageBar intent="info" className={styles.caveat}>
                <MessageBarBody>
                    Figures are rounded into bands by each installation before being sent, and tenants with
                    fewer than 25 Copilot seats are not reported at all — so totals are approximate by
                    design and small customers are deliberately absent. Percentages below are the median of
                    each installation's own rate, not a rate recalculated from the totals, so one very large
                    tenant cannot dominate them.
                </MessageBarBody>
            </MessageBar>

            <Section
                title="Copilot adoption"
                description="Across installations that report adoption metrics, not across all installations."
            >
                <StatCardGrid>
                    <StatCard
                        label="Reporting installs"
                        value={formatNumber(a.clientsReporting)}
                        hint={`${formatPercent(a.clientsReporting, stats.clientCount)} of all clients`}
                    />
                    <StatCard
                        label="Median adoption rate"
                        value={`${a.medianAdoptionRatePct}%`}
                        hint={`quartiles ${a.lowerQuartileAdoptionRatePct}% – ${a.upperQuartileAdoptionRatePct}%`}
                    />
                    <StatCard label="Median habit rate" value={`${a.medianHabitRatePct}%`} />
                    <StatCard
                        label="Median seats per install"
                        value={formatNumber(a.medianLicensedUsersPerClient)}
                        hint={`median ${formatNumber(a.medianActiveUsersPerClient)} active`}
                    />
                </StatCardGrid>
            </Section>

            <Section title="Scale and coverage">
                <StatCardGrid>
                    <StatCard label="Copilot seats seen" value={formatNumber(a.totalLicensedUsers)} small />
                    <StatCard label="Active Copilot users seen" value={formatNumber(a.totalActiveUsers)} small />
                    <StatCard
                        label="Installs with custom agents"
                        value={formatNumber(a.clientsWithCustomAgents)}
                        hint={`of ${a.clientsWithCopilotFigures} with figures`}
                        small
                    />
                    <StatCard
                        label="Suppressed as too small"
                        value={formatNumber(a.clientsSuppressed)}
                        hint="under 25 Copilot seats"
                        small
                    />
                </StatCardGrid>
            </Section>

            <Section
                title="Copilot data sources in use"
                description="Which inputs reporting installations actually have, which decides how complete their figures can be."
            >
                <Surface>
                    {a.dataSources.length === 0 ? (
                        <EmptyState message="No data-source information reported yet." />
                    ) : (
                        <div className={styles.bars}>
                            {a.dataSources.map(s => (
                                <BarRow
                                    key={s.name}
                                    label={s.name}
                                    count={s.enabledCount}
                                    total={s.reportingClients}
                                    valueLabel={`${s.enabledCount}/${s.reportingClients} (${formatPercent(s.enabledCount, s.reportingClients)})`}
                                />
                            ))}
                        </div>
                    )}
                </Surface>
            </Section>

            <Section
                title="Licence SKUs in the wild"
                description="How widely each Microsoft SKU is held across reporting installations."
            >
                <Surface>
                    {a.skus.length === 0 ? (
                        <EmptyState message="No licence information reported yet." />
                    ) : (
                        <>
                            <table className={styles.table}>
                                <thead>
                                    <tr>
                                        <th className={styles.th}>SKU</th>
                                        <th className={`${styles.th} ${styles.num}`}>Installs</th>
                                        <th className={`${styles.th} ${styles.num}`}>Assignments</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {a.skus.map(s => (
                                        <tr key={s.skuPartNumber}>
                                            <td className={styles.td}>{s.skuPartNumber}</td>
                                            <td className={`${styles.td} ${styles.num}`}>{formatNumber(s.clientCount)}</td>
                                            <td className={`${styles.td} ${styles.num}`}>{formatNumber(s.assignedUsers)}</td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                            <Text as="p" className={styles.note} size={200}>
                                “(unlisted)” groups SKUs Microsoft does not publish — typically reseller bundles —
                                which are never named. “(other)” groups SKUs held by too few people at an
                                installation to report separately. Assignments count licences, not people: one
                                person holding two SKUs appears in both rows.
                            </Text>
                        </>
                    )}
                </Surface>
            </Section>

            <Section
                title="Measurement coverage by workload"
                description="Whether each workload could actually be measured, which is the difference between real inactivity and missing data."
            >
                <Surface>
                    {a.coverage.length === 0 ? (
                        <EmptyState message="No coverage information reported yet." />
                    ) : (
                        <table className={styles.table}>
                            <thead>
                                <tr>
                                    <th className={styles.th}>Workload</th>
                                    <th className={styles.th}>Status</th>
                                    <th className={`${styles.th} ${styles.num}`}>Installs</th>
                                </tr>
                            </thead>
                            <tbody>
                                {a.coverage.map(c => (
                                    <tr key={`${c.workload}/${c.status}`}>
                                        <td className={styles.td}>{c.workload}</td>
                                        <td className={styles.td}>{c.status}</td>
                                        <td className={`${styles.td} ${styles.num}`}>{formatNumber(c.clientCount)}</td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    )}
                </Surface>
            </Section>
        </>
    );
}
