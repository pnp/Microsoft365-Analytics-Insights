export function formatNumber(n: number | null | undefined): string {
    if (n === null || n === undefined) return '—';
    return n.toLocaleString();
}

/** Sizes arrive from SQL as megabytes; scale up so large install bases stay readable. */
export function formatMB(mb: number | null | undefined): string {
    if (mb === null || mb === undefined) return '—';
    if (mb >= 1024 * 1024) return `${(mb / 1024 / 1024).toFixed(2)} TB`;
    if (mb >= 1024) return `${(mb / 1024).toFixed(2)} GB`;
    return `${mb.toFixed(2)} MB`;
}

export function formatDate(value: string | null | undefined): string {
    if (!value) return '—';
    const d = new Date(value);
    if (isNaN(d.getTime())) return value;
    return d.toLocaleString();
}

/** Compact "2 hours ago" style label, so staleness is obvious at a glance. */
export function formatRelative(value: string | null | undefined): string {
    if (!value) return 'never';
    const d = new Date(value);
    if (isNaN(d.getTime())) return value;

    const seconds = Math.floor((Date.now() - d.getTime()) / 1000);
    if (seconds < 60) return 'just now';

    const units: [number, string][] = [
        [60, 'minute'],
        [60, 'hour'],
        [24, 'day'],
        [30, 'month'],
        [12, 'year'],
    ];

    let value_ = seconds;
    let label = 'second';
    for (const [factor, name] of units) {
        if (value_ < factor) break;
        value_ = Math.floor(value_ / factor);
        label = name;
    }

    return `${value_} ${label}${value_ === 1 ? '' : 's'} ago`;
}

export function formatPercent(part: number, whole: number): string {
    if (!whole) return '0%';
    return `${Math.round((part / whole) * 100)}%`;
}
