import { useCallback, useEffect, useState, type ReactNode } from 'react';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Subtitle2,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import Spinner from '../Spinner';
import { formatDateParts, formatNumber, translateActive, useT, type TFunction } from '../../i18n';
import { health as enHealth } from '../../i18n/catalog/en/health';
import type { TranslationKey } from '../../i18n';
import type { ComponentHealthRow, DataOverviewSection, HealthSectionBase, HealthStatusName, HourCount } from '../../types/health';

export type BadgeColor = 'success' | 'warning' | 'danger' | 'informative' | 'subtle';

// A full activity import cycle should complete at least this often (see HEALTH-MONITORING-DESIGN.md).
export const CYCLE_SLA_HOURS = 24;
export const AUTO_REFRESH_MS = 60_000;

export const BLOB_CHECKPOINT_REASON_KEYS: Record<string, TranslationKey> = {
  'blobCheckpoint.healthy': 'health.reason.blobCheckpointHealthy',
  'blobCheckpoint.notConfigured': 'health.reason.blobCheckpointNotConfigured',
  'blobCheckpoint.transport': 'health.reason.blobCheckpointTransport',
  'blobCheckpoint.storageFirewall': 'health.reason.blobCheckpointStorageFirewall',
  'blobCheckpoint.permissionMismatch': 'health.reason.blobCheckpointPermissionMismatch',
  'blobCheckpoint.authenticationFailed': 'health.reason.blobCheckpointAuthenticationFailed',
  'blobCheckpoint.keyAuthDisabled': 'health.reason.blobCheckpointKeyAuthDisabled',
  'blobCheckpoint.storageRejected': 'health.reason.blobCheckpointStorageRejected',
};

export const HEALTH_COMPONENT_LABEL_KEYS: Record<string, TranslationKey> = {
  Credential: 'health.component.Credential',
  ServiceBus: 'health.component.ServiceBus',
  BlobCheckpoint: 'health.component.BlobCheckpoint',
};

/**
 * The importer's telemetry detail for a blob checkpoint it could not open durably
 * (ProcessedBlobStoreFactory.TrackDegradedHealth), with the classifier's operator message inside.
 *
 * The Components table translates these from the event's ReasonKey. The Overview and the section
 * roll-ups never see that key: HealthRollup folds each component into "{component} is degraded:
 * {detail}", so the detail text is all they have. Recognising the failure by the sentence that names
 * it is what stops every state but the storage firewall reaching a Spanish reader in English - and
 * it also covers events written before the importer sent a ReasonKey. serverAuthoredText.test.ts
 * rebuilds each detail from the C# and checks it lands on the right key.
 */
const BLOB_CHECKPOINT_UNAVAILABLE = /^Azure Table checkpoint unavailable: ([\s\S]+) Using non-durable in-memory checkpoint \(lost on restart; durable cross-cycle metadata recovery unavailable\)\. See importer error log\.$/;

export const BLOB_CHECKPOINT_FAILURE_SENTENCES: ReadonlyArray<{ reasonKey: string; sentence: RegExp }> = [
  { reasonKey: 'blobCheckpoint.transport', sentence: /^The Table checkpoint request failed before Azure Storage returned a service error\./ },
  { reasonKey: 'blobCheckpoint.storageFirewall', sentence: /^Storage firewall\/network rules rejected the Table checkpoint request \(HTTP (\d+) ([^)]+)\)\./ },
  { reasonKey: 'blobCheckpoint.permissionMismatch', sentence: /^The runtime identity reached Table storage but does not have the required data-plane role \(HTTP (\d+) ([^)]+)\)\./ },
  { reasonKey: 'blobCheckpoint.authenticationFailed', sentence: /^Azure Storage rejected the checkpoint credential \(HTTP (\d+) ([^)]+)\)\./ },
  { reasonKey: 'blobCheckpoint.keyAuthDisabled', sentence: /^The storage account has shared-key authentication disabled \(HTTP (\d+) ([^)]+)\)\./ },
  { reasonKey: 'blobCheckpoint.storageRejected', sentence: /^Azure Storage rejected the Table checkpoint request \(HTTP (\d+) ([^)]+)\)\./ },
];

/**
 * Matches text the server wrote against the English catalog value that mirrors it, and returns each
 * `{placeholder}`'s value - or null when the text is something else.
 *
 * For these keys the English catalog value IS the server's sentence, word for word
 * (serverAuthoredText.test.ts pins the two together), so recognising the server's English and
 * translating it are one contract rather than two copies of the same sentence that can drift apart.
 */
function matchServerTemplate(template: string, text: string): Record<string, string> | null {
  const names: string[] = [];
  const pattern = template
    .split(/(\{\w+\})/)
    .map((part) => {
      const placeholder = /^\{(\w+)\}$/.exec(part);
      if (!placeholder) return part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      names.push(placeholder[1]);
      return '(.+?)';
    })
    .join('');
  const match = new RegExp(`^${pattern}$`).exec(text);
  return match ? Object.fromEntries(names.map((name, i) => [name, match[i + 1]])) : null;
}

/** A whole number the server printed invariantly ("1234"), re-printed in the portal language. */
function serverNumber(value: string): string {
  return /^\d+$/.test(value) ? formatNumber(Number(value)) : value;
}

function durationPart(value: number, singularKey: TranslationKey, pluralKey: TranslationKey, t: TFunction): string {
  return t(value === 1 ? singularKey : pluralKey, { count: formatNumber(value) });
}

export function formatHealthDuration(totalSeconds: number | null | undefined, fallback: string | null | undefined, t: TFunction): string {
  if (totalSeconds === null || totalSeconds === undefined || !Number.isFinite(totalSeconds) || totalSeconds < 0) {
    return fallback ?? '';
  }

  const wholeSeconds = Math.round(totalSeconds);
  const days = Math.floor(wholeSeconds / 86400);
  const hours = Math.floor((wholeSeconds % 86400) / 3600);
  const minutes = Math.floor((wholeSeconds % 3600) / 60);
  const seconds = wholeSeconds % 60;
  const parts = [
    ...(days > 0 ? [durationPart(days, 'health.duration.day', 'health.duration.days', t)] : []),
    durationPart(hours, 'health.duration.hour', 'health.duration.hours', t),
    durationPart(minutes, 'health.duration.minute', 'health.duration.minutes', t),
    durationPart(seconds, 'health.duration.second', 'health.duration.seconds', t),
  ];

  return t('health.duration.parts', { parts: parts.join(', ') });
}

export function translateHealthComponentName(component: string | null | undefined, t: TFunction): string {
  if (!component) return '';
  const key = HEALTH_COMPONENT_LABEL_KEYS[component];
  return key ? t(key) : component;
}

// --- Time / format helpers ---

export function minutesAgo(iso: string | null): number | null {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return null;
  return (Date.now() - t) / 60000;
}

export function howLongAgo(iso: string | null, t: TFunction = translateActive): string {
  const m = minutesAgo(iso);
  if (m === null) return t('health.time.never');
  if (m < 1) return t('health.time.justNow');
  if (m < 60) {
    const minutes = formatNumber(Math.round(m));
    return t('health.time.minutesAgo', { minutes });
  }
  if (m < 60 * 24) {
    const hours = formatNumber(Number((m / 60).toFixed(1)));
    return t('health.time.hoursAgo', { hours });
  }
  const days = formatNumber(Number((m / 60 / 24).toFixed(1)));
  return t('health.time.daysAgo', { days });
}

export function freshnessColor(iso: string | null, greenHours: number, amberHours: number): BadgeColor {
  const m = minutesAgo(iso);
  if (m === null) return 'subtle';
  const h = m / 60;
  if (h <= greenHours) return 'success';
  if (h <= amberHours) return 'warning';
  return 'danger';
}

export function statusColor(status: string | null): BadgeColor {
  switch ((status ?? '').toLowerCase()) {
    case 'healthy':
      return 'success';
    case 'degraded':
      return 'warning';
    case 'unhealthy':
      return 'danger';
    default:
      return 'subtle';
  }
}

export function overallColor(status: string | null): BadgeColor {
  switch ((status ?? '').toLowerCase()) {
    case 'healthy':
      return 'success';
    case 'degraded':
      return 'warning';
    case 'unhealthy':
      return 'danger';
    default:
      return 'informative';
  }
}

export function formatUtc(iso: string | null): string {
  if (!iso) return '-';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '-';
  return `${formatDateParts(d, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
    timeZone: 'UTC',
  })} UTC`;
}

export function formatSize(mb: number): string {
  if (!mb || mb <= 0) return '-';
  if (mb >= 1024) return `${formatNumber(Number((mb / 1024).toFixed(1)))} GB`;
  return `${formatNumber(mb)} MB`;
}

/** null (couldn't compute, e.g. the bounded scan timed out) renders as "-"; otherwise a localised count. */
export function formatCount(n: number | null): string {
  return n === null || n === undefined ? '-' : formatNumber(n);
}

function isDataOverviewSection(section: HealthSectionBase): section is DataOverviewSection {
  return Object.prototype.hasOwnProperty.call(section, 'dataError')
    && Object.prototype.hasOwnProperty.call(section, 'countsError')
    && Object.prototype.hasOwnProperty.call(section, 'recentVolumeError');
}

export function translateHealthComponentDetailText(detail: string | null | undefined, t: TFunction): string {
  if (!detail) return '';

  if (detail === enHealth['health.reason.runtimeCertificateExpired']) {
    return t('health.reason.runtimeCertificateExpired');
  }

  const certificateValid = /^Runtime certificate '([^']+)' valid; expires ([0-9-]+)\.$/.exec(detail);
  if (certificateValid) {
    return t('health.reason.runtimeCertificateValid', {
      certificateName: certificateValid[1],
      expiryDate: certificateValid[2],
    });
  }

  if (detail === enHealth['health.reason.clientSecretAuthValid']) {
    return t('health.reason.clientSecretAuthValid');
  }

  const certificateCheckPrefix = enHealth['health.reason.runtimeCertificateCheckFailed'].replace('{error}', '');
  if (detail.startsWith(certificateCheckPrefix)) {
    return t('health.reason.runtimeCertificateCheckFailed', {
      error: detail.slice(certificateCheckPrefix.length),
    });
  }

  const queueDepthPrefix = enHealth['health.reason.teamsCallsQueueDepthFailed'].replace('{error}', '');
  if (detail.startsWith(queueDepthPrefix)) {
    const diagnostic = detail.slice(queueDepthPrefix.length);
    const networkHint = enHealth['health.reason.teamsCallsQueueDepthFailedNetworkBlock']
      .replace(queueDepthPrefix, '')
      .replace('{error}', '');
    if (diagnostic.endsWith(networkHint)) {
      return t('health.reason.teamsCallsQueueDepthFailedNetworkBlock', {
        error: diagnostic.slice(0, -networkHint.length),
      });
    }

    return t('health.reason.teamsCallsQueueDepthFailed', { error: diagnostic });
  }

  const blobCheckpointFirewall = /^Azure Table checkpoint unavailable: Storage firewall\/network rules rejected the Table checkpoint request \(HTTP ([0-9]+) ([^)]+)\)\. On a public install, set the storage account to 'Enabled from all networks'; IP allow-list rules do not apply to requests from an App Service in the same Azure region as the storage account\. Anything stricter needs App Service VNet integration plus a Microsoft\.Storage service endpoint, or the private-endpoint deployment\. Using non-durable in-memory checkpoint \(lost on restart; durable cross-cycle metadata recovery unavailable\)\. See importer error log\.$/.exec(detail);
  if (blobCheckpointFirewall) {
    return t('health.reason.blobCheckpointStorageFirewall', {
      status: blobCheckpointFirewall[1],
      errorCode: blobCheckpointFirewall[2],
    });
  }

  if (detail === enHealth['health.reason.blobCheckpointHealthy']) return t('health.reason.blobCheckpointHealthy');
  if (detail === enHealth['health.reason.blobCheckpointNotConfigured']) return t('health.reason.blobCheckpointNotConfigured');

  const blobCheckpointUnavailable = BLOB_CHECKPOINT_UNAVAILABLE.exec(detail);
  if (blobCheckpointUnavailable) {
    for (const { reasonKey, sentence } of BLOB_CHECKPOINT_FAILURE_SENTENCES) {
      const failure = sentence.exec(blobCheckpointUnavailable[1]);
      if (failure) {
        return t(BLOB_CHECKPOINT_REASON_KEYS[reasonKey], {
          status: failure[1] ?? '-',
          errorCode: failure[2] ?? '-',
        });
      }
    }
  }

  const queueDepth = matchServerTemplate(enHealth['health.reason.teamsCallsQueueDepth'], detail);
  if (queueDepth) {
    return t('health.reason.teamsCallsQueueDepth', {
      queue: queueDepth.queue,
      active: serverNumber(queueDepth.active),
      deadLettered: serverNumber(queueDepth.deadLettered),
    });
  }

  return detail;
}

export function translateHealthComponentDetail(component: ComponentHealthRow, t: TFunction): string {
  const blobCheckpointKey = component.reasonKey ? BLOB_CHECKPOINT_REASON_KEYS[component.reasonKey] : null;
  if (blobCheckpointKey) {
    return t(blobCheckpointKey, {
      status: component.httpStatus ? formatNumber(component.httpStatus) : '-',
      errorCode: component.errorCode ?? '-',
    });
  }

  return translateHealthComponentDetailText(component.detail, t);
}

export function translateHealthReasonText(reason: string, t: TFunction): string {
  if (reason === enHealth['health.reason.allChecksPassing']) return t('health.reason.allChecksPassing');
  if (reason === enHealth['health.reason.databaseReachableOpenDataTab']) {
    return t('health.reason.databaseReachableOpenDataTab');
  }
  if (reason === enHealth['health.reason.applicationInsightsNotConfigured']) return t('health.reason.applicationInsightsNotConfigured');
  if (reason === enHealth['health.reason.telemetryQueriesFailing']) {
    return t('health.reason.telemetryQueriesFailing');
  }
  if (reason === enHealth['health.reason.someConfigurationCouldntBeRead']) return t('health.reason.someConfigurationCouldntBeRead');

  const databaseErrorPrefix = enHealth['health.reason.databaseQueryFailed'].replace('{error}', '');
  if (reason.startsWith(databaseErrorPrefix)) {
    return t('health.reason.databaseQueryFailed', { error: reason.slice(databaseErrorPrefix.length) });
  }

  // HealthRollup's own sentences. Numbers arrive printed invariantly and are re-printed here; job
  // names are the importers' own identifiers and are left as they are.
  const schemaBehind = matchServerTemplate(enHealth['health.reason.schemaBehind'], reason);
  if (schemaBehind) return t('health.reason.schemaBehind', { count: serverNumber(schemaBehind.count) });

  const noCycle = matchServerTemplate(enHealth['health.reason.noCompletedImportCycle'], reason);
  if (noCycle) return t('health.reason.noCompletedImportCycle', { job: noCycle.job });

  const cycleOverdue = matchServerTemplate(enHealth['health.reason.importCycleOverdue'], reason);
  if (cycleOverdue) {
    return t('health.reason.importCycleOverdue', {
      job: cycleOverdue.job,
      hours: serverNumber(cycleOverdue.hours),
      sla: serverNumber(cycleOverdue.sla),
    });
  }

  const cycleLate = matchServerTemplate(enHealth['health.reason.importCycleLate'], reason);
  if (cycleLate) {
    return t('health.reason.importCycleLate', {
      job: cycleLate.job,
      hours: serverNumber(cycleLate.hours),
      sla: serverNumber(cycleLate.sla),
    });
  }

  const sqlCapacity = matchServerTemplate(enHealth['health.reason.sqlCapacityExceptions'], reason);
  if (sqlCapacity) return t('health.reason.sqlCapacityExceptions', { count: serverNumber(sqlCapacity.count) });

  if (reason === enHealth['health.reason.teamsCallsWebhookMissing']) return t('health.reason.teamsCallsWebhookMissing');
  if (reason === enHealth['health.reason.teamsCallsWebhookError']) return t('health.reason.teamsCallsWebhookError');

  const componentMatch = /^(.+) is (unhealthy|degraded): (.+)$/.exec(reason);
  if (componentMatch) {
    return t(
      componentMatch[2] === 'unhealthy'
        ? 'health.reason.componentUnhealthy'
        : 'health.reason.componentDegraded',
      {
        component: translateHealthComponentName(componentMatch[1], t),
        detail: translateHealthComponentDetailText(componentMatch[3], t),
      },
    );
  }

  return reason;
}

export function healthReasonTexts(section: HealthSectionBase, t: TFunction): string[] {
  if (isDataOverviewSection(section)) {
    if (section.dataError) {
      return [t('health.reason.databaseQueryFailed', { error: section.dataError })];
    }

    const reasons: string[] = [];
    if (section.countsError) {
      reasons.push(t('health.reason.approximateCountsUnavailable', { error: section.countsError }));
    }
    if (section.recentVolumeError) {
      reasons.push(t('health.reason.recentVolumeScanDidntComplete', { error: section.recentVolumeError }));
    }
    if (section.copilotUsageReportsIdentitiesConcealed) {
      reasons.push(t('health.reason.copilotUsageIdentitiesConcealed'));
    }
    for (const copilotError of section.copilotUsageReportErrors ?? []) {
      reasons.push(t('health.reason.graphCopilotUsageReportImportFailed', { error: copilotError }));
    }

    if (reasons.length > 0) return reasons;
  }

  return (section.reasons ?? []).map((reason) => translateHealthReasonText(reason, t));
}

// KQL summarize-by-bin omits empty hours, so pad to a full 24-bar series for a readable sparkline.
export function buildHourBuckets(perHour: HourCount[]): { hourUtc: string; count: number }[] {
  const byHour = new Map<number, number>();
  for (const h of perHour) {
    if (!h.hourUtc) continue;
    const t = new Date(h.hourUtc);
    if (Number.isNaN(t.getTime())) continue;
    t.setUTCMinutes(0, 0, 0);
    byHour.set(t.getTime(), (byHour.get(t.getTime()) ?? 0) + h.count);
  }
  const now = new Date();
  now.setUTCMinutes(0, 0, 0);
  const buckets: { hourUtc: string; count: number }[] = [];
  for (let i = 23; i >= 0; i--) {
    const d = new Date(now.getTime() - i * 3_600_000);
    buckets.push({ hourUtc: d.toISOString(), count: byHour.get(d.getTime()) ?? 0 });
  }
  return buckets;
}

// --- Per-section data hook ---

export interface SectionState<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
  refreshing: boolean;
  reload: () => void;
}

/**
 * Fetches one Health sub-section. Loads once when the panel first mounts (lazy: a panel only mounts
 * when its tab is first opened) and auto-refreshes every 60s while `active` (the tab is on top) so an
 * open "green board" stays current without every tab polling in the background.
 */
export function useHealthSection<T>(fetcher: () => Promise<T>, active: boolean): SectionState<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  const load = useCallback(async () => {
    setRefreshing(true);
    try {
      const d = await fetcher();
      setData(d);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : translateActive('health.section.failedToLoad'));
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [fetcher]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!active) return;
    const id = window.setInterval(() => void load(), AUTO_REFRESH_MS);
    return () => window.clearInterval(id);
  }, [active, load]);

  return { data, error, loading, refreshing, reload: () => void load() };
}

// --- Shared presentational bits ---

const useSharedStyles = makeStyles({
  toolbar: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
    marginBottom: '4px',
  },
  spacer: { flex: 1 },
  desc: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginBottom: '8px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  subHeading: {
    display: 'block',
    marginTop: '12px',
    marginBottom: '4px',
    fontWeight: tokens.fontWeightSemibold,
  },
  reasons: {
    marginTop: '4px',
    marginBottom: '8px',
    paddingLeft: '20px',
  },
  loading: { textAlign: 'center', padding: '32px' },
  /**
   * Fluent's Table is `table-layout: fixed; width: 100%`, so a long unbroken token (a fully
   * qualified type name, an endpoint, a URL) overflows its cell and paints over the next column
   * instead of widening it. `anywhere` rather than `break-word` because it also reduces the
   * element's intrinsic min-content width, so it keeps working in an auto-layout or flex context.
   */
  breakAnywhere: {
    overflowWrap: 'anywhere',
  },
  /** Numeric column: right-aligned and never wrapped. */
  numeric: {
    textAlign: 'right',
    whiteSpace: 'nowrap',
  },
});

export function useHealthStyles() {
  return useSharedStyles();
}

export function HealthStatusBadge({ status }: { status: HealthStatusName | null }) {
  const t = useT();
  return (
    <Badge appearance="filled" color={statusColor(status)}>
      {healthStatusText(status, t)}
    </Badge>
  );
}

export function healthStatusText(status: string | null, t: TFunction): string {
  switch ((status ?? '').toLowerCase()) {
    case 'healthy':
      return t('health.status.healthy');
    case 'degraded':
      return t('health.status.degraded');
    case 'unhealthy':
      return t('health.status.unhealthy');
    default:
      return t('health.status.unknown');
  }
}

export function SectionReasons({ reasons }: { reasons: string[] }) {
  const styles = useSharedStyles();
  if (!reasons || reasons.length === 0) return null;
  return (
    <ul className={styles.reasons}>
      {reasons.map((r, i) => (
        <li key={i}>
          <Text size={200}>{r}</Text>
        </li>
      ))}
    </ul>
  );
}

/**
 * Frame shared by the five detail sub-sections: a toolbar (own status badge + refresh + loaded-at),
 * an optional description, this section's own reasons, and loading/error handling. The body is only
 * rendered once data is present.
 */
export function SectionFrame<T extends HealthSectionBase>({
  title,
  description,
  state,
  children,
}: {
  title: string;
  description?: ReactNode;
  state: SectionState<T>;
  children: (data: T) => ReactNode;
}) {
  const styles = useSharedStyles();
  const t = useT();
  const { data, loading, error, refreshing, reload } = state;

  return (
    <div>
      <div className={styles.toolbar}>
        <Subtitle2>{title}</Subtitle2>
        {data && <HealthStatusBadge status={data.status} />}
        <span className={styles.spacer} />
        {data && (
          <Text size={200} className={styles.muted}>
            {t('health.section.loaded', { when: formatUtc(data.loadedAtUtc) })}
          </Text>
        )}
        <Button size="small" appearance="secondary" disabled={refreshing} onClick={reload}>
          {refreshing ? t('health.action.refreshing') : t('health.action.refresh')}
        </Button>
      </div>

      {description && <Text className={styles.desc}>{description}</Text>}
      {data && <SectionReasons reasons={healthReasonTexts(data, t)} />}

      {loading && !data ? (
        <div className={styles.loading}>
          <Spinner size={60} label={t('health.section.loading', { title: title.toLowerCase() })} />
        </div>
      ) : error && !data ? (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      ) : data ? (
        children(data)
      ) : null}
    </div>
  );
}
