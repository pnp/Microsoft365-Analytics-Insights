import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { DashboardStats } from '../types';
import { formatNumber, formatPercent, formatRelative } from '../format';
import { Section, StatCard, StatCardGrid, Surface, EmptyState } from '../components/layout';
import { BarRow } from '../components/BarRow';

const useStyles = makeStyles({
    bars: { paddingBlock: '6px' },
    note: {
        color: tokens.colorNeutralForeground3,
        paddingInline: '16px',
        paddingBottom: '10px',
    },
});

export default function AdoptionTab({ stats }: { stats: DashboardStats }) {
    const styles = useStyles();

    const versions = stats.versions;
    const features = stats.importFeatures;

    // "Unknown" is a real answer here (older builds don't report a label), but it shouldn't be
    // presented as the most popular version, so call out the known-version leader separately.
    const knownVersions = versions.filter(v => v.buildVersionLabel !== '(unknown)');
    const topVersion = knownVersions[0];

    return (
        <>
            <Section title="Build adoption" description="Which builds the reporting installations are running.">
                <StatCardGrid>
                    <StatCard label="Distinct builds" value={formatNumber(knownVersions.length)} />
                    {topVersion && (
                        <StatCard
                            label="Most common build"
                            value={topVersion.buildVersionLabel}
                            hint={`${topVersion.clientCount} of ${stats.clientCount} clients`}
                            small
                        />
                    )}
                </StatCardGrid>
            </Section>

            <Section title="Clients per build">
                <Surface>
                    {versions.length === 0 ? (
                        <EmptyState message="No build information reported yet." />
                    ) : (
                        <>
                            <div className={styles.bars}>
                                {versions.map(v => (
                                    <BarRow
                                        key={v.buildVersionLabel}
                                        label={v.buildVersionLabel}
                                        count={v.clientCount}
                                        total={stats.clientCount}
                                        valueLabel={`${v.clientCount} · last seen ${formatRelative(v.lastSeen)}`}
                                    />
                                ))}
                            </div>
                            <Text as="p" className={styles.note} size={200}>
                                Builds older than the version-reporting change report as “(unknown)”.
                            </Text>
                        </>
                    )}
                </Surface>
            </Section>

            <Section
                title="Import feature adoption"
                description="How many installations have each import switched on, out of those reporting that toggle."
            >
                <Surface>
                    {features.length === 0 ? (
                        <EmptyState message="No import configuration reported yet." />
                    ) : (
                        <>
                            <div className={styles.bars}>
                                {features.map(f => (
                                    <BarRow
                                        key={f.name}
                                        label={f.name}
                                        count={f.enabledCount}
                                        total={f.reportingClients}
                                        valueLabel={`${f.enabledCount}/${f.reportingClients} (${formatPercent(f.enabledCount, f.reportingClients)})`}
                                    />
                                ))}
                            </div>
                            <Text as="p" className={styles.note} size={200}>
                                Percentages are of clients reporting that toggle, not of all clients — a newly
                                added import is absent from older builds rather than reported as off.
                            </Text>
                        </>
                    )}
                </Surface>
            </Section>
        </>
    );
}
