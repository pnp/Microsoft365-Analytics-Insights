import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Accordion, AccordionHeader, AccordionItem, AccordionPanel, Badge, Body1, Button, Card, Dialog, DialogActions,
  DialogBody, DialogContent, DialogSurface, DialogTitle, DialogTrigger, Field, Input, MessageBar, MessageBarActions,
  MessageBarBody, MessageBarTitle, Select, Subtitle2, Switch, Text, Title3, makeStyles, tokens,
} from '@fluentui/react-components';
import { Add16Regular, ArrowClockwise16Regular, ArrowCounterclockwise16Regular, ArrowUndo16Regular, Delete16Regular, Save16Regular } from '@fluentui/react-icons';
import {
  fetchPromptCategoryAdmin, resetPromptCategories, savePromptCategories,
  type PromptCategoryAdmin, type PromptCategoryConfiguration,
} from '../api/promptCategoriesApi';
import Spinner from '../components/Spinner';
import PromptCategoryRuns from '../components/PromptCategoryRuns';
import { isStoredConfigurationInvalid, promptCategoryErrorText } from '../components/promptCategoryErrors';
import {
  MAX_CATEGORIES, MAX_DESCRIPTION_LENGTH, MAX_NAME_LENGTH, MAX_PROMPTS_CAP,
  configurationSnapshot, validateTaxonomy,
} from '../components/promptCategoryValidation';
import { EN_CATALOG, formatNumber, useT, type TranslationKey } from '../i18n';

const useStyles = makeStyles({
  page: { display: 'flex', flexDirection: 'column', gap: '16px', paddingBottom: '8px' },
  header: { display: 'flex', flexDirection: 'column', gap: '4px' },
  subtitle: { color: tokens.colorNeutralForeground2, maxWidth: '820px' },
  summary: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(170px, 1fr))', gap: '12px' },
  tile: { display: 'flex', flexDirection: 'column', gap: '6px' },
  tileLabel: { color: tokens.colorNeutralForeground3 },
  tileValue: { display: 'flex', alignItems: 'center', gap: '6px', minHeight: '24px', fontWeight: tokens.fontWeightSemibold },
  mono: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
  notes: { display: 'flex', flexDirection: 'column', gap: '8px', maxWidth: '900px' },
  section: { display: 'flex', flexDirection: 'column', gap: '12px' },
  sectionHeader: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  settings: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))', gap: '16px', alignItems: 'start' },
  capInput: { maxWidth: '200px' },
  rows: { display: 'flex', flexDirection: 'column', gap: '10px' },
  row: {
    display: 'grid', gridTemplateColumns: 'minmax(140px, 1fr) minmax(180px, 2fr) minmax(150px, 1fr) auto',
    gap: '12px', alignItems: 'start', padding: '12px',
    border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    '@media (max-width: 800px)': { gridTemplateColumns: '1fr' },
  },
  rowWide: { gridColumn: '1 / 4', '@media (max-width: 800px)': { gridColumn: '1' } },
  rowAction: { alignSelf: 'end', gridRow: '1', gridColumn: '4', '@media (max-width: 800px)': { gridRow: 'auto', gridColumn: '1', justifySelf: 'start' } },
  bar: {
    position: 'sticky', bottom: 0, zIndex: 2, display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap',
    padding: '10px 16px', backgroundColor: tokens.colorNeutralBackground1, boxShadow: tokens.shadow8,
    border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium,
  },
  barStatus: { display: 'flex', alignItems: 'center', gap: '8px', flexGrow: 1, minWidth: '240px' },
  barActions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

function Tile({ label, children }: { label: string; children: React.ReactNode }) {
  const styles = useStyles();
  return <Card size="small" className={styles.tile}>
    <Text size={200} className={styles.tileLabel}>{label}</Text>
    <div className={styles.tileValue}>{children}</div>
  </Card>;
}

export default function PromptCategoriesPage() {
  const t = useT();
  const styles = useStyles();
  const [admin, setAdmin] = useState<PromptCategoryAdmin | null>(null);
  const [baseline, setBaseline] = useState('');
  const [capText, setCapText] = useState('');
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [saveError, setSaveError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const [resetOpen, setResetOpen] = useState(false);
  const [focusIndex, setFocusIndex] = useState<number | null>(null);
  const [runsKey, setRunsKey] = useState(0);

  const adopt = useCallback((next: PromptCategoryAdmin) => {
    setAdmin(next);
    setBaseline(configurationSnapshot(next.configuration));
    setCapText(String(next.configuration.maxPromptsPerCycle));
  }, []);

  const load = useCallback(() => {
    setLoading(true);
    setLoadError(null);
    return fetchPromptCategoryAdmin().then(adopt).catch(setLoadError).finally(() => setLoading(false));
  }, [adopt]);
  useEffect(() => { void load(); }, [load]);

  const config = admin?.configuration;
  const problems = useMemo(() => config ? validateTaxonomy(config) : null, [config]);
  const dirty = !!config && configurationSnapshot(config) !== baseline;

  useEffect(() => {
    if (!dirty) return undefined;
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);
  useEffect(() => {
    if (focusIndex === null) return;
    document.getElementById(`prompt-category-id-${focusIndex}`)?.focus();
    setFocusIndex(null);
  }, [focusIndex]);

  const change = (configuration: PromptCategoryConfiguration) => {
    if (!admin || busy) return;
    setAdmin({ ...admin, configuration });
    setSaved(false);
    setSaveError(null);
  };
  const update = (index: number, field: 'id' | 'name' | 'description' | 'humanMode', value: string | null) => {
    if (!config) return;
    change({ ...config, categories: config.categories.map((c, i) => i === index ?
      { ...c, [field]: value, ...(field === 'name' || field === 'id' ? { nameKey: null } : {}),
        ...(field === 'description' || field === 'id' ? { descriptionKey: null } : {}) } : c) });
  };
  const discard = () => {
    if (!admin) return;
    void load();
    setSaved(false);
    setSaveError(null);
  };
  const persist = async (reset: boolean) => {
    if (!admin || !config) return;
    setBusy(true);
    setSaveError(null);
    try {
      const configuration = reset ? await resetPromptCategories() : await savePromptCategories(config);
      adopt({ ...admin, configuration });
      setSaved(true);
      setRunsKey(k => k + 1);
    } catch (e) { setSaveError(e); setSaved(false); }
    finally { setBusy(false); }
  };
  const repair = async () => {
    setBusy(true);
    try { await resetPromptCategories(); await load(); }
    catch (e) { setLoadError(e); }
    finally { setBusy(false); }
  };

  const nameOf = (c: { name: string; nameKey?: string | null }) =>
    c.nameKey && c.nameKey in EN_CATALOG ? t(c.nameKey as TranslationKey) : c.name;
  const descriptionOf = (c: { description: string; descriptionKey?: string | null }) =>
    c.descriptionKey && c.descriptionKey in EN_CATALOG ? t(c.descriptionKey as TranslationKey) : c.description;
  const rowLabel = (field: TranslationKey, n: number) => t('promptCategories.rowLabel', { field: t(field), n });
  const invalid = (problems?.total ?? 0) > 0;
  const canSave = !!admin?.storageAvailable && dirty && !invalid && !busy;

  return <div className={styles.page}>
    <div className={styles.header}>
      <Title3 as="h1">{t('promptCategories.title')}</Title3>
      <Body1 className={styles.subtitle}>{t('promptCategories.subtitle')}</Body1>
    </div>

    <MessageBar intent="warning" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{t('promptCategories.governanceTitle')}</MessageBarTitle>
        {t('promptCategories.governance')}
      </MessageBarBody>
    </MessageBar>
    <Accordion collapsible className={styles.notes}>
      <AccordionItem value="notes">
        <AccordionHeader size="small">{t('promptCategories.notesTitle')}</AccordionHeader>
        <AccordionPanel>
          <div className={styles.notes}>
            <Body1>{t('promptCategories.history')}</Body1>
            <Body1>{t('promptCategories.cycleSnapshot')}</Body1>
            <Body1>{t('promptCategories.limitedMode')}</Body1>
          </div>
        </AccordionPanel>
      </AccordionItem>
    </Accordion>

    {loading && !admin && <Spinner label={t('promptCategories.loading')} />}
    {loadError !== null && !admin && <MessageBar intent="error" layout="multiline">
      <MessageBarBody>{promptCategoryErrorText(loadError, t)}</MessageBarBody>
      <MessageBarActions>
        {isStoredConfigurationInvalid(loadError) &&
          <Button size="small" disabled={busy} icon={<ArrowCounterclockwise16Regular />} onClick={() => void repair()}>{t('promptCategories.resetConfirm')}</Button>}
        <Button size="small" icon={<ArrowClockwise16Regular />} onClick={() => void load()}>{t('promptCategories.retry')}</Button>
      </MessageBarActions>
    </MessageBar>}

    {admin && config && problems && <>
      <div className={styles.summary} role="group" aria-label={t('promptCategories.settingsTitle')}>
        <Tile label={t('promptCategories.summary.categorisation')}>
          <Badge appearance="filled" color={config.enabled ? 'success' : 'informative'}>
            {t(config.enabled ? 'promptCategories.status.enabled' : 'promptCategories.status.disabled')}
          </Badge>
        </Tile>
        <Tile label={t('promptCategories.summary.backend')}>
          <Badge appearance="tint" color={admin.backendConfigured ? 'success' : 'warning'}>
            {t(admin.backendConfigured ? 'promptCategories.summary.configured' : 'promptCategories.summary.notConfigured')}
          </Badge>
        </Tile>
        <Tile label={t('promptCategories.summary.storage')}>
          <Badge appearance="tint" color={admin.storageAvailable ? 'success' : 'danger'}>
            {t(admin.storageAvailable ? 'promptCategories.summary.available' : 'promptCategories.summary.unavailable')}
          </Badge>
        </Tile>
        <Tile label={t('promptCategories.version')}>
          <span className={styles.mono} title={config.version}>{config.version.slice(0, 12)}</span>
        </Tile>
        <Tile label={t('promptCategories.summary.cap')}>{formatNumber(config.maxPromptsPerCycle)}</Tile>
      </div>

      {!admin.backendConfigured && <MessageBar intent="warning" layout="multiline"><MessageBarBody>{t('promptCategories.backendMissing')}</MessageBarBody></MessageBar>}
      {!admin.storageAvailable && <MessageBar intent="error" layout="multiline"><MessageBarBody>{t('promptCategories.storageMissing')}</MessageBarBody></MessageBar>}

      <Card className={styles.section}>
        <Subtitle2>{t('promptCategories.settingsTitle')}</Subtitle2>
        <div className={styles.settings}>
          <Field hint={t('promptCategories.enabledHint')}>
            <Switch label={t('promptCategories.enabled')} checked={config.enabled}
              disabled={busy || !admin.storageAvailable || (!admin.backendConfigured && !config.enabled)}
              onChange={(_, data) => change({ ...config, enabled: data.checked })} />
          </Field>
          <Field label={t('promptCategories.cap')} required
            validationState={problems.cap ? 'error' : 'none'}
            validationMessage={problems.cap ? t(problems.cap, { min: formatNumber(1), max: formatNumber(MAX_PROMPTS_CAP) }) : undefined}>
            <Input className={styles.capInput} disabled={busy} type="number" min={1} max={MAX_PROMPTS_CAP} value={capText}
              onChange={(_, data) => { setCapText(data.value); change({ ...config, maxPromptsPerCycle: data.value === '' ? NaN : Number(data.value) }); }} />
          </Field>
        </div>
      </Card>

      <Card className={styles.section}>
        <div className={styles.sectionHeader}>
          <div>
            <Subtitle2>{t('promptCategories.taxonomyTitle')}</Subtitle2>
            <div><Text size={200} className={styles.muted}>{t('promptCategories.taxonomy')} · {t('promptCategories.categoryCount', { count: formatNumber(config.categories.length), max: formatNumber(MAX_CATEGORIES) })}</Text></div>
          </div>
          <Button icon={<Add16Regular />} disabled={config.categories.length >= MAX_CATEGORIES || busy}
            onClick={() => { change({ ...config, categories: [...config.categories, { id: '', name: '', description: '', humanMode: null }] }); setFocusIndex(config.categories.length); }}>
            {t('promptCategories.add')}
          </Button>
        </div>
        {problems.count && <MessageBar intent="error"><MessageBarBody>{t(problems.count)}</MessageBarBody></MessageBar>}
        <div className={styles.rows} role="list">
          {config.categories.map((category, index) => {
            const p = problems.categories[index] ?? {};
            const fixed = category.id === 'other';
            const n = index + 1;
            return <div key={index} role="listitem" className={styles.row}>
              <Field label={t('promptCategories.col.id')} validationState={p.id ? 'error' : 'none'} validationMessage={p.id ? t(p.id) : undefined}>
                <Input id={`prompt-category-id-${index}`} aria-label={rowLabel('promptCategories.id', n)} value={category.id}
                  disabled={busy || fixed} maxLength={40} onChange={(_, data) => update(index, 'id', data.value)} />
              </Field>
              <Field label={t('promptCategories.col.name')} validationState={p.name ? 'error' : 'none'} validationMessage={p.name ? t(p.name) : undefined}>
                <Input aria-label={rowLabel('promptCategories.name', n)} value={nameOf(category)}
                  disabled={busy || fixed} maxLength={MAX_NAME_LENGTH} onChange={(_, data) => update(index, 'name', data.value)} />
              </Field>
              <Field label={t('promptCategories.col.mode')}>
                <Select aria-label={rowLabel('promptCategories.mode', n)} disabled={busy} value={category.humanMode ?? ''}
                  onChange={(_, data) => update(index, 'humanMode', data.value || null)}>
                  <option value="">{t('promptCategories.unassigned')}</option>
                  <option value="directing">{t('promptCategories.directing')}</option>
                  <option value="supervising">{t('promptCategories.supervising')}</option>
                </Select>
              </Field>
              <div className={styles.rowAction}>
                <Button appearance="subtle" icon={<Delete16Regular />} aria-label={t('promptCategories.removeCategory', { n })}
                  title={t('promptCategories.remove')} disabled={busy || fixed || config.categories.length <= 2}
                  onClick={() => change({ ...config, categories: config.categories.filter((_, i) => i !== index) })} />
              </div>
              <Field className={styles.rowWide} label={t('promptCategories.col.description')}
                validationState={p.description ? 'error' : 'none'} validationMessage={p.description ? t(p.description) : undefined}>
                <Input aria-label={rowLabel('promptCategories.description', n)} value={descriptionOf(category)}
                  disabled={busy} maxLength={MAX_DESCRIPTION_LENGTH} onChange={(_, data) => update(index, 'description', data.value)} />
              </Field>
            </div>;
          })}
        </div>
      </Card>

      {saveError !== null && <MessageBar intent="error" layout="multiline"><MessageBarBody>{promptCategoryErrorText(saveError, t)}</MessageBarBody></MessageBar>}
      {saved && !dirty && <MessageBar intent="success" role="status"><MessageBarBody>{t('promptCategories.saved')}</MessageBarBody></MessageBar>}

      <div className={styles.bar} role="group" aria-label={t('promptCategories.save')}>
        <div className={styles.barStatus} role="status">
          {dirty && <Badge appearance="filled" color="warning">{t('promptCategories.unsavedBadge')}</Badge>}
          <Text size={200} className={styles.muted}>
            {dirty ? t(invalid ? 'promptCategories.fixFirst' : 'promptCategories.unsaved') : t('promptCategories.noChanges')}
          </Text>
        </div>
        <div className={styles.barActions}>
          <Button icon={<ArrowUndo16Regular />} disabled={!dirty || busy} onClick={discard}>{t('promptCategories.discard')}</Button>
          <Dialog open={resetOpen} onOpenChange={(_, data) => setResetOpen(data.open)}>
            <DialogTrigger disableButtonEnhancement>
              <Button icon={<ArrowCounterclockwise16Regular />} disabled={busy || !admin.storageAvailable}>{t('promptCategories.reset')}</Button>
            </DialogTrigger>
            <DialogSurface>
              <DialogBody>
                <DialogTitle>{t('promptCategories.resetTitle')}</DialogTitle>
                <DialogContent>{t('promptCategories.resetBody')}</DialogContent>
                <DialogActions>
                  <DialogTrigger disableButtonEnhancement><Button appearance="secondary">{t('promptCategories.cancel')}</Button></DialogTrigger>
                  <Button appearance="primary" onClick={() => { setResetOpen(false); void persist(true); }}>{t('promptCategories.resetConfirm')}</Button>
                </DialogActions>
              </DialogBody>
            </DialogSurface>
          </Dialog>
          <Button appearance="primary" icon={<Save16Regular />} disabled={!canSave} onClick={() => void persist(false)}>{t('promptCategories.save')}</Button>
        </div>
      </div>
    </>}

    <PromptCategoryRuns key={runsKey} active storageAvailable={admin ? admin.storageAvailable : undefined} />
  </div>;
}
