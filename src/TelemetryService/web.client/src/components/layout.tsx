import { makeStyles, tokens, Text, Title3 } from '@fluentui/react-components';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
    cardGrid: {
        display: 'grid',
        gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
        gap: '12px',
    },
    statCard: {
        backgroundColor: tokens.colorNeutralBackground1,
        border: `1px solid ${tokens.colorNeutralStroke2}`,
        borderRadius: tokens.borderRadiusMedium,
        padding: '14px 16px',
        display: 'flex',
        flexDirection: 'column',
        gap: '4px',
        minWidth: 0,
    },
    statLabel: {
        color: tokens.colorNeutralForeground3,
        textTransform: 'uppercase',
        letterSpacing: '0.04em',
        fontSize: tokens.fontSizeBase200,
        fontWeight: tokens.fontWeightSemibold,
    },
    statValue: {
        fontSize: tokens.fontSizeHero700,
        fontWeight: tokens.fontWeightSemibold,
        lineHeight: '1.15',
        wordBreak: 'break-word',
    },
    statValueSmall: {
        fontSize: tokens.fontSizeBase500,
        fontWeight: tokens.fontWeightSemibold,
        lineHeight: '1.25',
        wordBreak: 'break-word',
    },
    statHint: {
        color: tokens.colorNeutralForeground3,
        fontSize: tokens.fontSizeBase200,
    },
    section: {
        display: 'flex',
        flexDirection: 'column',
        gap: '10px',
        marginBottom: '28px',
    },
    sectionHead: {
        display: 'flex',
        flexDirection: 'column',
        gap: '2px',
    },
    sectionDesc: {
        color: tokens.colorNeutralForeground3,
    },
    surface: {
        backgroundColor: tokens.colorNeutralBackground1,
        border: `1px solid ${tokens.colorNeutralStroke2}`,
        borderRadius: tokens.borderRadiusMedium,
        overflowX: 'auto',
    },
    empty: {
        padding: '20px',
        color: tokens.colorNeutralForeground3,
    },
});

export function StatCard({ label, value, hint, small }: {
    label: string;
    value: string;
    hint?: string;
    /** Use for long values (dates) that would otherwise wrap badly at hero size. */
    small?: boolean;
}) {
    const styles = useStyles();
    return (
        <div className={styles.statCard}>
            <span className={styles.statLabel}>{label}</span>
            <span className={small ? styles.statValueSmall : styles.statValue}>{value}</span>
            {hint && <span className={styles.statHint}>{hint}</span>}
        </div>
    );
}

export function StatCardGrid({ children }: { children: ReactNode }) {
    const styles = useStyles();
    return <div className={styles.cardGrid}>{children}</div>;
}

export function Section({ title, description, children }: {
    title: string;
    description?: string;
    children: ReactNode;
}) {
    const styles = useStyles();
    return (
        <section className={styles.section}>
            <div className={styles.sectionHead}>
                <Title3>{title}</Title3>
                {description && <Text className={styles.sectionDesc}>{description}</Text>}
            </div>
            {children}
        </section>
    );
}

export function Surface({ children }: { children: ReactNode }) {
    const styles = useStyles();
    return <div className={styles.surface}>{children}</div>;
}

export function EmptyState({ message }: { message: string }) {
    const styles = useStyles();
    return <div className={styles.empty}>{message}</div>;
}
