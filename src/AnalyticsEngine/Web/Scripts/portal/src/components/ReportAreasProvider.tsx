import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { fetchReportAreas } from '../api/reportsApi';
import { useT } from '../i18n';
import { translateActive } from '../i18n/runtime';
import type { ReportAreas } from '../types/reports';

interface ReportAreasState {
  areas: ReportAreas | null;
  loading: boolean;
  error: string | null;
  retry: () => void;
}

const ReportAreasContext = createContext<ReportAreasState | null>(null);

function OwnedReportAreasProvider({ children, enabled }: { children: ReactNode; enabled: boolean }) {
  const t = useT();
  const [areas, setAreas] = useState<ReportAreas | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!enabled) return;
    let live = true;
    setLoading(true);
    setAreas(null);
    setError(null);
    fetchReportAreas()
      .then(value => { if (live) setAreas(value); })
      .catch(e => { if (live) setError(e instanceof Error ? e.message : t('reports.error.loadAreas')); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [enabled, attempt, t]);

  return (
    <ReportAreasContext.Provider value={{ areas, loading, error, retry: () => setAttempt(value => value + 1) }}>
      {children}
    </ReportAreasContext.Provider>
  );
}

/** Share import availability between the rail and the selected report, without fetching charts. */
export default function ReportAreasProvider({ children, enabled = true }: { children: ReactNode; enabled?: boolean }) {
  const existing = useContext(ReportAreasContext);
  return existing ? children : <OwnedReportAreasProvider enabled={enabled}>{children}</OwnedReportAreasProvider>;
}

export function useReportAreas(): ReportAreasState {
  const value = useContext(ReportAreasContext);
  if (!value) throw new Error(translateActive('reports.error.providerMissing'));
  return value;
}
