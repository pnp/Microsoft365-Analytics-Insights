<#
.SYNOPSIS
Runs a fictional hand-labelled intent evaluation through the production Azure Foundry backend.
.DESCRIPTION
Never runs in CI automatically. Obtain explicit authorization for an existing demo/dev deployment first.
Use -PrepareOnly for an offline private plan: no authorization, credentials or model endpoint is read,
no request is sent and no accuracy measurements are produced. Only PROMPT_EVAL_OUTPUT is required.
If native executable launch is blocked by host policy, the existing standalone test project can export
the same offline plan: set PROMPT_EVAL_PREPARE_OUTPUT to a new private path, then run its
Preparation_RecordsFrozenCoverageAndBoundsButNeverQualityOrPromptContent test with the approved
test runner. This exports preparation only; it never enables inference or substitutes fixture accuracy.
The script builds without restoring; restore missing dependencies explicitly only after a build reports them.
Set process environment variables PROMPT_EVAL_AUTHORIZED=yes, PROMPT_EVAL_ENDPOINT,
PROMPT_EVAL_DEPLOYMENT, PROMPT_EVAL_MODEL and PROMPT_EVAL_MODEL_VERSION from private verified deployment
metadata; authenticate with PROMPT_EVAL_KEY and/or PROMPT_EVAL_ACCESS_TOKEN. The latter is an already
obtained cognitive-services-scoped token; this command never discovers Azure accounts or changes CLI state.
PROMPT_EVAL_OUTPUT must name a new report in an existing private directory outside ANY Git worktree.
Junctions and symbolic links in that path are rejected, as for historical-repair checkpoints.
Never set these values in tracked source or shell scripts. Do not enable HTTP tracing/debug logging.

The embedded fictional-intents-v1 dataset is frozen before the model run. There is no training or fitting.
It covers every built-in category in English/Spanish/Greek, plus ambiguous, unknown, prompt-injection
and refusal probes. A refusal is an observed transport outcome, not a mandatory label for a probe.
The dataset is small, purposive and correlated. Its measured accuracy is NOT general tenant accuracy.
Controlled fixtures test only harness correctness; they must never be published as real-model evidence.
The offline plan records the corpus/taxonomy hashes and category/language/stratum counts. The corpus
order is fixed and grouped by intent: early circuit-breaking may prevent later categories being sent.

Five requests maximum for the current 41 examples (batches <=10), one at a time, 20 seconds per batch;
at most two attempts per batch solely for the production key-to-Entra fallback, no other retries.
The default authorization budget is 50 prompts and 10 HTTP attempts. Exceeding the prompt budget rejects
the entire run before a call; the dataset is never silently truncated. Output is capped at 500 tokens per
attempt by production code. Input is bounded by the embedded dataset and taxonomy, not a currency limit.
Before a live run, also set PROMPT_EVAL_CURRENCY (three uppercase letters), PROMPT_EVAL_APPROVED_MAX_COST
and PROMPT_EVAL_MAX_COST_PER_HTTP_ATTEMPT (positive decimal amounts using a dot). Obtain the latter
from a conservative private pricing review covering the full request input and 500 output tokens.
The worst-case attempt count times that quote must fit the current approved ceiling, or the runner
rejects the run before constructing a backend. This is NOT a verified price or Azure billing cap.
The evaluator does not inherit authorization from a previous deployment or provisioning operation.
Failures are reported separately from wrong classified answers; throttling/auth/timeouts/service failures
halt further calls. A private report contains ids, counts, model/version and parameter metadata only:
no prompts, raw model replies, reasoning, endpoint/deployment identity, credentials or exception messages.

Exit codes: 0 = all samples got valid category decisions (NOT an accuracy threshold);
2 = invalid/missing configuration or run could not complete; 3 = completed report has unclassified samples.
Report precision divides correct by predicted; recall divides correct by all labelled samples including
failures. Overall accuracy includes failures; conditional classified accuracy excludes them. Other is the
allowed unknown decision. F1 uses all labelled support, so failures reduce recall/F1, not precision.
Language/stratum slices expose coverage and outcome rates; known-intent unknown rate excludes samples
labelled Other from its denominator. All slice results remain descriptive, not independent estimates.
Confusion includes a separate not-classified column; failure counts distinguish
refused/content-filtered/output-limit/invalid-envelope/incomplete-response/invalid-response,
throttled/access-denied/timeout/service-failure and not-sent circuit-breaker rows.
Publish only a reviewed, de-identified summary to the operator methodology; never publish the private report.
.EXAMPLE
.\Invoke-PromptCategoryEvaluation.ps1 -AuthorizedSyntheticOnly
.EXAMPLE
.\Invoke-PromptCategoryEvaluation.ps1 -PrepareOnly
#>
[CmdletBinding(DefaultParameterSetName = 'Prepare')]
param(
    [Parameter(ParameterSetName = 'Live')][switch]$AuthorizedSyntheticOnly,
    [Parameter(ParameterSetName = 'Prepare')][switch]$PrepareOnly,
    [ValidateRange(1, 1000)][int]$MaxPrompts = 50,
    [ValidateRange(1, 200)][int]$MaxHttpAttempts = 10
)
$ErrorActionPreference = 'Stop'
if (-not $PrepareOnly -and -not $AuthorizedSyntheticOnly) { throw 'Explicit synthetic-only authorization is required.' }
$project = Join-Path $PSScriptRoot 'PromptCategoryEvaluation\PromptCategoryEvaluation.csproj'
& dotnet build $project -c Release --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Evaluation runner build failed.' }
$originalPrompts = $env:PROMPT_EVAL_MAX_PROMPTS
$originalAttempts = $env:PROMPT_EVAL_MAX_HTTP_ATTEMPTS
try {
    $env:PROMPT_EVAL_MAX_PROMPTS = "$MaxPrompts"
    $env:PROMPT_EVAL_MAX_HTTP_ATTEMPTS = "$MaxHttpAttempts"
    $mode = if ($PrepareOnly) { '--prepare-synthetic-only' } else { '--authorized-synthetic-only' }
    & dotnet (Join-Path $PSScriptRoot 'PromptCategoryEvaluation\bin\Release\net10.0\PromptCategoryEvaluation.dll') $mode
    $result = $LASTEXITCODE
} finally {
    $env:PROMPT_EVAL_MAX_PROMPTS = $originalPrompts
    $env:PROMPT_EVAL_MAX_HTTP_ATTEMPTS = $originalAttempts
}
exit $result
