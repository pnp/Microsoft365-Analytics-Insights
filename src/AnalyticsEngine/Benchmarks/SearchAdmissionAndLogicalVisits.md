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
