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
  Link,
  MessageBar,
  MessageBarBody,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Lightbulb20Regular } from '@fluentui/react-icons';
import { formatNumber, useT, type TranslationKey } from '../../i18n';
import type { InvestmentAssumptions, InvestmentScenarioOverrides } from '../../types/copilotInvestment';

const useStyles = makeStyles({
  surface: { maxWidth: 'min(760px, calc(100vw - 32px))', width: '100%' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: '12px' },
  group: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '16px' },
  muted: { color: tokens.colorNeutralForeground3 },
  example: {
    display: 'flex',
    gap: '12px',
    alignItems: 'center',
    flexWrap: 'wrap',
    marginTop: '12px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  exampleNote: { flex: '1 1 260px' },
  hint: { display: 'flex', flexDirection: 'column', gap: '2px' },
});

type NumericKey = Exclude<keyof InvestmentScenarioOverrides, 'currency'>;

interface NumericField {
  key: NumericKey;
  labelKey: TranslationKey;
  guideKey: TranslationKey;
  tenant: (a: InvestmentAssumptions) => number | null;
  min: number;
  max: number;
  /** Entered and shown as a percentage (0-100); sent to the server as a share (0-1). */
  percent?: boolean;
  /** A price must be above zero; minutes and realisation may be zero. */
  aboveMin?: boolean;
}

const REQUIRED_PRICE_FIELDS: NumericField[] = [
  { key: 'seatPrice', labelKey: 'copilotInvestment.field.seatPricePerMonth', guideKey: 'copilotInvestment.whatIf.guide.seatPrice', tenant: (a) => a.seatPricePerMonth, min: 0, max: 10000, aboveMin: true },
  { key: 'hourlyCost', labelKey: 'copilotInvestment.field.hourlyCost', guideKey: 'copilotInvestment.whatIf.guide.hourlyCost', tenant: (a) => a.hourlyCost, min: 0, max: 10000, aboveMin: true },
];

const OPTIONAL_PRICE_FIELDS: NumericField[] = [
  { key: 'studioCreditPrice', labelKey: 'copilotInvestment.field.studioCreditPrice', guideKey: 'copilotInvestment.whatIf.guide.studioCreditPrice', tenant: (a) => a.studioCreditPrice, min: 0, max: 100, aboveMin: true },
  { key: 'creditPrice', labelKey: 'copilotInvestment.field.creditPrice', guideKey: 'copilotInvestment.whatIf.guide.creditPrice', tenant: (a) => a.creditPrice, min: 0, max: 100, aboveMin: true },
];

const MINUTE_FIELDS: NumericField[] = [
  { key: 'minutesLow', labelKey: 'copilotInvestment.whatIf.low', guideKey: 'copilotInvestment.whatIf.guide.minutes', tenant: (a) => a.minutesPerActiveDay.conservative, min: 0, max: 480 },
  { key: 'minutesBase', labelKey: 'copilotInvestment.whatIf.base', guideKey: 'copilotInvestment.whatIf.guide.minutes', tenant: (a) => a.minutesPerActiveDay.base, min: 0, max: 480 },
  { key: 'minutesHigh', labelKey: 'copilotInvestment.whatIf.high', guideKey: 'copilotInvestment.whatIf.guide.minutes', tenant: (a) => a.minutesPerActiveDay.upside, min: 0, max: 480 },
];

const REALISATION_FIELDS: NumericField[] = [
  { key: 'realisationLow', labelKey: 'copilotInvestment.whatIf.low', guideKey: 'copilotInvestment.whatIf.guide.realisation', tenant: (a) => a.realisation.conservative, min: 0, max: 100, percent: true },
  { key: 'realisationBase', labelKey: 'copilotInvestment.whatIf.base', guideKey: 'copilotInvestment.whatIf.guide.realisation', tenant: (a) => a.realisation.base, min: 0, max: 100, percent: true },
  { key: 'realisationHigh', labelKey: 'copilotInvestment.whatIf.high', guideKey: 'copilotInvestment.whatIf.guide.realisation', tenant: (a) => a.realisation.upside, min: 0, max: 100, percent: true },
];

const ALL_FIELDS = [...REQUIRED_PRICE_FIELDS, ...OPTIONAL_PRICE_FIELDS, ...MINUTE_FIELDS, ...REALISATION_FIELDS];

/**
 * Optimistic illustrative figures for trying the page on a deployment nobody has configured. Round numbers chosen to read
 * as an example: they are not Microsoft prices, not any organisation's figures and not measured savings. Applied only as the
 * reader's own what-if, in this browser tab, and only when they ask - never saved, never a default.
 */
export const ILLUSTRATIVE_SCENARIO: Readonly<InvestmentScenarioOverrides> = Object.freeze({
  currency: 'EUR',
  seatPrice: 25,
  studioCreditPrice: 0.01,
  creditPrice: 0.01,
  hourlyCost: 60,
  minutesLow: 15,
  minutesBase: 30,
  minutesHigh: 45,
  realisationLow: 0.3,
  realisationBase: 0.5,
  realisationHigh: 0.7,
});

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

/** Whether the organisation has set none of the figures money is modelled from. */
export function noOrganisationFigures(defaults: InvestmentAssumptions | null): boolean {
  if (!defaults) return true;
  return !defaults.currency && ALL_FIELDS.every((f) => f.tenant(defaults) === null);
}

/** Percentages are shown without float noise: 0.3 x 100 is 30, not 30.000000000000004. */
const toPercent = (share: number): number => Math.round(share * 100 * 1e6) / 1e6;

/**
 * The reader's own what-if figures. Only applied when the reader asks, so typing never sends a request per keystroke;
 * never saved, and shown beside each organisation figure so it is clear what is being replaced. It says why money is
 * withheld when nothing is set, what each figure means and where to find it, and offers clearly labelled illustrative
 * figures to try the page with.
 */
export default function WhatIfDialog({ open, scenario, defaults, missing, settingsHref, onApply, onClose }: {
  open: boolean;
  scenario: InvestmentScenarioOverrides | null;
  defaults: InvestmentAssumptions | null;
  /** Labels of the organisation figures the return still needs, as the page words them. */
  missing: string[];
  /** The administration page, for a reader who may set the organisation's figures; absent otherwise. */
  settingsHref?: string;
  onApply: (scenario: InvestmentScenarioOverrides | null, origin?: 'example') => void;
  onClose: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const initial = (): Record<string, string> => {
    const values: Record<string, string> = {};
    for (const [key, value] of Object.entries(scenario ?? {})) {
      if (value === undefined || value === null) continue;
      const percent = ALL_FIELDS.find((f) => f.key === key)?.percent === true;
      values[key] =
        typeof value === 'number' ? formatNumber(percent ? toPercent(value) : value, { useGrouping: false, maximumFractionDigits: 6 }) : value;
    }
    return values;
  };
  const [text, setText] = useState<Record<string, string>>(initial);
  const [touched, setTouched] = useState(false);

  const parsed: Record<string, number | null | 'invalid' | 'range'> = {};
  for (const field of ALL_FIELDS) {
    const value = parseDecimal(text[field.key] ?? '');
    parsed[field.key] =
      typeof value === 'number' && (value > field.max || value < field.min || (field.aboveMin && value <= field.min)) ? 'range' : value;
  }
  const invalid = Object.values(parsed).some((v) => v === 'invalid' || v === 'range');
  const nothingSet = noOrganisationFigures(defaults);

  const apply = () => {
    const next: InvestmentScenarioOverrides = {};
    const currency = (text.currency ?? '').trim().toUpperCase();
    if (currency) next.currency = currency;
    for (const field of ALL_FIELDS) {
      const value = parsed[field.key];
      if (typeof value === 'number') next[field.key] = field.percent ? Math.round((value / 100) * 1e6) / 1e6 : value;
    }
    onApply(countOverrides(next) > 0 ? next : null);
  };

  const hint = (f: NumericField) => {
    const tenantValue = defaults ? f.tenant(defaults) : null;
    return (
      <span className={styles.hint}>
        <span>{t(f.guideKey)}</span>
        <span className={styles.muted}>
          {tenantValue === null
            ? t('copilotInvestment.whatIf.noDefault')
            : f.percent
              ? t('copilotInvestment.whatIf.defaultPercent', { value: formatNumber(toPercent(tenantValue), { maximumFractionDigits: 2 }) })
              : t('copilotInvestment.whatIf.defaultValue', { value: formatNumber(tenantValue, { maximumFractionDigits: 4 }) })}
        </span>
      </span>
    );
  };

  const field = (f: NumericField, labelKey: TranslationKey = f.labelKey) => {
    const value = parsed[f.key];
    const message =
      touched && value === 'invalid'
        ? t('copilotInvestment.whatIf.invalidNumber')
        : touched && value === 'range'
          ? t(f.aboveMin ? 'copilotInvestment.whatIf.outOfRangeAbove' : 'copilotInvestment.whatIf.outOfRange', {
              min: formatNumber(f.min),
              max: formatNumber(f.max),
            })
          : undefined;
    return (
      <Field key={f.key} label={t(labelKey)} hint={hint(f)} validationMessage={message} validationState={message ? 'error' : 'none'}>
        <Input
          inputMode="decimal"
          value={text[f.key] ?? ''}
          contentAfter={f.percent ? <Text className={styles.muted}>{'%'}</Text> : undefined}
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
            {(nothingSet || missing.length > 0) && (
              <MessageBar intent="info" style={{ marginTop: '12px' }}>
                <MessageBarBody>
                  {nothingSet ? t('copilotInvestment.whatIf.status.none') : t('copilotInvestment.whatIf.status.partial', { fields: missing.join(', ') })}{' '}
                  {settingsHref ? <Link href={settingsHref}>{t('copilotInvestment.whatIf.status.setDefaults')}</Link> : t('copilotInvestment.whatIf.status.askAdmin')}
                </MessageBarBody>
              </MessageBar>
            )}
            <div className={styles.example}>
              <Button icon={<Lightbulb20Regular />} onClick={() => onApply({ ...ILLUSTRATIVE_SCENARIO }, 'example')}>
                {t('copilotInvestment.whatIf.example.button')}
              </Button>
              <Text size={200} className={styles.exampleNote}>
                {t('copilotInvestment.whatIf.example.note')}
              </Text>
            </div>
            <div className={styles.group}>
              <Text weight="semibold">{t('copilotInvestment.whatIf.required')}</Text>
              <div className={styles.grid}>
                <Field
                  label={t('copilotInvestment.field.currency')}
                  hint={
                    <span className={styles.hint}>
                      <span>{t('copilotInvestment.whatIf.guide.currency')}</span>
                      <span className={styles.muted}>
                        {defaults?.currency ? t('copilotInvestment.whatIf.defaultValue', { value: defaults.currency }) : t('copilotInvestment.whatIf.noDefault')}
                      </span>
                    </span>
                  }
                >
                  <Input maxLength={3} value={text.currency ?? ''} onChange={(_e, data) => setText((current) => ({ ...current, currency: data.value }))} />
                </Field>
                {REQUIRED_PRICE_FIELDS.map((f) => field(f))}
              </div>
            </div>
            <div className={styles.group}>
              <Text weight="semibold">{t('copilotInvestment.whatIf.minutes')}</Text>
              <div className={styles.grid}>{MINUTE_FIELDS.map((f) => field(f))}</div>
            </div>
            <div className={styles.group}>
              <Text weight="semibold">{t('copilotInvestment.whatIf.realisation')}</Text>
              <div className={styles.grid}>{REALISATION_FIELDS.map((f) => field(f))}</div>
            </div>
            <div className={styles.group}>
              <Text weight="semibold">{t('copilotInvestment.whatIf.optional')}</Text>
              <Text size={200} className={styles.muted}>
                {t('copilotInvestment.whatIf.optional.description')}
              </Text>
              <div className={styles.grid}>{OPTIONAL_PRICE_FIELDS.map((f) => field(f))}</div>
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
