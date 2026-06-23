import { useState } from 'react';
import type { UserDataCategory, UserDataDetailRow } from '../../types/userData';
import { fetchUserDetail } from '../../api/userLookupApi';
import Spinner from '../Spinner';

type CategoryRowProps = {
  upn: string;
  category: UserDataCategory;
};

/** A single category row that can expand to lazily load & show its most recent rows. */
export default function CategoryRow({ upn, category }: CategoryRowProps) {
  const [expanded, setExpanded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [rows, setRows] = useState<UserDataDetailRow[] | null>(null);
  const [totalCount, setTotalCount] = useState<number | null>(null);

  const canDrill = category.supportsDetail && category.count > 0;

  const toggle = async () => {
    if (expanded) {
      setExpanded(false);
      return;
    }
    setExpanded(true);

    if (rows === null && !loading) {
      setLoading(true);
      setError(null);
      try {
        const resp = await fetchUserDetail(upn, category.key, 50);
        setRows(resp.rows);
        setTotalCount(resp.totalCount);
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Failed to load detail.');
      } finally {
        setLoading(false);
      }
    }
  };

  return (
    <>
      <tr>
        <td>
          {category.label}
          <div className="aa-muted" style={{ fontSize: '0.85em' }}>
            {category.description}
          </div>
        </td>
        <td style={{ textAlign: 'right' }}>{category.count.toLocaleString()}</td>
        <td style={{ textAlign: 'right' }}>
          {canDrill ? (
            <button type="button" className="btn btn-sm btn-outline-primary" onClick={toggle}>
              {expanded ? 'Hide' : 'View recent'}
            </button>
          ) : (
            <span className="aa-muted">—</span>
          )}
        </td>
      </tr>
      {expanded && (
        <tr className="aa-detail-row">
          <td colSpan={3}>
            {loading && <Spinner size={24} />}
            {error && <div className="aa-error">{error}</div>}
            {rows &&
              (rows.length === 0 ? (
                <div className="aa-muted">No rows.</div>
              ) : (
                <>
                  <div className="aa-muted" style={{ marginBottom: '0.5rem' }}>
                    Showing {rows.length} most recent of {(totalCount ?? category.count).toLocaleString()}.
                  </div>
                  <table className="table table-sm">
                    <thead>
                      <tr>
                        <th style={{ width: 200 }}>When</th>
                        <th>Detail</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((r, i) => (
                        <tr key={i}>
                          <td>{r.timestamp ? new Date(r.timestamp).toLocaleString() : '—'}</td>
                          <td>
                            {r.title ? <strong>{r.title}</strong> : null}
                            {r.title && r.detail ? ' — ' : ''}
                            {r.detail}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </>
              ))}
          </td>
        </tr>
      )}
    </>
  );
}
