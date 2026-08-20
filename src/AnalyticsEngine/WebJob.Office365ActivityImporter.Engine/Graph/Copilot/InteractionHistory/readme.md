# Copilot AI Interaction History Import

Optional, opt-in import of Microsoft 365 Copilot interaction history via Microsoft Graph:

```
GET /copilot/users/{userId}/interactionHistory/getAllEnterpriseInteractions
```

## Why this exists

We already have two Copilot data sources, and neither can describe the *shape* of a conversation:

| Source | What it gives us | What it can't tell us |
|---|---|---|
| Audit.General feed (`copilot_chats`) | Accessed resources, agent, app host, opaque message sizes | Which prompt produced which response; how long Copilot took |
| Graph Copilot usage reports | Per-user prompt **counts** aggregated over 7/28/90/180 days | Anything per-turn; anything intraday |

The interaction-history endpoint adds turn-level structure. The single most valuable field is `requestId`,
which pairs a user prompt with the Copilot response it produced - that is what makes **response latency**,
true turn counts and prompt-to-response ratios possible at all.

`sessionId` is the same identifier the audit feed stores as `copilot_chats.thread_id`, so interaction shape
joins back to the audit-derived accessed resources for the same conversation.

## What we store - and what we deliberately don't

**The Graph payload contains the user's literal prompt and Copilot's literal answer.** That is the most
sensitive data this product can see, so the importer reads each body, derives statistics from it, and throws
it away. There is no column anywhere in this feature capable of holding a message body.

Stored for every interaction (no cognitive services needed):

| Column | Source | New? |
|---|---|---|
| `request_id` | `requestId` | Yes - enables turn pairing |
| `response_latency_ms` | derived from the prompt/response pair | Yes |
| `conversation_type_id` | `conversationType` (`appchat` / `bizchat`) | Yes |
| `app_class_id` | `appClass`, e.g. `...Copilot.Excel` | Finer than the audit feed's `app_host` |
| `device_id` | `from.device` (desktop / web / mobile) | Yes |
| `locale_id` | `locale` | Yes |
| `body_char_count`, `body_word_count` | measured from the body, then discarded | Yes - audit only has an opaque size |
| `attachment_count`, `link_count`, `mention_count`, `context_count` | collection lengths | Yes |
| `interaction_type_id` | `userPrompt` / `aiResponse` | Authoritative per turn |
| `created_utc` | `createdDateTime` | Per-turn timestamps |

Stored **only when cognitive services are configured**, and **only for user prompts**:

| Column | Notes |
|---|---|
| `sentiment_score` | Positive-sentiment confidence 0.0-1.0, same scale as the sent-email import |
| `language_id` | Detected language, in the shared `languages` lookup |
| `copilot_interaction_keywords` | Extracted key phrases, in the shared `keywords` lookup |

Copilot responses are never scored: they are model output rather than a signal about the user, they are much
longer than prompts (so they would dominate the per-character cognitive bill), and the question this feature
answers is "what were our people asking for, and how did they feel about it".

## The cost problem, and the four brakes

`getAllEnterpriseInteractions` has **no tenant-wide form and no delta form**. It is one HTTP call per user.
At this product's stated design target of ~200,000 users that would be 200,000 Graph calls per import cycle,
which is a non-starter as an always-on import.

Four independent brakes apply, in order:

1. **The feature toggle.** `ImportTaskSettings.CopilotInteractionHistory`, off by default.
2. **Group scope.** `UserGroupsFilter` must be set - the import **refuses to run** without it unless
   `CopilotInteractionHistoryAllowUnscoped=true` is set explicitly. Scope is resolved **group-first**: the
   matching Entra ID groups are listed once and their members paged, then intersected with the users table.
   The obvious alternative - asking "is this user in the pilot group?" for each user - is one Graph call per
   *tenant* user, so it would spend 200,000 calls just deciding who to import. Group-first is
   O(groups + pilot members) instead, and it also pages membership properly rather than reading only the
   first page of a user's `memberOf`.
3. **A per-cycle ceiling.** `CopilotInteractionHistoryMaxUsersPerCycle` (default 500). Users are taken
   least-recently-run first, so a pilot group larger than the cap is still fully covered - just round-robin
   over consecutive cycles.
4. **A per-user back-off.** Users who return nothing twice in a row (almost always because they lack the
   `M365_COPILOT_BUSINESS_CHAT` service plan) are skipped for `CopilotInteractionHistoryEmptyUserBackOffHours`
   (default 72). The back-off always expires, so a newly-licensed user is picked up again.

On top of that the import is **incremental**: each user has a watermark, so a steady-state cycle only asks for
interactions created since the last successful run. The whole section is additionally cadence-gated by
`CopilotInteractionHistoryIntervalHours` (default 24), so it runs at most daily. A single user is also capped
at 50 pages per cycle, so one very heavy user cannot consume the whole run.

### How the watermark moves, and why it matters

After a **complete** read of a window, the watermark advances to the **end of the queried window** - not to
the newest interaction returned. That distinction is load-bearing. The next window starts slightly *before*
the watermark (see below), so using the newest interaction would hand that same interaction back every cycle;
it would look like a fresh non-empty success, rewrite the identical watermark, and the user would be queried
for ever without ever reaching the empty back-off - a standing cost that returns no new data.

The watermark is **not** advanced when a read was incomplete:

| Outcome | Watermark | Rationale |
|---|---|---|
| Complete read | end of window | Everything up to that instant has been seen |
| Page failure / HTTP error | unchanged | The window is retried in full; advancing would skip unread data |
| Stopped at the page cap | newest interaction actually read | The rest resumes next cycle |
| User not available (no licence) | unchanged | Nothing was read; back-off applies |

### Why the query window overlaps

Graph only supports `$filter` on `createdDateTime` as a **range**, and the comparison is a strict `gt`. If the
window started exactly on the watermark, a second interaction created in the same second would be skipped
forever. The window therefore starts `WatermarkOverlapSeconds` (5 minutes) before the watermark, which also
acts as a safety lag for late-arriving interactions and clock skew between the producing services.

Anything re-read by that overlap is discarded by an existence check on
`(session_id, graph_interaction_id)` **before** it reaches cognitive services or the database - so the overlap
costs neither a duplicate row nor a repeat Azure AI Language charge, and the prompt is not sent out again.

### Paging is all-or-nothing

This importer does not use the shared paging helper, which swallows a mid-paging HTTP failure and returns the
pages it managed to collect. For most importers that is the right behaviour. Here it would be silent data
loss: a truncated list would look like a complete window and the watermark would move past interactions that
were never read. Any page failure fails the whole user instead, leaving the watermark untouched.

The same loop also exists so that **the response body is never logged**. The shared client logs the raw body
on an HTTP or deserialisation error; for this endpoint that body is a page of real prompts and answers. Errors
here are reduced to a status code, Graph error code and page number before they are logged or written to
`copilot_interaction_user_watermarks.last_error`.

## Required Graph permission

| API | Permission | Type | Granted by the installer? |
|---|---|---|---|
| Graph | `AiEnterpriseInteraction.Read.All` | **Application** | **No** - needs explicit admin consent |

There is no delegated form of this permission. Because it is not part of the base consent, "the admin hasn't
consented yet" is the single most likely reason for this import to do nothing, so the importer checks the
access token's `roles` claim up front and logs one clear warning rather than emitting a 403 per user, every
cycle.

Data is only returned for users licensed with the **`M365_COPILOT_BUSINESS_CHAT`** service plan.
**Copilot Studio agents are excluded by the API itself** - those conversations will never appear here.

## Configuration

| Setting | Default | Purpose |
|---|---|---|
| `ImportJobSettings` -> `CopilotInteractionHistory` | off | Master toggle (installer checkbox) |
| `UserGroupsFilter` | none | Entra ID group display names, `;`-separated, `*` wildcards. **Required.** |
| `CopilotInteractionHistoryAllowUnscoped` | `false` | Permits running with no group filter. Understand the cost first. |
| `CopilotInteractionHistoryMaxUsersPerCycle` | `500` | Hard ceiling on Graph calls per cycle |
| `CopilotInteractionHistoryIntervalHours` | `24` | Minimum gap between runs; 0 = every cycle |
| `CopilotInteractionHistoryMaxDaysBackOnFirstRun` | `30` | Backfill window for a newly in-scope user |
| `CopilotInteractionHistoryEmptyUserBackOffHours` | `72` | Back-off for users returning nothing; 0 disables |
| `CognitiveEndpoint` / `CognitiveKey` | none | Enables sentiment, language and key phrases. Optional. |

## Data model

| Table | Contents |
|---|---|
| `copilot_interactions` | One row per turn. Statistics only. |
| `copilot_interaction_sessions` | One row per Copilot thread **per user**; `session_ref` joins to `copilot_chats.thread_id` |
| `copilot_interaction_app_classes` / `_conversation_types` / `_types` / `_locales` / `_devices` | Lookups |
| `copilot_interaction_keywords` | Key phrases per prompt, linked to the shared `keywords` table |
| `copilot_interaction_user_watermarks` | Per-user incremental state and back-off list |
| `copilot_interaction_import_log` | Per-run diagnostics: users in scope / called / skipped / failed, rows read and saved, prompts scored |

Two index choices are deliberate:

* `copilot_interaction_sessions` is unique on **(`user_id`, `session_ref`)**, not on `session_ref` alone. A
  Copilot thread can be shared - a Teams meeting session appears in more than one participant's history - so a
  global unique constraint would make the second pilot user's insert collide and abort the batch. A separate
  non-unique index on `session_ref` keeps the join back to `copilot_chats.thread_id` a seek.
* `copilot_interactions` is unique on **(`session_id`, `graph_interaction_id`)**. The Graph interaction id is
  only unique within a session, and this key is what makes the overlapping query window idempotent.

`copilot_interactions.user_id` is denormalised from the session so per-user reporting doesn't need a join. It
is intentionally a **non-cascading** foreign key: users already reach interactions via
`users -> copilot_interaction_sessions -> copilot_interactions`, and adding a second cascade path to the same
table is something SQL Server rejects outright.

## Retention

These rows describe how individual people used Copilot, turn by turn. No prompt or response text is stored,
but it is still personal usage data and should not be kept forever.

* `src/Clean Old Data Data.sql` ages out interactions, their key phrases, now-empty sessions and the import
  log, then removes any keyword left referenced by nothing (an extracted key phrase can amount to a whole
  short prompt, so it must not outlive the interaction it came from). Watermarks are deliberately **not**
  cleaned - they hold no interaction data, and deleting them would make the next run re-scan every user's
  full backfill window.
* `src/Clean Data By User StoredProc.sql` (`CleanDataByUser`) deletes a single user's interactions, sessions,
  key-phrase links and watermark explicitly, in leaf-to-parent order, and then removes orphaned keywords.

## Known limitations / deferred

* **Response latency is only computed within an import batch.** If a prompt and its response land in
  different windows, that turn gets no latency rather than a wrong one. Pairing across batches would need a
  lookup of persisted prompts by `requestId`; the index exists for it.
* **No distributed lock.** The cadence gate assumes a single web-job instance. If the importer is ever scaled
  out, two instances could both pass the gate; a lease (Redis `SET NX` or `sp_getapplock`) would be needed.
* **No reports yet.** The data is imported and queryable, but no Reports API endpoint or UI page has been
  added for it.

## Operational notes

* Failures are per-user and never abort the run; the affected user simply keeps its old watermark and is
  retried next cycle.
* A user who returns a terminal error (404/403 - no licence, no such user, blocked by policy) is put on the
  back-off list rather than counted as a failure.
* Graph URLs are not logged at error level: they contain the user's UPN or object id, and this path can fire
  once per user in the pilot group.
* `copilot_interaction_import_log` is the first place to look when judging how much of a cycle this import
  consumed and how many of its calls were productive.
