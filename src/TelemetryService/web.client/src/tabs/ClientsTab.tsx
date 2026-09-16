import { useMemo, useState } from 'react';
import {
    Badge,
    Button,
    Dialog,
    DialogActions,
    DialogBody,
    DialogContent,
    DialogSurface,
    DialogTitle,
    Field,
    Input,
    makeStyles,
    Popover,
    PopoverSurface,
    PopoverTrigger,
    Text,
    Textarea,
    tokens,
} from '@fluentui/react-components';
import { Edit20Regular, Search20Regular } from '@fluentui/react-icons';
import type { ClientAnnotationUpdate, ClientSummary } from '../types';
import { formatDate, formatMB, formatNumber, formatRelative } from '../format';
import { Section, Surface } from '../components/layout';
import { SortableTable, type Column } from '../components/SortableTable';

const useStyles = makeStyles({
    toolbar: {
        display: 'flex',
        alignItems: 'center',
        gap: '12px',
        flexWrap: 'wrap',
    },
    search: { minWidth: '260px' },
    count: { color: tokens.colorNeutralForeground3 },
    mono: {
        fontFamily: tokens.fontFamilyMonospace,
        fontSize: tokens.fontSizeBase200,
    },
    stale: { color: tokens.colorPaletteRedForeground1 },
    importList: {
        display: 'flex',
        flexDirection: 'column',
        gap: '4px',
        maxWidth: '280px',
    },
    trigger: { cursor: 'pointer' },
    unnamed: { color: tokens.colorNeutralForeground3 },
    nameCell: {
        display: 'flex',
        alignItems: 'center',
        gap: '6px',
    },
    dialogFields: {
        display: 'flex',
        flexDirection: 'column',
        gap: '14px',
    },
    dialogId: {
        fontFamily: tokens.fontFamilyMonospace,
        fontSize: tokens.fontSizeBase200,
        color: tokens.colorNeutralForeground3,
        wordBreak: 'break-all',
    },
    error: { color: tokens.colorPaletteRedForeground1 },
});

const STALE_AFTER_DAYS = 30;
const MAX_DISPLAY_NAME = 200;
const MAX_NOTES = 2000;

function isStale(generated: string | null): boolean {
    if (!generated) return true;
    const d = new Date(generated);
    if (isNaN(d.getTime())) return true;
    return Date.now() - d.getTime() > STALE_AFTER_DAYS * 24 * 60 * 60 * 1000;
}

export default function ClientsTab({ clients, onSaveAnnotation }: {
    clients: ClientSummary[];
    /** Omitted in read-only contexts; the edit affordance is hidden when absent. */
    onSaveAnnotation?: (anonClientId: string, update: ClientAnnotationUpdate) => Promise<void>;
}) {
    const styles = useStyles();
    const [filter, setFilter] = useState('');
    const [editing, setEditing] = useState<ClientSummary | null>(null);

    const filtered = useMemo(() => {
        const needle = filter.trim().toLowerCase();
        if (!needle) return clients;
        return clients.filter(c =>
            c.anonClientId.toLowerCase().includes(needle) ||
            (c.buildVersionLabel ?? '').toLowerCase().includes(needle) ||
            // Searching by the name a maintainer gave a client is the whole point of recording it.
            (c.annotationDisplayName ?? '').toLowerCase().includes(needle));
    }, [clients, filter]);

    const columns: Column<ClientSummary>[] = [
        {
            key: 'customer',
            header: 'Customer',
            render: c => (
                <div className={styles.nameCell}>
                    {c.annotationDisplayName
                        ? <span title={c.annotationNotes ?? undefined}>{c.annotationDisplayName}</span>
                        : <span className={styles.unnamed}>Not identified</span>}
                    {onSaveAnnotation && (
                        <Button
                            appearance="subtle"
                            size="small"
                            icon={<Edit20Regular />}
                            aria-label="Edit customer name"
                            onClick={() => setEditing(c)}
                        />
                    )}
                </div>
            ),
            sortValue: c => c.annotationDisplayName ?? '',
        },
        {
            key: 'id',
            header: 'Anon client ID',
            render: c => <span className={styles.mono} title={c.anonClientId}>{c.anonClientId.slice(0, 16)}…</span>,
            sortValue: c => c.anonClientId,
        },
        {
            key: 'generated',
            header: 'Last report',
            render: c => (
                <span className={isStale(c.generated) ? styles.stale : undefined} title={formatDate(c.generated)}>
                    {formatRelative(c.generated)}
                </span>
            ),
            sortValue: c => (c.generated ? new Date(c.generated).getTime() : 0),
        },
        {
            key: 'build',
            header: 'Build',
            render: c => c.buildVersionLabel ?? '—',
            sortValue: c => c.buildVersionLabel ?? '',
        },
        {
            key: 'seats',
            header: 'Copilot seats',
            numeric: true,
            render: c => (c.adoptionSuppressed ? 'Too small' : formatNumber(c.copilotLicensedUsers)),
            sortValue: c => c.copilotLicensedUsers ?? -1,
        },
        {
            key: 'adoption',
            header: 'Adoption',
            numeric: true,
            render: c => (c.copilotAdoptionRatePct === null ? '—' : `${c.copilotAdoptionRatePct}%`),
            sortValue: c => c.copilotAdoptionRatePct ?? -1,
        },
        { key: 'rows', header: 'Rows', numeric: true, render: c => formatNumber(c.rows), sortValue: c => c.rows },
        { key: 'size', header: 'Size', numeric: true, render: c => formatMB(c.totalSpaceMB), sortValue: c => c.totalSpaceMB },
        { key: 'tables', header: 'Tables', numeric: true, render: c => formatNumber(c.tableCount), sortValue: c => c.tableCount },
        {
            key: 'ai',
            header: 'AI data points',
            numeric: true,
            render: c => formatNumber(c.dataPointsFromAITotal),
            sortValue: c => c.dataPointsFromAITotal ?? -1,
        },
        {
            key: 'imports',
            header: 'Imports on',
            numeric: true,
            render: c =>
                c.enabledImports.length === 0 ? (
                    '—'
                ) : (
                    <Popover withArrow>
                        <PopoverTrigger disableButtonEnhancement>
                            <Badge appearance="tint" color="brand" className={styles.trigger}>
                                {c.enabledImports.length}
                            </Badge>
                        </PopoverTrigger>
                        <PopoverSurface>
                            <div className={styles.importList}>
                                <Text weight="semibold">Enabled imports</Text>
                                {c.enabledImports.map(i => (
                                    <Text key={i} size={200}>{i}</Text>
                                ))}
                            </div>
                        </PopoverSurface>
                    </Popover>
                ),
            sortValue: c => c.enabledImports.length,
        },
    ];

    return (
        <Section
            title="Reporting clients"
            description={
                'One row per installation. Client IDs are random and contain nothing about the tenant, so an ' +
                'installation can only be identified if its operator gives us their ID — names below are entered ' +
                'by maintainers, never reported by the client.'
            }
        >
            <div className={styles.toolbar}>
                <Input
                    className={styles.search}
                    placeholder="Filter by customer, client ID or build…"
                    value={filter}
                    onChange={(_e, d) => setFilter(d.value)}
                    contentBefore={<Search20Regular />}
                />
                <Text className={styles.count}>
                    {filtered.length === clients.length
                        ? `${formatNumber(clients.length)} clients`
                        : `${formatNumber(filtered.length)} of ${formatNumber(clients.length)} clients`}
                </Text>
            </div>

            <Surface>
                <SortableTable
                    items={filtered}
                    columns={columns}
                    rowKey={c => c.anonClientId}
                    initialSortKey="generated"
                    emptyMessage="No clients match that filter."
                />
            </Surface>

            {onSaveAnnotation && editing && (
                <AnnotationDialog
                    // Keying on the client remounts the dialog when a different row is opened, so its
                    // state initialisers re-run with that row's values. That is what removes the need
                    // to re-seed state from an effect (react-hooks/set-state-in-effect).
                    key={editing.anonClientId}
                    client={editing}
                    onDismiss={() => setEditing(null)}
                    onSave={onSaveAnnotation}
                />
            )}
        </Section>
    );
}

function AnnotationDialog({ client, onDismiss, onSave }: {
    client: ClientSummary;
    onDismiss: () => void;
    onSave: (anonClientId: string, update: ClientAnnotationUpdate) => Promise<void>;
}) {
    const styles = useStyles();
    const [displayName, setDisplayName] = useState(client.annotationDisplayName ?? '');
    const [notes, setNotes] = useState(client.annotationNotes ?? '');
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const anonClientId = client.anonClientId;

    async function save() {
        setSaving(true);
        setError(null);
        try {
            await onSave(anonClientId, {
                displayName: displayName.trim() || null,
                notes: notes.trim() || null,
            });
            onDismiss();
        } catch (e: unknown) {
            setError(e instanceof Error ? e.message : 'Could not save.');
        } finally {
            setSaving(false);
        }
    }

    return (
        <Dialog open modalType="modal" onOpenChange={(_e, d) => { if (!d.open) onDismiss(); }}>
            <DialogSurface>
                <DialogBody>
                    <DialogTitle>Identify this installation</DialogTitle>
                    <DialogContent>
                        <div className={styles.dialogFields}>
                            <Text size={200}>
                                Only fill this in for a customer who has given you their client ID and asked to be
                                recognised. Clearing both fields removes the record.
                            </Text>
                            <div className={styles.dialogId}>{anonClientId}</div>
                            <Field label="Customer name">
                                <Input
                                    value={displayName}
                                    maxLength={MAX_DISPLAY_NAME}
                                    onChange={(_e, d) => setDisplayName(d.value)}
                                />
                            </Field>
                            <Field label="Notes" hint="Who asked, when, and any context worth keeping.">
                                <Textarea
                                    value={notes}
                                    maxLength={MAX_NOTES}
                                    rows={4}
                                    onChange={(_e, d) => setNotes(d.value)}
                                />
                            </Field>
                            {error && <Text className={styles.error}>{error}</Text>}
                        </div>
                    </DialogContent>
                    <DialogActions>
                        <Button appearance="secondary" onClick={onDismiss} disabled={saving}>Cancel</Button>
                        <Button appearance="primary" onClick={() => void save()} disabled={saving}>
                            {saving ? 'Saving…' : 'Save'}
                        </Button>
                    </DialogActions>
                </DialogBody>
            </DialogSurface>
        </Dialog>
    );
}
