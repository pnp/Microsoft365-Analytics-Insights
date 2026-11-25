# Search admission and logical visits: synthetic SQL measurements

These measurements compare the search merge and web-activity reports with their immediately preceding implementation (`f55cc6e0`). They are not customer telemetry or an estimate of portal latency. No permanent table, index, migration or history rewrite is required.

## What was measured

- SQL Server 2025 LocalDB, private disposable databases. Each case discards a warm-up, alternates the compared statements, reports the median of three measured executions, and captures a separate **actual execution plan**. Each cell below is **total logical reads / SQL elapsed milliseconds**, including worktables. SQL time is not wall time for an entire ingestion cycle or portal request.
- **Merge:** 200,000 synthetic users, 202,000 physical sessions and 200,000 search facts. One percent of users have a duplicate session with the same tracker ID. Mixed batches spread old and new events across the history; hot replays share one term and sixty timestamps; missing batches cannot match a session. Both sides use the same staged inputs. Transactions are rolled back between runs and only the benchmark database's plan cache is cleared. Row-by-row staging is not timed; the new eligibility lookup is measured separately.
- **Admission lookup and reports:** 200,000 users, 202,000 sessions, 200,000 searches from 50,000 users, 2,000,000 hits over 365 days. Hit ownership uses the existing unique request-ID index. Reports use the existing timestamp (including session ID), session and foreign-key indexes; there is no search timestamp index. `scope` is the 28-day window with 2,000 explicitly included user IDs, using the production scope SQL and JSON parameter.
- **Report comparison:** the old Search tab requested `SearchKpis` and a separate `VisitKpis` denominator; the new query returns the logical-hit-visit denominator itself. The Search tab row measures both old statements **serially** to show their combined work, not their parallel UI latency. The separate **KPI alone** row compares what the Overview caller pays. Report statements use `OPTION (RECOMPILE)` at each selectivity.

## Search fact merge

| Batch contents | Rows | Before | Unfiltered | Verified owner |
|---|---:|---:|---:|---:|
| Mixed | 1 | 26 / 1 | 26 / 1 | 26 / 1 |
| Mixed | 1,000 | 9,672 / 112 | 9,671 / 111 | 9,670 / 129 |
| Mixed | 10,000 | 73,505 / 193 | 73,488 / 180 | 73,471 / 193 |
| Hot replay | 1 | 26 / 5 | 26 / 8 | 26 / 2 |
| Hot replay | 1,000 | 2,645 / 119 | 2,645 / 170 | 2,645 / 175 |
| Hot replay | 10,000 | 3,220 / 132 | 3,220 / 118 | 3,220 / 169 |
| Missing | 1 | 11 / 0 | 11 / 0 | 11 / 0 |
| Missing | 1,000 | 3,283 / 1 | 3,283 / 1 | 4,289 / 14 |
| Missing | 10,000 | 32,515 / 12 | 32,515 / 17 | 2,460 / 73 |

The unfiltered single row seeks the tracker and fact session indexes; broad mixed/hot batches scan the fact table once. At 10,000 hot replays, a rejected optional-owner `OR` did 33,153 reads when unfiltered instead of 3,220. Separate, fixed predicates preserve the unfiltered plan. Verification does have a cost: the hot and missing cases above were sometimes slower even with the same or fewer reads. At 10,000 verified misses SQL scanned the session and user tables but did not read the fact table. This is a defensive stress case: a restricted search with no matching **request** stops before the merge.

## Site admission

There was no page-request verification cost before this change. These are the **additional** server costs of the single set-based lookup, not the cost of inserting a search. It returns ownership and stored site/page evidence; authorization is evaluated in process. The configuration snapshot and its commit-time comparison, measured with one rule, took 4 reads / 1 ms.

| Request IDs | Batch | Hits returned | Reads / ms |
|---|---:|---:|---:|
| Present | 1 | 1 | 22 / 0 |
| Present | 1,000 | 1,000 | 18,252 / 18 |
| Present | 10,000 | 10,000 | 182,500 / 185 |
| Half present | 1 | 1 | 18 / 0 |
| Half present | 1,000 | 500 | 10,656 / 6 |
| Half present | 10,000 | 5,000 | 106,561 / 106 |
| Absent | 1 | 0 | 3 / 0 |
| Absent | 1,000 | 0 | 3,062 / 2 |
| Absent | 10,000 | 0 | 30,625 / 35 |

All sizes used `IX_PageRequestID` seeks. Confirmed hits required additional seeks for session, user and location evidence. For a wholly absent input the hit lookups returned no rows; there was no scan of historical hits.

## Web-activity reports

The denominator is the number of logical visits with a hit in the window. Search facts keep their original dates and physical session IDs. A missing tracker/user identity falls back to its physical session; an anonymous hit with no session does not become a visit. Both hit and search inputs retain the report's user scope and `[from, to)` bounds. A search cannot acquire a following hit from outside the window or a site from another person's session.

| Window | Statement | Before | After |
|---|---|---:|---:|
| 1 hour | Search tab KPI + denominator | 5,632 / 70 | 1,694 / 24 |
| 1 hour | KPI alone | 4,890 / 61 | 1,694 / 25 |
| 1 hour | Search terms | 2,041 / 28 | 965 / 13 |
| 1 hour | Dead-end terms | 1,225 / 20 | 889 / 14 |
| 1 hour | Searches by site | 1,494 / 11 | 3,114 / 38 |
| 28 days | Search tab KPI + denominator | 15,911 / 1,215 | 4,464 / 281 |
| 28 days | KPI alone | 10,476 / 392 | 4,464 / 215 |
| 28 days | Search terms | 338,889 / 471 | 2,847 / 230 |
| 28 days | Dead-end terms | 329,970 / 182 | 2,834 / 233 |
| 28 days | Searches by site | 6,019,768 / 2,315 | 29,028 / 520 |
| 365 days | Search tab KPI + denominator | 45,374 / 1,231 | 10,373 / 1,503 |
| 365 days | KPI alone | 10,476 / 514 | 10,373 / 1,651 |
| 365 days | Search terms | 6,619,823 / 2,301 | 7,877 / 701 |
| 365 days | Dead-end terms | 6,571,242 / 2,476 | 7,864 / 759 |
| 365 days | Searches by site | 6,577,946 / 5,642 | 29,028 / 3,779 |
| 28 days, scoped | Search tab KPI + denominator | 79,208 / 825 | 27,131 / 103 |
| 28 days, scoped | KPI alone | 50,364 / 85 | 27,131 / 77 |
| 28 days, scoped | Search terms | 21,858 / 45 | 19,671 / 94 |
| 28 days, scoped | Dead-end terms | 14,241 / 33 | 19,347 / 77 |
| 28 days, scoped | Searches by site | 15,909 / 29 | 35,425 / 149 |

The 28-day term plans replaced repeated physical-session hit probes with a bounded aggregate using `IX_hits_hit_timestamp`. The KPI reads that aggregate once into a query-local temporary table. A previously tested one-pass full join also halved hit-index reads, but used **555,635 total reads** over the year; it was not shipped. The site query maps searched visits to their equivalent physical sessions and excludes hits after each visit's last search. Its broad plan scanned the clustered hit index; the scoped plan used `IX_FK_hits_sessions` seeks and hit lookups.

**Slower cases matter.** The annual KPI alone took 1,651 ms instead of 514 ms despite similar reads; even including the former denominator query, measured SQL time increased. The 28-day dead-end ranking was also slower despite substantially fewer reads. The hour-wide site query and all three scoped term/site lists were slower; the scoped dead-end and site lists also did more total reads. Conversely, annual term ranking and site attribution cut both reads and elapsed time. These are trade-offs of scoring the correct logical visit, not a promise that every request will be faster.

## Follow-up: count historical searches once and measure reliance among hit visits

The Search tab and Overview now count one fact per comparable user/tracker visit, term ID and **full stored timestamp**. A repeat at another timestamp still counts. Facts without a comparable identity stay separate. Search-only visits still contribute to search totals, even when their page views fall outside the selected period. Reliance alone divides the number of hit visits that also searched by the number of hit visits. Neither change rewrites history.

Admission uses the tracker's collection boundary for stored site/page evidence: a root collection does not own a page in a named `/sites/` or `/teams/` collection. This does not change the meaning of a configured URL prefix. Unicode path letters compare case-insensitively whether supplied directly or percent-encoded; a percent-encoded separator does not become a path boundary for a positive match. The SQL admission lookup is unchanged.

For this comparison the baseline is `079bc162`, **not** the original baseline above. The same private LocalDB report harness added 8,000 duplicate facts across equivalent physical sessions to the 200,000 original synthetic search facts. Actual report SQL ran against identical data, with a discarded warm-up, alternating versions, three measured runs and separate actual plans. Values are median **total logical reads / SQL elapsed ms**, not portal latency; the VM was also running local validation.

| Statement | 1 hour: before → after | 365 days: before → after |
|---|---:|---:|
| Search KPIs | 1,800 / 32 → 1,702 / 24 | 10,425 / 1,707 → 112,382 / 2,175 |
| Search terms | 1,024 / 12 → 1,016 / 18 | 7,902 / 717 → 7,902 / 905 |
| Dead-end terms | 940 / 18 → 940 / 16 | 7,889 / 785 → 7,889 / 1,747 |
| Searches by site | 3,283 / 47 → 2,825 / 73 | 29,107 / 2,477 → 29,107 / 2,749 |
| Searches by day/hour | 750 / 13 → 844 / 10 | 750 / 63 → 1,590 / 576 |
| Weekly overview/search trend | 1,727 / 30 → 1,915 / 29 | 24,704 / 3,062 → 26,384 / 2,936 |

| Statement | 28 days: before → after | Same window, 2,000-user scope: before → after |
|---|---:|---:|
| Search KPIs | 4,516 / 243 → 3,676 / 176 | 27,222 / 85 → 26,554 / 75 |
| Search terms | 2,872 / 256 → 2,872 / 188 | 19,719 / 96 → 19,711 / 78 |
| Dead-end terms | 2,859 / 181 → 2,859 / 146 | 19,387 / 107 → 19,387 / 77 |
| Searches by site | 29,107 / 504 → 29,147 / 485 | 35,549 / 90 → 35,549 / 102 |
| Searches by day/hour | 750 / 14 → 1,590 / 80 | 11,185 / 20 → 11,635 / 19 |
| Weekly overview/search trend | 4,584 / 241 → 6,264 / 355 | 123,976 / 274 → 48,325 / 201 |

This is a correctness change, not a speedup. In particular, the annual KPI did over ten times as many total reads, although both plans still sought the timestamp index and recorded 5,452 reads against hits. The annual site report still scanned hits and sessions; the annual day/hour report now also scans sessions to identify duplicate facts. No schema or index change was tested or introduced.
