import { makeStyles, tokens, Text } from '@fluentui/react-components';

const useStyles = makeStyles({
    row: {
        display: 'grid',
        gridTemplateColumns: 'minmax(140px, 1fr) 3fr auto',
        alignItems: 'center',
        gap: '12px',
        padding: '8px 16px',
    },
    track: {
        backgroundColor: tokens.colorNeutralBackground3,
        borderRadius: tokens.borderRadiusSmall,
        height: '10px',
        overflow: 'hidden',
    },
    fill: {
        height: '100%',
        borderRadius: tokens.borderRadiusSmall,
        backgroundColor: tokens.colorBrandBackground,
    },
    value: {
        color: tokens.colorNeutralForeground2,
        fontVariantNumeric: 'tabular-nums',
        whiteSpace: 'nowrap',
    },
    label: {
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        whiteSpace: 'nowrap',
    },
});

/**
 * A labelled proportional bar. Used for freshness, version adoption and feature adoption so the
 * shape of the install base is readable without pulling in a charting library.
 */
export function BarRow({ label, count, total, valueLabel, color }: {
    label: string;
    count: number;
    total: number;
    valueLabel?: string;
    color?: string;
}) {
    const styles = useStyles();
    const pct = total > 0 ? Math.round((count / total) * 100) : 0;

    return (
        <div className={styles.row}>
            <Text className={styles.label} title={label}>{label}</Text>
            <div className={styles.track}>
                <div
                    className={styles.fill}
                    style={{ width: `${pct}%`, ...(color ? { backgroundColor: color } : {}) }}
                />
            </div>
            <Text className={styles.value}>{valueLabel ?? `${count} (${pct}%)`}</Text>
        </div>
    );
}
