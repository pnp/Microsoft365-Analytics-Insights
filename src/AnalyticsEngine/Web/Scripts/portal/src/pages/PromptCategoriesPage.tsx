import { useEffect, useState } from 'react';
import { Button, Card, Checkbox, Field, Input, Select, Text, Title3 } from '@fluentui/react-components';
import { fetchPromptCategoryAdmin, resetPromptCategories, savePromptCategories, type PromptCategoryAdmin, type PromptCategoryConfiguration } from '../api/promptCategoriesApi';
import { EN_CATALOG, useT, type TranslationKey } from '../i18n';
import PromptCategoryRuns from '../components/PromptCategoryRuns';

export default function PromptCategoriesPage() {
  const t = useT();
  const [admin, setAdmin] = useState<PromptCategoryAdmin | null>(null);
  const [busy, setBusy] = useState(false);
  const [messageKey, setMessageKey] = useState<TranslationKey | null>(null);
  useEffect(() => {
    let live = true;
    fetchPromptCategoryAdmin().then(value => { if (live) setAdmin(value); })
      .catch(() => { if (live) setMessageKey('promptCategories.error'); });
    return () => { live = false; };
  }, []);

  const config = admin?.configuration;
  const change = (configuration: PromptCategoryConfiguration) => {
    if (!admin || busy) return;
    setAdmin({ ...admin, configuration });
    setMessageKey('promptCategories.unsaved');
  };
  const update = (index: number, field: 'id' | 'name' | 'description' | 'humanMode', value: string | null) => {
    if (!admin || !config || busy) return;
    change({ ...config, categories: config.categories.map((c, i) => i === index ?
      { ...c, [field]: value, ...(field === 'name' || field === 'id' ? { nameKey: null } : {}),
        ...(field === 'description' || field === 'id' ? { descriptionKey: null } : {}) } : c) });
  };
  const persist = async (reset: boolean) => {
    if (!admin || !config) return;
    setBusy(true);
    try {
      const configuration = reset ? await resetPromptCategories() : await savePromptCategories(config);
      setAdmin({ ...admin, configuration });
      setMessageKey('promptCategories.saved');
    } catch { setMessageKey('promptCategories.error'); }
    finally { setBusy(false); }
  };
  return <div>
    <Title3>{t('promptCategories.title')}</Title3>
    <Card>
      <Text>{t('promptCategories.governance')}</Text>
      <Text>{t('promptCategories.history')}</Text>
      <Text>{t('promptCategories.cycleSnapshot')}</Text>
      <Text>{t('promptCategories.limitedMode')}</Text>
      {messageKey && <Text role="status">{t(messageKey)}</Text>}
      {admin && config && <>
        {!admin.backendConfigured && <Text>{t('promptCategories.backendMissing')}</Text>}
        {!admin.storageAvailable && <Text>{t('promptCategories.storageMissing')}</Text>}
        <Checkbox label={t('promptCategories.enabled')} checked={config.enabled}
          disabled={busy || !admin.storageAvailable || (!admin.backendConfigured && !config.enabled)}
          onChange={(_, data) => change({ ...config, enabled: data.checked === true })} />
        <Field label={t('promptCategories.cap')}>
          <Input disabled={busy} type="number" min={1} max={10000} value={String(config.maxPromptsPerCycle)}
            onChange={(_, data) => change({ ...config, maxPromptsPerCycle: Number(data.value) })} />
        </Field>
        <Text>{t('promptCategories.taxonomy')}</Text>
        {config.categories.map((category, index) => <Card key={index}>
          <Field label={t('promptCategories.id')}>
            <Input value={category.id} disabled={busy || category.id === 'other'} onChange={(_, data) => update(index, 'id', data.value)} />
          </Field>
          <Field label={t('promptCategories.name')}>
            <Input value={category.nameKey && category.nameKey in EN_CATALOG ? t(category.nameKey as TranslationKey) : category.name}
              disabled={busy || category.id === 'other'} maxLength={100} onChange={(_, data) => update(index, 'name', data.value)} />
          </Field>
          <Field label={t('promptCategories.description')}>
            <Input value={category.descriptionKey && category.descriptionKey in EN_CATALOG ? t(category.descriptionKey as TranslationKey) : category.description}
              disabled={busy} maxLength={500} onChange={(_, data) => update(index, 'description', data.value)} />
          </Field>
          <Field label={t('promptCategories.mode')}>
            <Select disabled={busy} value={category.humanMode ?? ''} onChange={(_, data) => update(index, 'humanMode', data.value || null)}>
              <option value="">{t('promptCategories.unassigned')}</option>
              <option value="directing">{t('promptCategories.directing')}</option>
              <option value="supervising">{t('promptCategories.supervising')}</option>
            </Select>
          </Field>
          <Button disabled={busy || category.id === 'other' || config.categories.length <= 2}
            onClick={() => change({ ...config, categories: config.categories.filter((_, i) => i !== index) })}>
            {t('promptCategories.remove')}
          </Button>
        </Card>)}
        <Button disabled={config.categories.length >= 20 || busy}
          onClick={() => change({ ...config, categories: [...config.categories, { id: '', name: '', description: '', humanMode: null }] })}>
          {t('promptCategories.add')}
        </Button>
        <Text>{t('promptCategories.version')}: {config.version}</Text>
        <Button disabled={busy || !admin.storageAvailable} appearance="primary" onClick={() => void persist(false)}>{t('promptCategories.save')}</Button>
        <Button disabled={busy || !admin.storageAvailable} onClick={() => void persist(true)}>{t('promptCategories.reset')}</Button>
      </>}
    </Card>
    <PromptCategoryRuns active />
  </div>;
}
