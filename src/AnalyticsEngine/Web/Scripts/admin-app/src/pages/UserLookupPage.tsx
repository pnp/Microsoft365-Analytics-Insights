import { useState } from 'react';
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
import { fetchUserSummary } from '../api/userLookupApi';
import type { UserDataSummary } from '../types/userData';
import Spinner from '../components/Spinner';
import UserProfileCard from '../components/userlookup/UserProfileCard';
import CategoryTable from '../components/userlookup/CategoryTable';

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

/**
 * Admin page: enter a user's UPN to see all of their data held in SQL - profile,
 * per-category record counts (with the SQL behind each), drill-down to the most recent rows,
 * and which import workloads are enabled.
 */
export default function UserLookupPage() {
  const styles = useStyles();
  const [upnInput, setUpnInput] = useState('');
  const [searchedUpn, setSearchedUpn] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [summary, setSummary] = useState<UserDataSummary | null>(null);

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    const upn = upnInput.trim();
    if (!upn) {
      return;
    }
    setLoading(true);
    setError(null);
    setSummary(null);
    setSearchedUpn(upn);
    try {
      const result = await fetchUserSummary(upn);
      setSummary(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Lookup failed.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div>
      <Title3 block>User Data Lookup</Title3>
      <Body1 block style={{ marginTop: '8px' }}>
        Enter a user's UPN (user principal name, e.g. <code>jane.doe@contoso.com</code>) to see all of the data held
        for them in the analytics database.
      </Body1>

      <form onSubmit={onSubmit} className={styles.form}>
        <Input
          className={styles.input}
          placeholder="user@contoso.com"
          value={upnInput}
          onChange={(_e, data) => setUpnInput(data.value)}
          aria-label="User principal name"
        />
        <Button type="submit" appearance="primary" icon={<SearchRegular />} disabled={loading || !upnInput.trim()}>
          Look up
        </Button>
      </form>

      {loading && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={80} label="Looking up user data..." />
        </div>
      )}
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {!loading && !error && summary === null && searchedUpn === '' && (
        <Text className={styles.muted}>No user looked up yet.</Text>
      )}

      {!loading && summary && (
        <div className={styles.results}>
          <UserProfileCard profile={summary.profile} />
          <CategoryTable
            upn={summary.profile.userPrincipalName || searchedUpn}
            categories={summary.categories}
            workloads={summary.workloads}
          />
        </div>
      )}
    </div>
  );
}
