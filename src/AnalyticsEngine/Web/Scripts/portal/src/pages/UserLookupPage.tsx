import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import {
  Title3,
  Text,
  Body1,
  Input,
  Button,
  MessageBar,
  MessageBarBody,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { SearchRegular } from '@fluentui/react-icons';
import { useLocation, useNavigate } from 'react-router-dom';
import { fetchUserDiagnostics, fetchUserSummary } from '../api/userLookupApi';
import type { UserDataSummary } from '../types/userData';
import type { UserDiagnostics, UserDiagnosticsContext } from '../types/userDiagnostics';
import Spinner from '../components/Spinner';
import UserProfileCard from '../components/userlookup/UserProfileCard';
import CategoryTable from '../components/userlookup/CategoryTable';
import UserDiagnosticsPanel from '../components/userlookup/diagnostics/UserDiagnosticsPanel';
import { useT, useTNode } from '../i18n';

const useStyles = makeStyles({
  form: {
    display: 'flex',
    gap: '8px',
    marginBlock: '16px',
    flexWrap: 'wrap',
  },
  input: {
    minWidth: '340px',
  },
  results: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

/** The report context a "Troubleshoot this person" link left in router state, if any. */
function contextFromState(state: unknown): UserDiagnosticsContext | null {
  const candidate = (state as { diagnostics?: UserDiagnosticsContext } | null)?.diagnostics;
  return candidate && typeof candidate.upn === 'string' && candidate.upn.trim() ? candidate : null;
}

/**
 * Admin page: enter a user's UPN to see all of their data held in SQL - profile,
 * per-category record counts (with the SQL behind each), drill-down to the most recent rows,
 * and which import workloads are enabled - plus the licence and data diagnostics that explain
 * why the person shows as licensed or unlicensed and how fresh each kind of data is.
 */
export default function UserLookupPage() {
  const styles = useStyles();
  const t = useT();
  const tNode = useTNode();
  const location = useLocation();
  const navigate = useNavigate();
  const [upnInput, setUpnInput] = useState('');
  const [searchedUpn, setSearchedUpn] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [summaryMissing, setSummaryMissing] = useState(false);
  const [summary, setSummary] = useState<UserDataSummary | null>(null);
  const [diagnostics, setDiagnostics] = useState<UserDiagnostics | null>(null);
  const [diagnosticsError, setDiagnosticsError] = useState<string | null>(null);
  const [context, setContext] = useState<UserDiagnosticsContext | null>(null);
  const lookupId = useRef(0);

  const lookUp = async (rawUpn: string) => {
    const upn = rawUpn.trim();
    if (!upn) {
      return;
    }
    const id = ++lookupId.current;
    setLoading(true);
    setError(null);
    setSummary(null);
    setSummaryMissing(false);
    setDiagnostics(null);
    setDiagnosticsError(null);
    setSearchedUpn(upn);

    // Independent: a person the import never stored has no summary, and that is exactly when the
    // diagnostics matter most.
    const [summaryResult, diagnosticsResult] = await Promise.allSettled([fetchUserSummary(upn), fetchUserDiagnostics(upn)]);
    if (id !== lookupId.current) return;

    if (summaryResult.status === 'fulfilled') {
      setSummary(summaryResult.value);
    } else if (diagnosticsResult.status === 'fulfilled' && diagnosticsResult.value.storedUser === null) {
      setSummaryMissing(true);
    } else {
      const err = summaryResult.reason;
      setError(err instanceof Error ? err.message : t('admin.userLookup.page.lookupFailed'));
    }

    if (diagnosticsResult.status === 'fulfilled') {
      setDiagnostics(diagnosticsResult.value);
    } else {
      const err = diagnosticsResult.reason;
      setDiagnosticsError(err instanceof Error ? err.message : t('userDiagnostics.loadFailed'));
    }
    setLoading(false);
  };

  // After a one-person refresh: reload in place - no spinner, nothing cleared - so the refresh card keeps its result
  // on screen while the profile and licences beneath it update.
  const reloadAfterChange = async () => {
    const upn = searchedUpn;
    const id = lookupId.current;
    if (!upn) return;
    const [summaryResult, diagnosticsResult] = await Promise.allSettled([fetchUserSummary(upn), fetchUserDiagnostics(upn)]);
    if (id !== lookupId.current) return;
    if (summaryResult.status === 'fulfilled') setSummary(summaryResult.value);
    if (diagnosticsResult.status === 'fulfilled') setDiagnostics(diagnosticsResult.value);
  };

  // Arriving from "Troubleshoot this person": look them up straight away, keep the report context for the
  // verdict card, and drop it from history so a refresh or Back does not replay it.
  useEffect(() => {
    const incoming = contextFromState(location.state);
    if (!incoming) return;
    setContext(incoming);
    setUpnInput(incoming.upn);
    void lookUp(incoming.upn);
    navigate(location.pathname, { replace: true, state: null });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [location.state]);

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    await lookUp(upnInput);
  };

  return (
    <div>
      <Title3 block>{t('admin.userLookup.page.title')}</Title3>
      <Body1 block style={{ marginTop: '8px' }}>
        {tNode('admin.userLookup.page.description', {
          exampleUpn: <code>{t('admin.userLookup.page.exampleUpn')}</code>,
        })}
      </Body1>

      <form onSubmit={onSubmit} className={styles.form}>
        <Input
          className={styles.input}
          placeholder={t('admin.userLookup.page.upnPlaceholder')}
          value={upnInput}
          onChange={(_e: any, data: any) => setUpnInput(data.value)}
          aria-label={t('admin.userLookup.page.upnAriaLabel')}
        />
        <Button type="submit" appearance="primary" icon={<SearchRegular />} disabled={loading || !upnInput.trim()}>
          {t('admin.userLookup.page.lookupButton')}
        </Button>
      </form>

      {loading && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={80} label={t('admin.userLookup.page.loading')} />
        </div>
      )}
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {!loading && !error && summary === null && diagnostics === null && searchedUpn === '' && (
        <Text className={styles.muted}>{t('admin.userLookup.page.noUserLookedUp')}</Text>
      )}

      {!loading && (summary || diagnostics || diagnosticsError) && (
        <div className={styles.results}>
          {summaryMissing && (
            <MessageBar intent="warning">
              <MessageBarBody>{t('userDiagnostics.noStoredUserLookup')}</MessageBarBody>
            </MessageBar>
          )}
          {summary && <UserProfileCard profile={summary.profile} />}
          {diagnosticsError && (
            <MessageBar intent="error">
              <MessageBarBody>{diagnosticsError}</MessageBarBody>
            </MessageBar>
          )}
          {diagnostics && (
            <UserDiagnosticsPanel diagnostics={diagnostics} context={context} onDataChanged={() => void reloadAfterChange()} />
          )}
          {summary && (
            <CategoryTable
              upn={summary.profile.userPrincipalName || searchedUpn}
              categories={summary.categories}
              workloads={summary.workloads}
            />
          )}
        </div>
      )}
    </div>
  );
}
