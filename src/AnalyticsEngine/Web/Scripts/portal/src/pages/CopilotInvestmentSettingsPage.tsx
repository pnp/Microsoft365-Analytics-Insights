import { useEffect, useState } from 'react';
import {
  Body1,
  Button,
  Card,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Textarea,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { formatNumber, useT, type TranslationKey } from '../i18n';
import {
  COPILOT_INVESTMENT_SETTINGS_ERROR_KEYS,
  InvestmentSettingsError,
  fetchInvestmentSettings,
  saveInvestmentSettings,
  type InvestmentSettingsModel,
} from '../api/copilotInvestmentApi';
import type { InvestmentAssumptions } from '../types/copilotInvestment';
import { FIELD_LABEL_KEYS, formatShortDate } from '../components/copilotInvestment/investmentShared';
import { parseDecimal } from '../components/copilotInvestment/WhatIfDialog';
import Spinner from '../components/Spinner';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px', maxWidth: '960px' },
  card: { display: 'flex', flexDirection: 'column', gap: '12px', padding: '16px' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: '12px' },
  actions: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

type FormText = Record<string, string>;


function toText(a: InvestmentAssumptions): FormText {
  const plain = (v: number | null): string => (v === null || v === undefined ? '' : formatNumber(v, { useGrouping: false, maximumFractionDigits: 6 }));
  return {
    currency: a.currency ?? '',
    seatPricePerMonth: plain(a.seatPricePerMonth),
    creditPrice: plain(a.creditPrice),
    studioCreditPrice: plain(a.studioCreditPrice),
    hourlyCost: plain(a.hourlyCost),
    'minutesPerActiveDay.conservative': plain(a.minutesPerActiveDay.conservative),
    'minutesPerActiveDay.base': plain(a.minutesPerActiveDay.base),
    'minutesPerActiveDay.upside': plain(a.minutesPerActiveDay.upside),
    'realisation.conservative': plain(a.realisation.conservative),
    'realisation.base': plain(a.realisation.base),
    'realisation.upside': plain(a.realisation.upside),
    note: a.note ?? '',
  };
}

/** The form as assumptions, or the fields that are not numbers. */
function fromText(text: FormText): { assumptions: InvestmentAssumptions; invalid: string[] } {
  const invalid: string[] = [];
  const number = (field: string): number | null => {
    const parsed = parseDecimal(text[field] ?? '');
    if (parsed === 'invalid') {
      invalid.push(field);
      return null;
    }
    return parsed;
  };
  return {
    assumptions: {
      currency: (text.currency ?? '').trim().toUpperCase() || null,
      seatPricePerMonth: number('seatPricePerMonth'),
      creditPrice: number('creditPrice'),
      studioCreditPrice: number('studioCreditPrice'),
      hourlyCost: number('hourlyCost'),
      minutesPerActiveDay: {
        conservative: number('minutesPerActiveDay.conservative'),
        base: number('minutesPerActiveDay.base'),
        upside: number('minutesPerActiveDay.upside'),
      },
      realisation: {
        conservative: number('realisation.conservative'),
        base: number('realisation.base'),
        upside: number('realisation.upside'),
      },
      note: (text.note ?? '').trim() || null,
    },
    invalid,
  };
}

const HINT_KEYS: Partial<Record<string, TranslationKey>> = {
  currency: 'copilotInvestment.settings.currency.hint',
  seatPricePerMonth: 'copilotInvestment.settings.seatPrice.hint',
  creditPrice: 'copilotInvestment.settings.creditPrice.hint',
  studioCreditPrice: 'copilotInvestment.settings.studioCreditPrice.hint',
  hourlyCost: 'copilotInvestment.settings.hourlyCost.hint',
};

/** The time-and-value ranges, cautious to optimistic, with the hint each shows. */
const TIME_FIELDS: { field: string; hint: TranslationKey }[] = [
  { field: 'minutesPerActiveDay.conservative', hint: 'copilotInvestment.settings.minutes.hint' },
  { field: 'minutesPerActiveDay.base', hint: 'copilotInvestment.settings.minutes.hint' },
  { field: 'minutesPerActiveDay.upside', hint: 'copilotInvestment.settings.minutes.hint' },
  { field: 'realisation.conservative', hint: 'copilotInvestment.settings.realisation.hint' },
  { field: 'realisation.base', hint: 'copilotInvestment.settings.realisation.hint' },
  { field: 'realisation.upside', hint: 'copilotInvestment.settings.realisation.hint' },
];

function errorText(t: ReturnType<typeof useT>, code: string): string {
  return t(COPILOT_INVESTMENT_SETTINGS_ERROR_KEYS.get(code) ?? 'copilotInvestment.settings.error.saveFailed');
}

/**
 * Administration > Copilot investment assumptions. Empty until an administrator sets them: the report never
 * models money with a figure nobody chose.
 */
export default function CopilotInvestmentSettingsPage() {
  const styles = useStyles();
  const t = useT();
  const [model, setModel] = useState<InvestmentSettingsModel | null>(null);
  const [text, setText] = useState<FormText | null>(null);
  const [loadError, setLoadError] = useState<{ code: string | null } | null>(null);
  const [saving, setSaving] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);
  const [saved, setSaved] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  // Loads when the page opens and when the administrator asks for the latest - never because the language
  // changed: a refetch would overwrite what they are typing. So `t` stays out of these dependencies, and a
  // failure is kept as its code and worded at render time, in whatever language is showing then.
  useEffect(() => {
    let cancelled = false;
    setLoadError(null);
    fetchInvestmentSettings()
      .then((result) => {
        if (cancelled) return;
        setModel(result);
        setText(toText(result.assumptions));
        setErrors([]);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setLoadError({ code: e instanceof InvestmentSettingsError ? e.code : null });
      });
    return () => {
      cancelled = true;
    };
  }, [reloadKey]);

  const save = async () => {
    if (!model || !text) return;
    const { assumptions, invalid } = fromText(text);
    if (invalid.length > 0) {
      setErrors([t('copilotInvestment.whatIf.invalidNumber')]);
      return;
    }
    setSaving(true);
    setErrors([]);
    setSaved(null);
    try {
      const result = await saveInvestmentSettings(model.version, assumptions);
      setSaved(result.version === model.version ? t('copilotInvestment.settings.unchanged') : t('copilotInvestment.settings.saved', { version: formatNumber(result.version) }));
      setModel(result);
      setText(toText(result.assumptions));
    } catch (e: unknown) {
      if (e instanceof InvestmentSettingsError) {
        const codes = e.validationErrors.length > 0 ? e.validationErrors : [e.code];
        setErrors(codes.map((code) => errorText(t, code)));
      } else {
        setErrors([t('copilotInvestment.settings.error.saveFailed')]);
      }
    } finally {
      setSaving(false);
    }
  };

  const input = (field: string, hintKey?: TranslationKey) => (
    <Field key={field} label={t(FIELD_LABEL_KEYS[field])} hint={hintKey ? t(hintKey) : undefined}>
      <Input
        inputMode={field === 'currency' ? 'text' : 'decimal'}
        maxLength={field === 'currency' ? 3 : undefined}
        value={text?.[field] ?? ''}
        disabled={!model?.durable || saving}
        onChange={(_e, data) => setText((current) => ({ ...(current ?? {}), [field]: data.value }))}
      />
    </Field>
  );

  return (
    <div className={styles.stack}>
      <Title3>{t('copilotInvestment.settings.title')}</Title3>
      <Body1 block>{t('copilotInvestment.settings.intro')}</Body1>

      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{loadError.code === null ? t('copilotInvestment.settings.error.loadFailed') : errorText(t, loadError.code)}</MessageBarBody>
        </MessageBar>
      )}
      {!model && !loadError && <Spinner size={48} label={t('copilotInvestment.settings.loading')} />}

      {model && text && (
        <>
          {!model.durable && (
            <MessageBar intent="warning">
              <MessageBarBody>{t('copilotInvestment.settings.notDurable')}</MessageBarBody>
            </MessageBar>
          )}

          <Text size={200} className={styles.muted}>
            {model.updatedUtc
              ? model.updatedBy
                ? t('copilotInvestment.settings.version', { version: formatNumber(model.version), who: model.updatedBy, when: formatShortDate(model.updatedUtc) })
                : t('copilotInvestment.settings.versionNoName', { version: formatNumber(model.version), when: formatShortDate(model.updatedUtc) })
              : t('copilotInvestment.settings.neverSaved')}
          </Text>

          <Card className={styles.card}>
            <Text weight="semibold" size={400}>
              {t('copilotInvestment.settings.prices')}
            </Text>
            <Text size={200} className={styles.muted}>
              {t('copilotInvestment.settings.prices.description')}
            </Text>
            <div className={styles.grid}>
              {input('currency', HINT_KEYS.currency)}
              {input('seatPricePerMonth', HINT_KEYS.seatPricePerMonth)}
              {input('studioCreditPrice', HINT_KEYS.studioCreditPrice)}
              {input('creditPrice', HINT_KEYS.creditPrice)}
              {input('hourlyCost', HINT_KEYS.hourlyCost)}
            </div>
          </Card>

          <Card className={styles.card}>
            <Text weight="semibold" size={400}>
              {t('copilotInvestment.settings.time')}
            </Text>
            <Text size={200} className={styles.muted}>
              {t('copilotInvestment.settings.time.description')}
            </Text>
            <div className={styles.grid}>
              {TIME_FIELDS.map((f) => input(f.field, f.hint))}
            </div>
            <Field label={t('copilotInvestment.field.note')} hint={t('copilotInvestment.settings.note.hint')}>
              <Textarea
                value={text.note ?? ''}
                maxLength={500}
                disabled={!model.durable || saving}
                onChange={(_e, data) => setText((current) => ({ ...(current ?? {}), note: data.value }))}
              />
            </Field>
          </Card>

          {errors.length > 0 && (
            <MessageBar intent="error">
              <MessageBarBody>
                {errors.map((e) => (
                  <div key={e}>{e}</div>
                ))}
              </MessageBarBody>
            </MessageBar>
          )}
          {saved && (
            <MessageBar intent="success">
              <MessageBarBody>{saved}</MessageBarBody>
            </MessageBar>
          )}

          <div className={styles.actions}>
            <Button appearance="primary" onClick={save} disabled={!model.durable || saving}>
              {saving ? t('copilotInvestment.settings.saving') : t('copilotInvestment.settings.save')}
            </Button>
            <Button appearance="secondary" onClick={() => setReloadKey((k) => k + 1)} disabled={saving}>
              {t('copilotInvestment.settings.reload')}
            </Button>
          </div>

          <Card className={styles.card}>
            <Text weight="semibold" size={400}>
              {t('copilotInvestment.settings.history')}
            </Text>
            {model.history.length === 0 ? (
              <Text size={200}>{t('copilotInvestment.settings.history.empty')}</Text>
            ) : (
              <Table size="small" aria-label={t('copilotInvestment.settings.history')}>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>{t('copilotInvestment.settings.history.col.version')}</TableHeaderCell>
                    <TableHeaderCell>{t('copilotInvestment.settings.history.col.who')}</TableHeaderCell>
                    <TableHeaderCell>{t('copilotInvestment.settings.history.col.when')}</TableHeaderCell>
                    <TableHeaderCell>{t('copilotInvestment.settings.history.col.changes')}</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {model.history.map((change) => (
                    <TableRow key={change.version}>
                      <TableCell>{formatNumber(change.version)}</TableCell>
                      <TableCell>{change.changedBy ?? t('copilotInvestment.settings.history.unknownWho')}</TableCell>
                      <TableCell>{formatShortDate(change.changedUtc)}</TableCell>
                      <TableCell>
                        {change.changes.map((c) => (
                          <div key={c.field}>
                            {t('copilotInvestment.settings.history.change', {
                              field: FIELD_LABEL_KEYS[c.field] ? t(FIELD_LABEL_KEYS[c.field]) : c.field,
                              from: c.oldValue ?? t('copilotInvestment.settings.history.empty.value'),
                              to: c.newValue ?? t('copilotInvestment.settings.history.empty.value'),
                            })}
                          </div>
                        ))}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </Card>
        </>
      )}
    </div>
  );
}
