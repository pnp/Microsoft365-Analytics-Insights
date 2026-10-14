import { useState } from 'react';
import {
  Body1, Button, Card, Field, MessageBar, MessageBarBody, Subtitle2, Text, Textarea, makeStyles, tokens,
} from '@fluentui/react-components';
import { testPromptCategory, type PromptCategoryTestResult } from '../api/promptCategoriesApi';
import { EN_CATALOG, formatNumber, useT, type TranslationKey } from '../i18n';
import { promptCategoryErrorText } from './promptCategoryErrors';
import Spinner from './Spinner';

const MAX_PROMPT_LENGTH = 5000;
const useStyles = makeStyles({
  card: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground2 },
  action: { alignSelf: 'flex-start' },
  result: { display: 'flex', flexDirection: 'column', gap: '6px', overflowWrap: 'anywhere' },
});

export default function PromptCategoryTestDrive({ version, disabled }: { version: string; disabled: boolean }) {
  const t = useT();
  const styles = useStyles();
  const [prompt, setPrompt] = useState('');
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<PromptCategoryTestResult | null>(null);
  const [error, setError] = useState<unknown>(null);
  const submit = async () => {
    if (busy || disabled || !prompt.trim() || prompt.length > MAX_PROMPT_LENGTH) return;
    setBusy(true);
    setResult(null);
    setError(null);
    try { setResult(await testPromptCategory(prompt, version)); }
    catch (e) { setError(e); }
    finally { setBusy(false); }
  };
  const category = result?.category;
  const label = category?.nameKey && category.nameKey in EN_CATALOG
    ? t(category.nameKey as TranslationKey) : category?.name;
  const description = category?.descriptionKey && category.descriptionKey in EN_CATALOG
    ? t(category.descriptionKey as TranslationKey) : category?.description;
  return <Card className={styles.card}>
    <Subtitle2 as="h2">{t('promptCategories.test.title')}</Subtitle2>
    <Body1 className={styles.muted}>{t('promptCategories.test.notice')}</Body1>
    {disabled && <MessageBar intent="info"><MessageBarBody>{t('promptCategories.test.saveFirst')}</MessageBarBody></MessageBar>}
    <Field label={t('promptCategories.test.prompt')} hint={t('promptCategories.test.limit', { max: formatNumber(MAX_PROMPT_LENGTH) })}>
      <Textarea value={prompt} rows={4} maxLength={MAX_PROMPT_LENGTH} disabled={busy || disabled}
        onChange={(_, data) => { setPrompt(data.value); setResult(null); setError(null); }} />
    </Field>
    <Button className={styles.action} appearance="primary" disabled={busy || disabled || !prompt.trim()}
      onClick={() => void submit()}>{t('promptCategories.test.submit')}</Button>
    {busy && <Spinner label={t('promptCategories.test.running')} />}
    {error !== null && <MessageBar intent="error" layout="multiline"><MessageBarBody>{promptCategoryErrorText(error, t)}</MessageBarBody></MessageBar>}
    {result && category && <div className={styles.result} role="status" aria-live="polite">
      <Text weight="semibold">{t('promptCategories.test.result', { category: label ?? category.id })}</Text>
      <Body1>{description}</Body1>
      <Text className={styles.muted}>{t('promptCategories.test.mode', { mode: t(category.humanMode === 'directing'
        ? 'promptCategories.directing' : category.humanMode === 'supervising'
          ? 'promptCategories.supervising' : 'promptCategories.unassigned') })}</Text>
      <Text className={styles.muted}>{t('promptCategories.test.modeNote')}</Text>
      <Text>{t('promptCategories.test.tokens', { input: formatNumber(result.inputTokens), output: formatNumber(result.outputTokens) })}</Text>
      <Text size={200}>{t('promptCategories.test.version', { version: result.version.slice(0, 12) })}</Text>
    </div>}
  </Card>;
}
