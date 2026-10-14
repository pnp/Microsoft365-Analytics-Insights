import { useCallback, useEffect, useMemo, useState } from 'react';
import { Badge, Body1, Button, Card, CardHeader, Field, Input, MessageBar, MessageBarBody, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Title3, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowClockwise16Regular, ArrowReset20Regular, Save16Regular } from '@fluentui/react-icons';
import {
  COPILOT_ADOPTION_SETTINGS_ERROR_KEYS,
  CopilotAdoptionSettingsError,
  fetchCopilotAdoptionSettings,
  resetCopilotAdoptionSettings,
  saveCopilotAdoptionSettings,
} from '../api/copilotAdoptionSettingsApi';
import { PortalPermissionError, SessionExpiredError } from '../api/http';
import Spinner from '../components/Spinner';
import { SCORE_FIELD_LABEL_KEYS, THRESHOLD_FIELDS, WEIGHT_FIELDS, validateScoreSettings } from '../components/copilotAdoption/scoreSettings';
import { formatUtc } from '../components/health/healthShared';
import { formatNumber, useT, type TFunction, type TranslationKey } from '../i18n';
import type { CopilotAdoptionScoreField, CopilotAdoptionScoreValues, CopilotAdoptionSettingsModel } from '../types/copilotAdoptionSettings';

type Draft = Record<CopilotAdoptionScoreField, string>;

function toDraft(values: CopilotAdoptionScoreValues): Draft {
  return {
    frequencyWeightPercent: String(values.frequencyWeightPercent),
    depthWeightPercent: String(values.depthWeightPercent),
    breadthWeightPercent: String(values.breadthWeightPercent),
    developingScore: String(values.developingScore),
    establishedScore: String(values.establishedScore),
    championScore: String(values.championScore),
  };
}

function parse(value: string): number {
  return /^\s*\d+\s*$/.test(value) ? Number(value.trim()) : Number.NaN;
}

function toValues(draft: Draft): CopilotAdoptionScoreValues {
  return {
    frequencyWeightPercent: parse(draft.frequencyWeightPercent),
    depthWeightPercent: parse(draft.depthWeightPercent),
    breadthWeightPercent: parse(draft.breadthWeightPercent),
    developingScore: parse(draft.developingScore),
    establishedScore: parse(draft.establishedScore),
    championScore: parse(draft.championScore),
  };
}

function errorText(t: TFunction, code: string): string {
  return t(COPILOT_ADOPTION_SETTINGS_ERROR_KEYS.get(code) ?? 'admin.copilotAdoptionSettings.error.saveFailed');
}

const useStyles = makeStyles({
  cards: { display: 'flex', flexDirection: 'column', gap: '16px', marginTop: '16px' },
  fields: { display: 'flex', gap: '16px', flexWrap: 'wrap', alignItems: 'start' },
  field: { width: '200px' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  titleRow: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  total: { fontWeight: tokens.fontWeightSemibold },
});

export default function CopilotAdoptionSettingsPage() {
  const t = useT();
  const styles = useStyles();
  const [model, setModel] = useState<CopilotAdoptionSettingsModel | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

  const report = useCallback((e: unknown, fallback: string) => {
    if (e instanceof SessionExpiredError || e instanceof PortalPermissionError) {
      setError(e.message);
      return;
    }
    if (e instanceof CopilotAdoptionSettingsError) {
      const codes = e.validationErrors.length > 0 ? e.validationErrors : [e.code];
      setError(codes.map((code) => errorText(t, code)).join(' '));
      return;
    }
    setError(errorText(t, fallback));
  }, [t]);

  const accept = (next: CopilotAdoptionSettingsModel) => {
    setModel(next);
    setDraft(toDraft(next.settings));
  };

  const load = useCallback(async () => {
    setError(null);
    setSaved(null);
    try { accept(await fetchCopilotAdoptionSettings()); }
    catch (e) { report(e, 'loadFailed'); }
    finally { setLoading(false); }
  }, [report]);

  useEffect(() => { void load(); }, [load]);

  const values = useMemo(() => (draft ? toValues(draft) : null), [draft]);
  const problems = useMemo(
    () => (values && model ? validateScoreSettings(values, model.minThreshold, model.maxThreshold) : []),
    [values, model],
  );
  const changed = !!values && !!model && (Object.keys(values) as CopilotAdoptionScoreField[]).some((f) => values[f] !== model.settings[f]);
  const weightTotal = values ? WEIGHT_FIELDS.reduce((sum, f) => sum + (Number.isNaN(values[f]) ? 0 : values[f]), 0) : 0;

  const save = async () => {
    if (!model || !values) return;
    setBusy(true); setError(null); setSaved(null);
    try {
      accept(await saveCopilotAdoptionSettings(model.version, values));
      setSaved(t('admin.copilotAdoptionSettings.saved'));
    } catch (e) { report(e, 'saveFailed'); }
    finally { setBusy(false); }
  };

  const reset = async () => {
    if (!model) return;
    setBusy(true); setError(null); setSaved(null);
    try {
      accept(await resetCopilotAdoptionSettings(model.version));
      setSaved(t('admin.copilotAdoptionSettings.resetDone'));
    } catch (e) { report(e, 'saveFailed'); }
    finally { setBusy(false); }
  };

  if (loading) return <Spinner label={t('admin.copilotAdoptionSettings.loading')} />;

  const customised = (model?.customisedFields.length ?? 0) > 0;
  const input = (field: CopilotAdoptionScoreField, suffixKey: TranslationKey) => (
    <Field
      key={field}
      className={styles.field}
      label={t(SCORE_FIELD_LABEL_KEYS[field])}
      hint={model ? t(suffixKey, { value: formatNumber(model.defaults[field]) }) : undefined}
    >
      <Input
        type="number"
        inputMode="numeric"
        value={draft?.[field] ?? ''}
        disabled={!model || busy}
        onChange={(_, data) => setDraft((prev) => (prev ? { ...prev, [field]: data.value } : prev))}
      />
    </Field>
  );

  return <div>
    <div className={styles.titleRow}>
      <Title3>{t('admin.copilotAdoptionSettings.title')}</Title3>
      {model && <Badge appearance="tint" color={customised ? 'warning' : 'success'}>
        {t(customised ? 'admin.copilotAdoptionSettings.status.customised' : 'admin.copilotAdoptionSettings.status.defaults')}
      </Badge>}
    </div>
    <Body1>{t('admin.copilotAdoptionSettings.description')}</Body1>
    <div className={styles.cards}>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {saved && <MessageBar intent="success"><MessageBarBody>{saved}</MessageBarBody></MessageBar>}
      {model && !model.durable && <MessageBar intent="warning"><MessageBarBody>{t('admin.copilotAdoptionSettings.notDurable')}</MessageBarBody></MessageBar>}
      {model && <>
        <Card>
          <CardHeader header={<Text weight="semibold">{t('admin.copilotAdoptionSettings.weights.title')}</Text>} />
          <Text>{t('admin.copilotAdoptionSettings.weights.description')}</Text>
          <div className={styles.fields}>
            {WEIGHT_FIELDS.map((f) => input(f, 'admin.copilotAdoptionSettings.defaultPercent'))}
          </div>
          <Text className={styles.total} role="status">{t('admin.copilotAdoptionSettings.weights.total', { total: formatNumber(weightTotal) })}</Text>
        </Card>
        <Card>
          <CardHeader header={<Text weight="semibold">{t('admin.copilotAdoptionSettings.thresholds.title')}</Text>} />
          <Text>{t('admin.copilotAdoptionSettings.thresholds.description', { min: formatNumber(model.minThreshold), max: formatNumber(model.maxThreshold) })}</Text>
          <div className={styles.fields}>
            {THRESHOLD_FIELDS.map((f) => input(f, 'admin.copilotAdoptionSettings.defaultScore'))}
          </div>
          {values && !problems.includes('thresholdOutOfRange') && !problems.includes('thresholdsNotAscending') && (
            <Text className={styles.muted}>
              {t('admin.copilotAdoptionSettings.thresholds.preview', {
                developing: formatNumber(values.developingScore),
                established: formatNumber(values.establishedScore),
                champion: formatNumber(values.championScore),
              })}
            </Text>
          )}
          <Text className={styles.muted}>{t('admin.copilotAdoptionSettings.thresholds.coworkIndependent')}</Text>
        </Card>
        {problems.length > 0 && <MessageBar intent="warning"><MessageBarBody>{problems.map((p) => errorText(t, p)).join(' ')}</MessageBarBody></MessageBar>}
        <Text className={styles.muted}>{t('admin.copilotAdoptionSettings.effect')}</Text>
        <div className={styles.actions}>
          <Button appearance="primary" icon={<Save16Regular />} onClick={save} disabled={busy || !model.durable || !changed || problems.length > 0}>{t('admin.copilotAdoptionSettings.save')}</Button>
          <Button icon={<ArrowReset20Regular />} onClick={reset} disabled={busy || !model.durable || !customised}>{t('admin.copilotAdoptionSettings.reset')}</Button>
          <Button icon={<ArrowClockwise16Regular />} onClick={load} disabled={busy}>{t('admin.copilotAdoptionSettings.reload')}</Button>
        </div>
        <Card>
          <CardHeader header={<Text weight="semibold">{t('admin.copilotAdoptionSettings.history.title')}</Text>} />
          {model.updatedUtc && <Text className={styles.muted}>
            {t('admin.copilotAdoptionSettings.history.lastChanged', { who: model.updatedBy ?? t('admin.common.unknown'), when: formatUtc(model.updatedUtc) })}
          </Text>}
          {model.history.length === 0
            ? <Text className={styles.muted}>{t('admin.copilotAdoptionSettings.history.none')}</Text>
            : <Table size="small" aria-label={t('admin.copilotAdoptionSettings.history.title')}>
              <TableHeader><TableRow>
                <TableHeaderCell>{t('admin.copilotAdoptionSettings.history.when')}</TableHeaderCell>
                <TableHeaderCell>{t('admin.copilotAdoptionSettings.history.who')}</TableHeaderCell>
                <TableHeaderCell>{t('admin.copilotAdoptionSettings.history.action')}</TableHeaderCell>
                <TableHeaderCell>{t('admin.copilotAdoptionSettings.history.changes')}</TableHeaderCell>
              </TableRow></TableHeader>
              <TableBody>
                {model.history.map((entry) => (
                  <TableRow key={entry.version}>
                    <TableCell>{formatUtc(entry.changedUtc)}</TableCell>
                    <TableCell>{entry.changedBy ?? t('admin.common.unknown')}</TableCell>
                    <TableCell>{t(entry.action === 'reset' ? 'admin.copilotAdoptionSettings.history.actionReset' : 'admin.copilotAdoptionSettings.history.actionSave')}</TableCell>
                    <TableCell>{entry.changes.map((c) => t('admin.copilotAdoptionSettings.history.change', {
                      field: SCORE_FIELD_LABEL_KEYS[c.field] ? t(SCORE_FIELD_LABEL_KEYS[c.field]) : c.field,
                      oldValue: formatNumber(c.oldValue),
                      newValue: formatNumber(c.newValue),
                    })).join('; ')}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>}
        </Card>
      </>}
    </div>
  </div>;
}