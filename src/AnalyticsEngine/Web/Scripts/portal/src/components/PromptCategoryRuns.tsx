import { useEffect, useState } from 'react';
import { Card, Text } from '@fluentui/react-components';
import { fetchPromptCategoryRuns, type PromptCategoryRun } from '../api/promptCategoriesApi';
import { formatDateParts, formatNumber, useT, type TranslationKey } from '../i18n';

const REASONS: Record<string, TranslationKey> = {
  disabled: 'promptCategories.status.disabled',
  enabled: 'promptCategories.status.enabled',
  'not-configured': 'promptCategories.status.notConfigured',
  'configuration-unavailable': 'promptCategories.status.configurationUnavailable',
  throttled: 'promptCategories.status.throttled',
  'access-denied': 'promptCategories.status.accessDenied',
  'service-failure': 'promptCategories.status.serviceFailure',
  timeout: 'promptCategories.status.timeout',
  refused: 'promptCategories.status.refused',
  'invalid-response': 'promptCategories.status.invalidResponse',
  'storage-failure': 'promptCategories.status.storageFailure',
};
export default function PromptCategoryRuns({ active }: { active: boolean }) {
  const t = useT();
  const [runs, setRuns] = useState<PromptCategoryRun[]>([]);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    if (!active) return;
    let live = true;
    fetchPromptCategoryRuns().then(value => { if (live) setRuns(value); })
      .catch(() => { if (live) setFailed(true); });
    return () => { live = false; };
  }, [active]);
  return <Card>
    <Text weight="semibold">{t('promptCategories.runs')}</Text>
    {failed && <Text>{t('promptCategories.error')}</Text>}
    {runs.map(run => <Card key={run.startedUtc}>
      <Text>{formatDateParts(new Date(run.startedUtc), { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' })}</Text>
      <Text>{t('promptCategories.reason')}: {t(REASONS[run.counters.reason] ?? 'promptCategories.status.serviceFailure')}</Text>
      {run.counters.taxonomyVersion && <Text>{t('promptCategories.version')}: {run.counters.taxonomyVersion}</Text>}
      <Text>{t('promptCategories.sent')}: {formatNumber(run.counters.sent)} · {t('promptCategories.classified')}: {formatNumber(run.counters.classified)} · {t('promptCategories.other')}: {formatNumber(run.counters.other)}</Text>
      <Text>{t('promptCategories.notClassified')}: {formatNumber(run.counters.notClassified)} · {t('promptCategories.capped')}: {formatNumber(run.counters.capped)} · {t('promptCategories.failed')}: {formatNumber(run.counters.failed)}</Text>
      <Text>{t('promptCategories.tokens')}: {formatNumber(run.counters.inputTokens)} / {formatNumber(run.counters.outputTokens)}</Text>
      <Text>{t('promptCategories.httpAttempts')}: {formatNumber(run.counters.httpAttempts)}</Text>
    </Card>)}
  </Card>;
}
