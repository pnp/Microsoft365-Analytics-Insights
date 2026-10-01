import { useEffect, useMemo, useState } from 'react';
import {
  Body1,
  Button,
  Card,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Spinner,
  Subtitle2,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular, Save16Regular } from '@fluentui/react-icons';
import {
  GlobalFilterApiError,
  fetchGlobalFilter,
  previewGlobalFilter,
  saveGlobalFilter,
} from '../api/globalFilterApi';
import toast from '../components/toast';
import GlobalFilterEditor, { globalFilterProblemText } from '../components/globalFilter/GlobalFilterEditor';
import { describeGlobalFilter } from '../components/globalFilter/describeGlobalFilter';
import {
  fromClauseModels,
  globalFilterProblem,
  isEmptyGlobalFilter,
  sameGlobalFilter,
  serializeGlobalFilter,
  usesViewer,
} from '../components/globalFilter/globalFilterModel';
import { useGlobalFilter } from '../components/globalFilter/GlobalFilterProvider';
import { useUserFilterDimensions } from '../components/userFilter/useUserFilterDimensions';
import { formatDateParts, formatNumber, useT, type TFunction } from '../i18n';
import {
  EMPTY_GLOBAL_FILTER,
  type GlobalFilterAdmin,
  type GlobalFilterDefinition,
  type GlobalFilterEffective,
} from '../types/globalFilter';

/** The manual upgrade script that creates the table the filter is kept in. A file name, the same in every language. */
const UPGRADE_SCRIPT = '202610011330001_PortalGlobalFilter.manual.sql';

/** How long to wait after an edit before asking the server what the draft would mean for the administrator. */
const PREVIEW_DEBOUNCE_MS = 400;

const useStyles = makeStyles({
  page: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  intro: {
    color: tokens.colorNeutralForeground2,
    maxWidth: '820px',
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap',
  },
  list: {
    margin: '0',
    paddingInlineStart: '20px',
  },
});

function messageOf(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

function lastChanged(t: TFunction, admin: GlobalFilterAdmin): string {
  if (!admin.modifiedUtc) return t('globalFilter.admin.neverSet');
  const when = formatDateParts(new Date(admin.modifiedUtc), { dateStyle: 'medium', timeStyle: 'short' });
  return admin.modifiedBy
    ? t('globalFilter.admin.lastChangedBy', { when, by: admin.modifiedBy })
    : t('globalFilter.admin.lastChanged', { when });
}

/**
 * Where a portal administrator sets the global report filter: conditions every Insights report applies
 * for everyone who opens it, on top of their own filters, which readers can see but not change.
 *
 * The page previews every draft against the administrator's own account - the filter applies to them
 * too - so a condition on "the viewer's own value" can be checked before anyone else sees its effect.
 */
export default function GlobalFilterPage() {
  const styles = useStyles();
  const t = useT();
  const globalFilter = useGlobalFilter();
  const { list, error: dimensionsError, reload: reloadDimensions } = useUserFilterDimensions();

  const [admin, setAdmin] = useState<GlobalFilterAdmin | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [attempt, setAttempt] = useState(0);
  const [draft, setDraft] = useState<GlobalFilterDefinition>(EMPTY_GLOBAL_FILTER);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<unknown>(null);
  const [conflict, setConflict] = useState(false);
  const [preview, setPreview] = useState<GlobalFilterEffective | null>(null);
  const [previewError, setPreviewError] = useState<unknown>(null);
  const [previewLoading, setPreviewLoading] = useState(false);

  // Deliberately not keyed on the language: switching it must not throw away a draft.
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setLoadError(null);

    fetchGlobalFilter(controller.signal)
      .then((model) => {
        setAdmin(model);
        setDraft(fromClauseModels(model.clauses));
        setConflict(false);
        setSaveError(null);
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setLoadError(e);
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => controller.abort();
  }, [attempt]);

  const saved = useMemo(() => fromClauseModels(admin?.clauses), [admin]);
  const draftText = serializeGlobalFilter(draft);
  const dirty = !sameGlobalFilter(draft, saved);
  const problem = globalFilterProblemText(t, draft);
  const storageAvailable = admin?.storageAvailable ?? false;

  // What the draft would mean for the administrator - the filter applies to them as well.
  useEffect(() => {
    if (!admin || !draftText || globalFilterProblem(draft)) {
      setPreview(null);
      setPreviewError(null);
      setPreviewLoading(false);
      return;
    }

    const controller = new AbortController();
    const timer = setTimeout(() => {
      setPreviewLoading(true);
      previewGlobalFilter(draftText, controller.signal)
        .then((result) => {
          setPreview(result);
          setPreviewError(null);
        })
        .catch((e: unknown) => {
          if (!controller.signal.aborted) setPreviewError(e);
        })
        .finally(() => {
          if (!controller.signal.aborted) setPreviewLoading(false);
        });
    }, PREVIEW_DEBOUNCE_MS);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
    // `draft` is fully described by `draftText`, which is what is sent.
  }, [admin, draftText]);

  const save = async () => {
    if (!admin) return;
    setSaving(true);
    setSaveError(null);
    try {
      const next = await saveGlobalFilter(draftText, admin.revision);
      setAdmin(next);
      setDraft(fromClauseModels(next.clauses));
      toast.success(isEmptyGlobalFilter(draft) ? t('globalFilter.admin.toast.removed') : t('globalFilter.admin.toast.saved'));
      // The bar on every Insights page, and the figures under it, follow the new filter.
      void globalFilter.refresh();
    } catch (e: unknown) {
      if (e instanceof GlobalFilterApiError && e.code === 'revisionConflict') setConflict(true);
      else setSaveError(e);
    } finally {
      setSaving(false);
    }
  };

  const canSave = !!admin && storageAvailable && !saving && !conflict && !problem && (dirty || admin.invalid);
  const dimensions = list?.dimensions ?? [];

  return (
    <div className={styles.page}>
      <div>
        <Title3 block>{t('globalFilter.admin.title')}</Title3>
        <Body1 block className={styles.intro}>{t('globalFilter.admin.intro')}</Body1>
      </div>

      {loading && !admin && <Spinner size="small" label={t('globalFilter.admin.loading')} />}

      {!!loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{messageOf(loadError, t('globalFilter.admin.loadFailed'))}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" icon={<ArrowClockwise16Regular />} onClick={() => setAttempt((a) => a + 1)}>
              {t('globalFilter.admin.retry')}
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {admin && !admin.rolesEnforced && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>{t('globalFilter.admin.rolesNotEnforced')}</MessageBarBody>
        </MessageBar>
      )}

      {admin && !admin.storageAvailable && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>{t('globalFilter.admin.storageUnavailable', { script: UPGRADE_SCRIPT })}</MessageBarBody>
        </MessageBar>
      )}

      {admin?.invalid && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>{t('globalFilter.admin.invalidStored')}</MessageBarBody>
        </MessageBar>
      )}

      {globalFilter.effective?.bypassed && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>{t('globalFilter.admin.bypassedForYou')}</MessageBarBody>
        </MessageBar>
      )}

      {admin && (
        <>
          <Card className={styles.card}>
            <Subtitle2>{t('globalFilter.admin.conditions.heading')}</Subtitle2>
            <Text size={200} className={styles.muted}>{t('globalFilter.admin.conditions.note')}</Text>

            {dimensionsError && (
              <MessageBar intent="warning">
                <MessageBarBody>{t('userFilter.bar.dimensionsError')}</MessageBarBody>
                <MessageBarActions>
                  <Button size="small" onClick={reloadDimensions}>
                    {t('userFilter.bar.retry')}
                  </Button>
                </MessageBarActions>
              </MessageBar>
            )}

            <GlobalFilterEditor
              filter={draft}
              onChange={(next) => {
                setDraft(next);
                setSaveError(null);
              }}
              dimensions={dimensions}
              names={preview?.filter?.dimensionNames}
              disabled={!storageAvailable || saving}
            />

            {problem && <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>{problem}</Text>}
          </Card>

          <Card className={styles.card}>
            <Subtitle2>{t('globalFilter.admin.preview.heading')}</Subtitle2>
            <Text size={200} className={styles.muted}>{t('globalFilter.admin.preview.note')}</Text>
            <PreviewBody draft={draft} preview={preview} loading={previewLoading} error={previewError} />
          </Card>

          {conflict && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>{t('errors.globalFilter.revisionConflict')}</MessageBarBody>
              <MessageBarActions>
                <Button size="small" icon={<ArrowClockwise16Regular />} onClick={() => setAttempt((a) => a + 1)}>
                  {t('globalFilter.admin.reload')}
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}

          {!!saveError && (
            <MessageBar intent="error">
              <MessageBarBody>{messageOf(saveError, t('globalFilter.admin.saveFailed'))}</MessageBarBody>
            </MessageBar>
          )}

          <div className={styles.actions}>
            <Button appearance="primary" icon={<Save16Regular />} disabled={!canSave} onClick={() => void save()}>
              {saving ? t('globalFilter.admin.saving') : t('globalFilter.admin.save')}
            </Button>
            <Button disabled={!dirty || saving} onClick={() => setDraft(saved)}>
              {t('globalFilter.admin.discard')}
            </Button>
            {dirty && <Text size={200}>{t('globalFilter.admin.unsaved')}</Text>}
            <Text size={200} className={styles.muted}>{lastChanged(t, admin)}</Text>
          </div>
          <Text size={200} className={styles.muted}>{t('globalFilter.admin.propagation')}</Text>
        </>
      )}
    </div>
  );
}

function PreviewBody({
  draft,
  preview,
  loading,
  error,
}: {
  draft: GlobalFilterDefinition;
  preview: GlobalFilterEffective | null;
  loading: boolean;
  error: unknown;
}) {
  const styles = useStyles();
  const t = useT();

  if (isEmptyGlobalFilter(draft)) return <Text size={300}>{t('globalFilter.admin.preview.none')}</Text>;
  if (error) return <Text size={300}>{messageOf(error, t('globalFilter.admin.preview.failed'))}</Text>;
  if (!preview?.filter) {
    return loading ? <Spinner size="extra-tiny" label={t('globalFilter.admin.preview.loading')} labelPosition="after" /> : null;
  }

  const echo = preview.filter;
  const source = { names: echo.dimensionNames };
  return (
    <>
      <Text size={300} weight="semibold">
        {t('globalFilter.admin.preview.coverage', {
          matched: formatNumber(echo.matchedPeople),
          total: formatNumber(echo.directoryPeople),
        })}
      </Text>
      <Text size={300}>
        {t('globalFilter.admin.preview.description', { description: describeGlobalFilter(t, echo.clauses, 'reader', source) })}
      </Text>
      {!echo.viewerFound && usesViewer(echo.clauses) && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('globalFilter.admin.preview.viewerNotFound')}</MessageBarBody>
        </MessageBar>
      )}
      {loading && <Spinner size="extra-tiny" label={t('globalFilter.admin.preview.loading')} labelPosition="after" className={styles.muted} />}
    </>
  );
}
