import { useEffect, useRef, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  MessageBar,
  MessageBarBody,
  Subtitle2,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { PlugConnected20Regular } from '@fluentui/react-icons';
import { fetchLiveLicences } from '../../../api/userLookupApi';
import type { LiveLicenceComparison, LiveLicenceRow, UserDiagnostics } from '../../../types/userDiagnostics';
import Spinner from '../../Spinner';
import { formatNumber, useT, type TFunction, type TranslationKey } from '../../../i18n';
import { classificationReason, formatUtc } from './diagnosticsText';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  facts: {
    display: 'grid',
    gridTemplateColumns: 'minmax(200px, max-content) 1fr',
    columnGap: '24px',
    rowGap: '6px',
    alignItems: 'baseline',
  },
  label: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground2 },
  muted: { color: tokens.colorNeutralForeground3 },
  tableWrap: { overflowX: 'auto' },
  mono: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, wordBreak: 'break-all' },
  detailList: { display: 'flex', flexDirection: 'column', gap: '2px' },
  actions: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
});

const STAMP_STATE_KEYS: Record<string, TranslationKey> = {
  notRecorded: 'userDiagnostics.freshness.notRecorded',
  storageNotConfigured: 'userDiagnostics.freshness.storageNotConfigured',
  storageUnavailable: 'userDiagnostics.freshness.storageUnavailable',
};

const SCOPE_FAILURE_KEYS: Record<string, TranslationKey> = {
  directoryRead: 'userDiagnostics.scope.failure.directoryRead',
  budgetExhausted: 'userDiagnostics.scope.failure.budgetExhausted',
  clientUnavailable: 'userDiagnostics.scope.failure.clientUnavailable',
  unexpected: 'userDiagnostics.scope.failure.unexpected',
};

const LIVE_REASON_KEYS: Record<string, TranslationKey> = {
  scopeExcluded: 'userDiagnostics.live.notAllowed.scopeExcluded',
  scopeUnknown: 'userDiagnostics.live.notAllowed.scopeUnknown',
  userNotFoundInDirectory: 'userDiagnostics.live.failed.userNotFoundInDirectory',
  permissionDenied: 'userDiagnostics.live.failed.permissionDenied',
  graphUnavailable: 'userDiagnostics.live.failed.graphUnavailable',
  credentialsUnavailable: 'userDiagnostics.live.failed.credentialsUnavailable',
  timeout: 'userDiagnostics.live.failed.timeout',
  identityMismatch: 'userDiagnostics.live.failed.identityMismatch',
};

const COMPARISON_KEYS: Record<LiveLicenceRow['comparison'], TranslationKey> = {
  match: 'userDiagnostics.live.comparison.match',
  storedOnly: 'userDiagnostics.live.comparison.storedOnly',
  liveOnly: 'userDiagnostics.live.comparison.liveOnly',
  indeterminate: 'userDiagnostics.live.comparison.indeterminate',
};

const MAPPING_KEYS: Partial<Record<LiveLicenceRow['mapping'], TranslationKey>> = {
  collapsed: 'userDiagnostics.live.mapping.collapsed',
  unmapped: 'userDiagnostics.live.mapping.unmapped',
  catalogueDrift: 'userDiagnostics.live.mapping.catalogueDrift',
};

const PLAN_KEYS: Record<string, TranslationKey> = {
  enabled: 'userDiagnostics.live.copilotPlans.enabled',
  disabled: 'userDiagnostics.live.copilotPlans.disabled',
  pending: 'userDiagnostics.live.copilotPlans.pending',
};

function yesNo(t: TFunction, value: boolean): string {
  return value ? t('admin.common.yes') : t('admin.common.no');
}

function scopeSentence(t: TFunction, diagnostics: UserDiagnostics): string {
  const scope = diagnostics.scope;
  const facts = {
    members: formatNumber(scope.memberCount ?? 0),
    groups: formatNumber(scope.matchedGroupCount ?? 0),
    when: formatUtc(scope.resolvedUtc),
  };
  switch (scope.decision) {
    case 'unfiltered':
      return t('userDiagnostics.scope.unfiltered');
    case 'inScope':
      return t('userDiagnostics.scope.inScope', facts);
    case 'excluded':
      return t('userDiagnostics.scope.excluded', facts);
    default:
      return t('userDiagnostics.scope.unknown', {
        reason: t(SCOPE_FAILURE_KEYS[scope.failureKind ?? ''] ?? 'userDiagnostics.scope.failure.unexpected'),
      });
  }
}

function importCompletedText(t: TFunction, diagnostics: UserDiagnostics): string {
  const freshness = diagnostics.freshness;
  if (freshness.userImportEnabled === false) return t('userDiagnostics.freshness.importDisabled');
  if (freshness.userImportStampState === 'recorded') return formatUtc(freshness.userImportLastCompletedUtc);
  return t(STAMP_STATE_KEYS[freshness.userImportStampState] ?? 'userDiagnostics.freshness.notRecorded');
}

function heldSinceText(t: TFunction, diagnostics: UserDiagnostics, kind: string, when: string | null): string {
  if (kind === 'observed') return t('userDiagnostics.licences.heldSince.observed', { when: formatUtc(when) });
  if (kind === 'beforeTrackingBegan') {
    return t('userDiagnostics.licences.heldSince.beforeTrackingBegan', { when: formatUtc(diagnostics.history.trackingBeganUtc ?? when) });
  }
  return t('userDiagnostics.licences.heldSince.unknown');
}

function comparisonColour(row: LiveLicenceRow): 'success' | 'danger' | 'warning' | 'informative' {
  switch (row.comparison) {
    case 'match':
      return 'success';
    case 'indeterminate':
      return 'informative';
    default:
      return row.classification.isCopilotSeat ? 'danger' : 'warning';
  }
}

/** One live row's detail lines: how it is assigned, any Entra error, Copilot plan state and SKU mapping caveats. */
function liveDetailLines(t: TFunction, row: LiveLicenceRow): string[] {
  const lines: string[] = [];
  for (const sku of row.liveSkus) {
    for (const path of sku.assignmentPaths) {
      lines.push(path === 'group' ? t('userDiagnostics.live.path.group') : t('userDiagnostics.live.path.direct'));
    }
    if (sku.error || (sku.state && sku.state.toLowerCase() !== 'active')) {
      lines.push(t('userDiagnostics.live.assignmentError', { state: sku.state ?? '\u2014', error: sku.error ?? '\u2014' }));
    }
    const plan = PLAN_KEYS[sku.copilotSeatPlans];
    if (plan) lines.push(t(plan));
  }
  const mapping = MAPPING_KEYS[row.mapping];
  if (mapping) lines.push(t(mapping));
  return [...new Set(lines)];
}

function LiveComparison({ upn, allowed, decision }: { upn: string; allowed: boolean; decision: string }) {
  const styles = useStyles();
  const t = useT();
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<LiveLicenceComparison | null>(null);
  const [error, setError] = useState<string | null>(null);
  const controller = useRef<AbortController | null>(null);

  useEffect(() => {
    setResult(null);
    setError(null);
    return () => controller.current?.abort();
  }, [upn]);

  const compare = async () => {
    controller.current?.abort();
    const abort = new AbortController();
    controller.current = abort;
    setLoading(true);
    setError(null);
    setResult(null);
    try {
      const comparison = await fetchLiveLicences(upn, abort.signal);
      if (!abort.signal.aborted) setResult(comparison);
    } catch (err) {
      if (!abort.signal.aborted) setError(err instanceof Error ? err.message : t('userDiagnostics.loadFailed'));
    } finally {
      if (!abort.signal.aborted) setLoading(false);
    }
  };

  const reasonKey = result?.reason ? LIVE_REASON_KEYS[result.reason] : undefined;

  return (
    <div className={styles.stack}>
      <Text weight="semibold">{t('userDiagnostics.live.title')}</Text>
      <Text size={200} className={styles.muted}>
        {t('userDiagnostics.live.description')}
      </Text>
      <div className={styles.actions}>
        <Button icon={<PlugConnected20Regular />} onClick={compare} disabled={loading || !allowed}>
          {t('userDiagnostics.live.button')}
        </Button>
        {!allowed && (
          <Text size={200} className={styles.muted}>
            {decision === 'excluded' ? t('userDiagnostics.live.notAllowed.scopeExcluded') : t('userDiagnostics.live.notAllowed.scopeUnknown')}
          </Text>
        )}
      </div>
      <div aria-live="polite">
        {loading && <Spinner size={32} label={t('userDiagnostics.live.loading')} />}
        {error && (
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
        {result && result.status !== 'ok' && (
          <MessageBar intent={result.status === 'notAllowed' ? 'warning' : 'error'}>
            <MessageBarBody>
              {t(reasonKey ?? 'userDiagnostics.live.failed.graphUnavailable', { status: String(result.httpStatus ?? '') })}
            </MessageBarBody>
          </MessageBar>
        )}
      </div>
      {result && result.status === 'ok' && (
        <div className={styles.stack}>
          <Text size={200} className={styles.muted}>
            {t('userDiagnostics.live.observedAt', { when: formatUtc(result.observedAtUtc) })}
          </Text>
          {!result.complete && (
            <MessageBar intent="warning">
              <MessageBarBody>{t('userDiagnostics.live.incomplete')}</MessageBarBody>
            </MessageBar>
          )}
          {result.directory?.objectIdMatchesStored === false && (
            <MessageBar intent="warning">
              <MessageBarBody>{t('userDiagnostics.live.objectIdMismatch')}</MessageBarBody>
            </MessageBar>
          )}
          {result.directory?.userPrincipalNameDiffersFromStored && (
            <MessageBar intent="info">
              <MessageBarBody>{t('userDiagnostics.live.renamed', { upn: result.directory.userPrincipalName ?? '' })}</MessageBarBody>
            </MessageBar>
          )}
          {result.rows.length === 0 ? (
            <Text>{t('userDiagnostics.live.none')}</Text>
          ) : (
            <div className={styles.tableWrap}>
              <Table size="small" aria-label={t('userDiagnostics.live.title')}>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>{t('userDiagnostics.live.columnLicence')}</TableHeaderCell>
                    <TableHeaderCell>{t('userDiagnostics.live.columnStored')}</TableHeaderCell>
                    <TableHeaderCell>{t('userDiagnostics.live.columnLive')}</TableHeaderCell>
                    <TableHeaderCell>{t('userDiagnostics.live.columnResult')}</TableHeaderCell>
                    <TableHeaderCell>{t('userDiagnostics.licences.columnCopilot')}</TableHeaderCell>
                    <TableHeaderCell>{t('userDiagnostics.live.columnDetail')}</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {result.rows.map((row, index) => (
                    <TableRow key={`${row.licenceTypeId ?? 'sku'}-${row.liveSkus[0]?.skuId ?? index}`}>
                      <TableCell>
                        <div className={styles.detailList}>
                          <Text>{row.name ?? row.skuPartNumber ?? row.liveSkus[0]?.skuId}</Text>
                          <Text className={styles.mono}>{row.liveSkus.map((s) => s.skuPartNumber ?? s.skuId).join(', ') || row.skuPartNumber}</Text>
                        </div>
                      </TableCell>
                      <TableCell>{yesNo(t, row.stored)}</TableCell>
                      <TableCell>{yesNo(t, row.live)}</TableCell>
                      <TableCell>
                        <Badge appearance="tint" color={comparisonColour(row)}>
                          {t(COMPARISON_KEYS[row.comparison])}
                        </Badge>
                      </TableCell>
                      <TableCell>{yesNo(t, row.classification.isCopilotSeat)}</TableCell>
                      <TableCell>
                        <div className={styles.detailList}>
                          {liveDetailLines(t, row).map((line) => (
                            <Text key={line} size={200}>
                              {line}
                            </Text>
                          ))}
                        </div>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * The licence evidence for one person: import scope, how fresh the stored licences are (and what each timestamp
 * does and does not prove), every stored licence with why it does or does not count as a Copilot seat, the
 * observed licence history, and an on-demand comparison with what Microsoft Graph reports now.
 */
export default function LicenceEvidenceCard({ diagnostics }: { diagnostics: UserDiagnostics }) {
  const styles = useStyles();
  const t = useT();
  const [showHistory, setShowHistory] = useState(false);
  const freshness = diagnostics.freshness;
  const history = diagnostics.history;

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('userDiagnostics.freshness.title')}</Subtitle2>} />
      <div className={styles.stack}>
        <div className={styles.facts}>
          <Text className={styles.label}>{t('userDiagnostics.scope.title')}</Text>
          <Text>{scopeSentence(t, diagnostics)}</Text>

          <Text className={styles.label}>{t('userDiagnostics.freshness.metadataLastWritten')}</Text>
          <div className={styles.detailList}>
            <Text>{formatUtc(diagnostics.storedUser?.metadataLastWrittenUtc)}</Text>
            <Text size={200} className={styles.muted}>
              {t('userDiagnostics.freshness.metadataLastWrittenHint')}
            </Text>
          </div>

          <Text className={styles.label}>{t('userDiagnostics.freshness.userImportLastCompleted')}</Text>
          <div className={styles.detailList}>
            <Text>{importCompletedText(t, diagnostics)}</Text>
            {freshness.userImportIntervalHours > 0 && (
              <Text size={200} className={styles.muted}>
                {t('userDiagnostics.freshness.interval', { hours: formatNumber(freshness.userImportIntervalHours) })}
              </Text>
            )}
          </div>

          <Text className={styles.label}>{t('userDiagnostics.freshness.lastTenantLicenceRefresh')}</Text>
          <Text>{freshness.lastTenantLicenceRefreshUtc ? formatUtc(freshness.lastTenantLicenceRefreshUtc) : t('userDiagnostics.freshness.notRecorded')}</Text>

          <Text className={styles.label}>{t('userDiagnostics.freshness.userVerification')}</Text>
          <Text>{freshness.userVerification === 'verified' ? formatUtc(freshness.userVerifiedUtc) : t('userDiagnostics.freshness.notRecorded')}</Text>
        </div>
        <Text size={200} className={styles.muted}>
          {t('userDiagnostics.freshness.tenantWideHint')}
        </Text>

        <Text weight="semibold">{t('userDiagnostics.licences.title')}</Text>
        {diagnostics.licences.length === 0 ? (
          <Text>{t('userDiagnostics.licences.none')}</Text>
        ) : (
          <div className={styles.tableWrap}>
            <Table size="small" aria-label={t('userDiagnostics.licences.title')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('userDiagnostics.licences.columnLicence')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.licences.columnCopilot')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.licences.columnWhy')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.licences.columnHeldSince')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.licences.columnCatalogue')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {diagnostics.licences.map((licence) => (
                  <TableRow key={licence.licenceTypeId}>
                    <TableCell>
                      <div className={styles.detailList}>
                        <Text>{licence.name}</Text>
                        <Text className={styles.mono}>{licence.skuPartNumber}</Text>
                      </div>
                    </TableCell>
                    <TableCell>
                      <Badge appearance="tint" color={licence.classification.isCopilotSeat ? 'brand' : 'informative'}>
                        {yesNo(t, licence.classification.isCopilotSeat)}
                      </Badge>
                    </TableCell>
                    <TableCell>
                      <Text size={200}>{classificationReason(t, licence.classification)}</Text>
                    </TableCell>
                    <TableCell>
                      <Text size={200}>{heldSinceText(t, diagnostics, licence.heldSinceKind, licence.heldSinceUtc)}</Text>
                    </TableCell>
                    <TableCell>
                      <Text size={200}>
                        {!licence.catalogueListed
                          ? t('userDiagnostics.licences.catalogue.notListed')
                          : licence.catalogueCarriesCopilotSeat
                            ? t('userDiagnostics.licences.catalogue.listedWithSeat')
                            : t('userDiagnostics.licences.catalogue.listed')}
                      </Text>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}

        {history.available && diagnostics.storedUser && (
          <div className={styles.stack}>
            <div className={styles.actions}>
              <Button appearance="subtle" onClick={() => setShowHistory((open) => !open)} aria-expanded={showHistory}>
                {showHistory ? t('userDiagnostics.history.hide') : t('userDiagnostics.history.show')}
              </Button>
              <Text size={200} className={styles.muted}>
                {t('userDiagnostics.history.trackingBegan', { when: formatUtc(history.trackingBeganUtc) })}
              </Text>
            </div>
            {showHistory && (
              <>
                <Text size={200} className={styles.muted}>
                  {t('userDiagnostics.history.hint')}
                </Text>
                {history.rows.length === 0 ? (
                  <Text>{t('userDiagnostics.history.none')}</Text>
                ) : (
                  <div className={styles.tableWrap}>
                    <Table size="small" aria-label={t('userDiagnostics.history.title')}>
                      <TableHeader>
                        <TableRow>
                          <TableHeaderCell>{t('userDiagnostics.licences.columnLicence')}</TableHeaderCell>
                          <TableHeaderCell>{t('userDiagnostics.history.columnFrom')}</TableHeaderCell>
                          <TableHeaderCell>{t('userDiagnostics.history.columnTo')}</TableHeaderCell>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {history.rows.map((row) => (
                          <TableRow key={`${row.licenceTypeId}-${row.validFromUtc}`}>
                            <TableCell>
                              <div className={styles.detailList}>
                                <Text>{row.name}</Text>
                                <Text className={styles.mono}>{row.skuPartNumber}</Text>
                              </div>
                            </TableCell>
                            <TableCell>
                              <div className={styles.detailList}>
                                <Text>
                                  {row.heldBeforeTrackingBegan ? t('userDiagnostics.history.heldBeforeTracking') : formatUtc(row.validFromUtc)}
                                </Text>
                                {row.recordedByUserRefresh && (
                                  <Text size={200} className={styles.muted}>
                                    {t('userDiagnostics.history.recordedByUserRefresh')}
                                  </Text>
                                )}
                              </div>
                            </TableCell>
                            <TableCell>{row.validToUtc ? formatUtc(row.validToUtc) : t('userDiagnostics.history.open')}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </div>
                )}
                {history.truncated && (
                  <Text size={200} className={styles.muted}>
                    {t('userDiagnostics.history.truncated', { count: formatNumber(history.rows.length) })}
                  </Text>
                )}
              </>
            )}
          </div>
        )}

        <LiveComparison
          upn={diagnostics.storedUser?.userPrincipalName ?? diagnostics.lookedUpUpn}
          allowed={diagnostics.scope.liveComparisonAllowed}
          decision={diagnostics.scope.decision}
        />
      </div>
    </Card>
  );
}
