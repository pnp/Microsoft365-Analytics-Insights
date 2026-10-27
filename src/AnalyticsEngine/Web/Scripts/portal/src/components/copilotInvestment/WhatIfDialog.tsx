import { useState } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { formatNumber, useT, type TranslationKey } from '../../i18n';
import type { InvestmentAssumptions, InvestmentScenarioOverrides } from '../../types/copilotInvestment';

const useStyles = makeStyles({
  surface: { maxWidth: 'min(720px, calc(100vw - 32px))', width: '100%' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '12px' },
  group: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '16px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

type NumericKey = Exclude<keyof InvestmentScenarioOverrides, 'currency'>;

const PRICE_FIELDS: { key: NumericKey; labelKey: TranslationKey; tenant: (a: InvestmentAssumptions) => number | null }[] = [
  { key: 'seatPrice', labelKey: 'copilotInvestment.field.seatPricePerMonth', tenant: (a) => a.seatPricePerMonth },
  { key: 'studioCreditPrice', labelKey: 'copilotInvestment.field.studioCreditPrice', tenant: (a) => a.studioCreditPrice },
  { key: 'creditPrice', labelKey: 'copilotInvestment.field.creditPrice', tenant: (a) => a.creditPrice },
  { key: 'hourlyCost', labelKey: 'copilotInvestment.field.hourlyCost', tenant: (a) => a.hourlyCost },
];

const MINUTE_FIELDS: { key: NumericKey; labelKey: TranslationKey; tenant: (a: InvestmentAssumptions) => number | null }[] = [
  { key: 'minutesLow', labelKey: 'copilotInvestment.whatIf.low', tenant: (a) => a.minutesPerActiveDay.conservative },
  { key: 'minutesBase', labelKey: 'copilotInvestment.whatIf.base', tenant: (a) => a.minutesPerActiveDay.base },
  { key: 'minutesHigh', labelKey: 'copilotInvestment.whatIf.high', tenant: (a) => a.minutesPerActiveDay.upside },
];

const REALISATION_FIELDS: { key: NumericKey; labelKey: TranslationKey; tenant: (a: InvestmentAssumptions) => number | null }[] = [
  { key: 'realisationLow', labelKey: 'copilotInvestment.whatIf.low', tenant: (a) => a.realisation.conservative },
  { key: 'realisationBase', labelKey: 'copilotInvestment.whatIf.base', tenant: (a) => a.realisation.base },
  { key: 'realisationHigh', labelKey: 'copilotInvestment.whatIf.high', tenant: (a) => a.realisation.upside },
];

/** Accepts the reader's decimal separator: "0,5" and "0.5" are both one half. */
export function parseDecimal(text: string): number | null | 'invalid' {
  const trimmed = text.trim();
  if (trimmed === '') return null;
  const normalised = trimmed.replace(/\s/g, '').replace(',', '.');
  if (!/^\d+(\.\d+)?$/.test(normalised)) return 'invalid';
  return Number(normalised);
}

/** How many what-if figures are in force, for the toolbar button. */
export function countOverrides(scenario: InvestmentScenarioOverrides | null): number {
  if (!scenario) return 0;
  return Object.values(scenario).filter((v) => v !== undefined && v !== null && v !== '').length;
}

/**
 * The reader's own what-if figures. Only applied when the reader asks, so typing never sends a request per
 * keystroke; never saved, and shown beside each organisation default so it is clear what is being replaced.
 */
export default function WhatIfDialog({ open, scenario, defaults, onApply, onClose }: {
  open: boolean;
  scenario: InvestmentScenarioOverrides | null;
  defaults: InvestmentAssumptions | null;
  onApply: (scenario: InvestmentScenarioOverrides | null) => void;
  onClose: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const initial = (): Record<string, string> => {
    const values: Record<string, string> = {};
    for (const [key, value] of Object.entries(scenario ?? {})) {
      if (value !== undefined && value !== null) values[key] = typeof value === 'number' ? formatNumber(value, { useGrouping: false, maximumFractionDigits: 6 }) : value;
    }
    return values;
  };
  const [text, setText] = useState<Record<string, string>>(initial);
  const [touched, setTouched] = useState(false);

  const parsed: Record<string, number | null | 'invalid'> = {};
  for (const field of [...PRICE_FIELDS, ...MINUTE_FIELDS, ...REALISATION_FIELDS]) parsed[field.key] = parseDecimal(text[field.key] ?? '');
  const invalid = Object.values(parsed).some((v) => v === 'invalid');

  const apply = () => {
    const next: InvestmentScenarioOverrides = {};
    const currency = (text.currency ?? '').trim().toUpperCase();
    if (currency) next.currency = currency;
    for (const [key, value] of Object.entries(parsed)) {
      if (typeof value === 'number') next[key as NumericKey] = value;
    }
    onApply(countOverrides(next) > 0 ? next : null);
  };

  const field = (f: { key: NumericKey; labelKey: TranslationKey; tenant: (a: InvestmentAssumptions) => number | null }) => {
    const tenantValue = defaults ? f.tenant(defaults) : null;
    const value = parsed[f.key];
    return (
      <Field
        key={f.key}
        label={t(f.labelKey)}
        hint={tenantValue === null ? t('copilotInvestment.whatIf.noDefault') : t('copilotInvestment.whatIf.defaultValue', { value: formatNumber(tenantValue, { maximumFractionDigits: 4 }) })}
        validationMessage={touched && value === 'invalid' ? t('copilotInvestment.whatIf.invalidNumber') : undefined}
        validationState={touched && value === 'invalid' ? 'error' : 'none'}
      >
        <Input
          inputMode="decimal"
          value={text[f.key] ?? ''}
          onChange={(_e, data) => {
            setTouched(true);
            setText((current) => ({ ...current, [f.key]: data.value }));
          }}
        />
      </Field>
    );
  };

  return (
    <Dialog open={open} onOpenChange={(_e, data) => !data.open && onClose()}>
      <DialogSurface className={styles.surface}>
        <DialogBody>
          <DialogTitle>{t('copilotInvestment.whatIf.title')}</DialogTitle>
          <DialogContent>
            <Text size={200} block className={styles.muted}>
              {t('copilotInvestment.whatIf.description')}
            </Text>
            <div className={styles.group}>
              <div className={styles.grid}>
                <Field
                  label={t('copilotInvestment.field.currency')}
                  hint={defaults?.currency ? t('copilotInvestment.whatIf.defaultValue', { value: defaults.currency }) : t('copilotInvestment.whatIf.noDefault')}
                >
                  <Input
                    maxLength={3}
                    value={text.currency ?? ''}
                    onChange={(_e, data) => setText((current) => ({ ...current, currency: data.value }))}
                  />
                </Field>
                {PRICE_FIELDS.map(field)}
              </div>
            </div>
            <div className={styles.group}>
              <Text weight="semibold">{t('copilotInvestment.whatIf.minutes')}</Text>
              <div className={styles.grid}>{MINUTE_FIELDS.map(field)}</div>
            </div>
            <div className={styles.group}>
              <Text weight="semibold">{t('copilotInvestment.whatIf.realisation')}</Text>
              <div className={styles.grid}>{REALISATION_FIELDS.map(field)}</div>
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={() => onApply(null)}>
              {t('copilotInvestment.whatIf.reset')}
            </Button>
            <Button appearance="secondary" onClick={onClose}>
              {t('copilotInvestment.whatIf.close')}
            </Button>
            <Button appearance="primary" onClick={apply} disabled={invalid}>
              {t('copilotInvestment.whatIf.apply')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
