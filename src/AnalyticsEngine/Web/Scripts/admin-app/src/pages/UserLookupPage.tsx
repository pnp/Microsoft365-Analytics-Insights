import { useState } from 'react';
import type { FormEvent } from 'react';
import { fetchUserSummary } from '../api/userLookupApi';
import type { UserDataSummary } from '../types/userData';
import Spinner from '../components/Spinner';
import UserProfileCard from '../components/userlookup/UserProfileCard';
import CategoryTable from '../components/userlookup/CategoryTable';

/**
 * Admin page: enter a user's UPN to see all of their data held in SQL - profile,
 * per-category record counts, and drill-down to the most recent rows per category.
 */
export default function UserLookupPage() {
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
      <h5 className="card-header text-center">User Data Lookup</h5>
      <br />
      <p>
        Enter a user's UPN (user principal name, e.g. <code>jane.doe@contoso.com</code>) to see all of the data held
        for them in the analytics database.
      </p>

      <form onSubmit={onSubmit} className="form-inline" style={{ marginBottom: '1.5rem' }}>
        <input
          type="text"
          className="form-control"
          style={{ minWidth: 320, marginRight: '0.5rem' }}
          placeholder="user@contoso.com"
          value={upnInput}
          onChange={(e) => setUpnInput(e.target.value)}
          aria-label="User principal name"
        />
        <button type="submit" className="btn btn-primary" disabled={loading || !upnInput.trim()}>
          Look up
        </button>
      </form>

      {loading && (
        <div className="text-center">
          <Spinner size={80} />
        </div>
      )}
      {error && <p className="aa-error">Error: {error}</p>}

      {!loading && !error && summary === null && searchedUpn === '' && (
        <p className="aa-muted">No user looked up yet.</p>
      )}

      {!loading && summary && (
        <div>
          <UserProfileCard profile={summary.profile} />
          <CategoryTable upn={summary.profile.userPrincipalName || searchedUpn} categories={summary.categories} />
        </div>
      )}
    </div>
  );
}
