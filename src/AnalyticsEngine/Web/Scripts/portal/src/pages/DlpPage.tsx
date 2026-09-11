import { Fragment, useEffect, useState } from 'react';
import {
  Title3,
  Body1,
  Text,
  Card,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  MessageBar,
  MessageBarBody,
  Button,
  Select,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular, ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons';
import { fetchDlpAvailability, fetchDlpSummary } from '../api/dlpApi';
import type { DlpAvailability, DlpImpactRow, DlpSummary } from '../types/dlp';
import Spinner from '../components/Spinner';

const WINDOWS = [
  { days: 7, label: 'Last 7 days' },
  { days: 28, label: 'Last 28 days' },
  { days: 90, label: 'Last 90 days' },
  { days: 180, label: 'Last 180 days' },
];

const useStyles = makeStyles({
  intro: { marginTop: '8px' },
  sectionTitle: { marginTop: '24px' },
  card: { marginTop: '12px', display: 'flex', flexDirection: 'column', gap: '4px' },
  muted: { color: tokens.colorNeutralForeground3 },
  kpiRow: { display: 'flex', gap: '12px', flexWrap: 'wrap', marginTop: '12px' },
  kpi: { flex: '1 1 160px', minWidth: '160px', padding: '12px', display: 'flex', flexDirection: 'column', gap: '2px' },
  kpiValue: { fontSize: tokens.fontSizeHero700, lineHeight: tokens.lineHeightHero700, fontWeight: tokens.fontWeightSemibold },
  blocked: { color: tokens.colorPaletteRedForeground1 },
  toolbar: { display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap' },
  spacer: { flexGrow: 1 },
  // Table cells inherit fontSizeBase300 from a bare value; pin the smaller size explicitly.
  td: { fontSize: tokens.fontSizeBase200 },
  bar: { display: 'flex', height: '8px', borderRadius: '4px', overflow: 'hidden', minWidth: '80px' },
  // Row expander: a real button so it is keyboard reachable and announces its expanded state.
  expander: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    background: 'none',
    border: 'none',
    padding: '0',
    cursor: 'pointer',
    font: 'inherit',
    color: 'inherit',
    textAlign: 'left',
  },
  nested: { paddingLeft: '20px', paddingTop: '4px', paddingBottom: '4px' },
});

function KpiCard({ label, value, hint, danger }: { label: string; value: number; hint?: string; danger?: boolean }) {
  const styles = useStyles();
  return (
    <Card className={styles.kpi}>
      <Text size={200} className={styles.muted}>
        {label}
      </Text>
      <span className={`${styles.kpiValue} ${danger ? styles.blocked : ''}`}>{value.toLocaleString()}</span>
      {hint && (
        <Text size={100} className={styles.muted}>
          {hint}
        </Text>
      )}
    </Card>
  );
}

/**
 * A ranked "top offenders" table. Blocked and audited are shown as separate columns on purpose:
 * a policy running in simulation ("Audit only") matches without withholding anything, and merging
 * the two would tell an admin their users are blocked when they are not.
 */
function ImpactTable({
  title,
  description,
  nameHeader,
  rows,
  showUsers,
  expandable,
}: {
  title: string;
  description: string;
  nameHeader: string;
  rows: DlpImpactRow[] | null | undefined;
  showUsers: boolean;
  /** Allow an agent row to expand into the policies that affected it. */
  expandable?: boolean;
}) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  // Defensive: a ranked list is only ever absent if the API contract has drifted. Rendering "nothing
  // in this period" beats taking the whole page down with a TypeError, which is exactly what an
  // unguarded .map() did when these fields were serialised under the wrong names.
  const safeRows = rows ?? [];
  const columnCount = 3 + (showUsers ? 1 : 0);

  const toggle = (key: string) =>
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  return (
    <Card className={styles.card}>
      <Text weight="semibold" size={400}>
        {title}
      </Text>
      <Text size={200} block className={styles.muted} style={{ marginBottom: '8px' }}>
        {description}
      </Text>
      {safeRows.length === 0 ? (
        <Text size={200} className={styles.muted}>
          Nothing in this period.
        </Text>
      ) : (
        <Table size="small" aria-label={title}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{nameHeader}</TableHeaderCell>
              <TableHeaderCell style={{ width: 110 }}>Blocked</TableHeaderCell>
              <TableHeaderCell style={{ width: 110 }}>Audited only</TableHeaderCell>
              {showUsers && <TableHeaderCell style={{ width: 110 }}>Users</TableHeaderCell>}
            </TableRow>
          </TableHeader>
          <TableBody>
            {safeRows.map((r, i) => {
              const key = r.id ?? `${r.name}-${i}`;
              const policies = r.policies ?? [];
              const canExpand = expandable === true && policies.length > 0;
              const isOpen = expanded.has(key);

              return (
                <Fragment key={key}>
                  <TableRow>
                    <TableCell className={styles.td}>
                      {canExpand ? (
                        <button
                          type="button"
                          className={styles.expander}
                          aria-expanded={isOpen}
                          onClick={() => toggle(key)}
                        >
                          {isOpen ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
                          <span>{r.name ?? '—'}</span>
                        </button>
                      ) : (
                        (r.name ?? '—')
                      )}
                    </TableCell>
                    <TableCell className={`${styles.td} ${r.blockedCount > 0 ? styles.blocked : ''}`}>
                      {r.blockedCount.toLocaleString()}
                    </TableCell>
                    <TableCell className={styles.td}>{r.auditedCount.toLocaleString()}</TableCell>
                    {showUsers && <TableCell className={styles.td}>{(r.usersAffected ?? 0).toLocaleString()}</TableCell>}
                  </TableRow>

                  {canExpand && isOpen && (
                    <TableRow>
                      <TableCell className={styles.td} colSpan={columnCount}>
                        <div className={styles.nested}>
                          <Text size={200} className={styles.muted} block style={{ marginBottom: '4px' }}>
                            Policies affecting {r.name ?? 'this agent'}
                          </Text>
                          <Table size="extra-small" aria-label={`Policies affecting ${r.name ?? 'this agent'}`}>
                            <TableHeader>
                              <TableRow>
                                <TableHeaderCell>Policy</TableHeaderCell>
                                <TableHeaderCell style={{ width: 110 }}>Blocked</TableHeaderCell>
                                <TableHeaderCell style={{ width: 110 }}>Audited only</TableHeaderCell>
                              </TableRow>
                            </TableHeader>
                            <TableBody>
                              {policies.map((p, pi) => (
                                <TableRow key={p.id ?? `${p.name}-${pi}`}>
                                  <TableCell className={styles.td}>{p.name ?? '—'}</TableCell>
                                  <TableCell className={`${styles.td} ${p.blockedCount > 0 ? styles.blocked : ''}`}>
                                    {p.blockedCount.toLocaleString()}
                                  </TableCell>
                                  <TableCell className={styles.td}>{p.auditedCount.toLocaleString()}</TableCell>
                                </TableRow>
                              ))}
                            </TableBody>
                          </Table>
                        </div>
                      </TableCell>
                    </TableRow>
                  )}
                </Fragment>
              );
            })}
          </TableBody>
        </Table>
      )}
    </Card>
  );
}

/**
 * Insights page: which Copilot actions and agents are being blocked by Microsoft Purview DLP
 * policies, how often, and who is most affected.
 *
 * The per-agent figures come from the DLP detail Microsoft embeds in each Copilot interaction
 * record, which is the only place a Copilot DLP block can be tied to a specific agent. The
 * tenant-wide DLP.All figures are shown in their own section because those records carry no agent
 * identity at all - presenting them together would imply an attribution the audit data cannot
 * support.
 */
export default function DlpPage() {
  const styles = useStyles();

  const [days, setDays] = useState(28);
  const [reloadKey, setReloadKey] = useState(0);

  const [availability, setAvailability] = useState<DlpAvailability | null>(null);
  const [summary, setSummary] = useState<DlpSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    fetchDlpAvailability()
      .then(async (a) => {
        if (cancelled) return;
        setAvailability(a);
        // Still fetch when only one source is on - the other simply reports zero.
        if (a.available) {
          const s = await fetchDlpSummary(days);
          if (!cancelled) setSummary(s);
        } else {
          setSummary(null);
        }
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load DLP data.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [days, reloadKey]);

  const totalCopilot = (summary?.copilotBlockedCount ?? 0) + (summary?.copilotAuditedCount ?? 0);

  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap' }}>
        <Title3>DLP impact on Copilot</Title3>
        <div className={styles.toolbar}>
          <Select value={String(days)} onChange={(_e, data) => setDays(Number(data.value))} aria-label="Reporting period">
            {WINDOWS.map((w) => (
              <option key={w.days} value={w.days}>
                {w.label}
              </option>
            ))}
          </Select>
          <Button
            appearance="subtle"
            icon={<ArrowClockwise16Regular />}
            onClick={() => setReloadKey((k) => k + 1)}
            disabled={loading}
          >
            Refresh
          </Button>
        </div>
      </div>

      <Body1 block className={styles.intro}>
        Where Microsoft Purview Data Loss Prevention policies stopped Microsoft 365 Copilot from using
        content — which agents and people are most affected, and which policies are responsible.
      </Body1>

      {error && (
        <MessageBar intent="error" style={{ marginTop: '12px' }}>
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {(availability?.reasons ?? []).map((r) => (
        <MessageBar key={r} intent={availability?.available ? 'info' : 'warning'} style={{ marginTop: '12px' }}>
          <MessageBarBody>{r}</MessageBarBody>
        </MessageBar>
      ))}

      {loading && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={64} label="Loading DLP data..." />
        </div>
      )}

      {!loading && summary && (
        <>
          <div className={styles.kpiRow}>
            <KpiCard
              label="Blocked"
              value={summary.copilotBlockedCount}
              hint="Copilot was denied content"
              danger
            />
            <KpiCard
              label="Audited only"
              value={summary.copilotAuditedCount}
              hint="Policy matched, nothing withheld"
            />
            <KpiCard label="People affected" value={summary.usersImpacted} hint="Distinct users blocked" />
            <KpiCard label="Agents affected" value={summary.agentsImpacted} hint="Distinct agents blocked" />
            <KpiCard label="Policies involved" value={summary.policiesInvolved} hint="Distinct DLP policies" />
          </div>

          {totalCopilot === 0 && (
            <MessageBar intent="success" style={{ marginTop: '12px' }}>
              <MessageBarBody>
                No DLP policy affected Copilot in this period. If you expected activity, remember that a
                policy change can take up to four hours to reach Copilot, and that the policy must target
                the &quot;Microsoft 365 Copilot and Copilot Chat&quot; location.
              </MessageBarBody>
            </MessageBar>
          )}

          <Text className={styles.sectionTitle} weight="semibold" size={500} block>
            Who and what is affected
          </Text>

          <ImpactTable
            title="Agents"
            description="Copilot agents whose access to content was affected by a DLP policy. This is the only view that can attribute a DLP block to a specific agent. Select an agent to see which policies affected it."
            nameHeader="Agent"
            rows={summary.topAgents}
            showUsers
            expandable
          />
          <ImpactTable
            title="People"
            description="Users whose Copilot requests were affected most often. Select a person to see which policies affected them."
            nameHeader="User"
            rows={summary.topUsers}
            showUsers={false}
            expandable
          />
          <ImpactTable
            title="Policies"
            description="The DLP policies responsible. A policy with blocks in the 'Audited only' column is matching without withholding anything - typically because its rules are in simulation mode."
            nameHeader="Policy"
            rows={summary.topPolicies}
            showUsers
          />
          <ImpactTable
            title="Sensitivity labels"
            description="Labels on the content Copilot was stopped from using. The most common Copilot DLP policy shape is 'prevent Copilot processing content with label X', so this is usually the explanation."
            nameHeader="Sensitivity label"
            rows={summary.topSensitivityLabels}
            showUsers
          />

          <Text className={styles.sectionTitle} weight="semibold" size={500} block>
            Tenant-wide DLP activity
          </Text>
          <Body1 block className={styles.muted} style={{ marginTop: '4px' }}>
            DLP policy activity across Exchange, SharePoint/OneDrive and endpoint devices, from the
            separate DLP audit feed. These records identify the person but never the Copilot agent, so
            they are reported separately and are not combined with the Copilot figures above.
          </Body1>

          {!availability?.tenantDlpAvailable ? (
            <Text size={200} className={styles.muted} block style={{ marginTop: '8px' }}>
              The DLP import is switched off, so there is nothing to show here.
            </Text>
          ) : (
            <>
              <div className={styles.kpiRow}>
                <KpiCard label="Blocked" value={summary.tenantBlockedCount} hint="Across all workloads" danger />
                <KpiCard label="Audited only" value={summary.tenantAuditedCount} hint="Matched, not enforced" />
              </div>
              <ImpactTable
                title="Policies (tenant-wide)"
                description="Policies firing across the tenant, from the DLP audit feed."
                nameHeader="Policy"
                rows={summary.tenantTopPolicies}
                showUsers={false}
              />
            </>
          )}
        </>
      )}
    </div>
  );
}
