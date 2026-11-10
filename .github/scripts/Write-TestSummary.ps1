[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [Parameter(Mandatory)][string]$Name,
    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY
)
$ErrorActionPreference = 'Stop'
# Starts the Inconclusive message of every test that needs a live credential (Tests.UnitTests/LiveCredentials.cs).
# Change both together.
$liveCredentialMarker = 'Needs a live credential:'
function Format-Cell([string]$Text, [int]$MaxLength = [int]::MaxValue) {
    if ($Text.Length -gt $MaxLength) { $Text = $Text.Substring(0, $MaxLength) + '...' }
    $Text.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
}
$files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -Recurse)
if ($files.Count -eq 0) { throw "No TRX results were produced for $Name." }
$results = @(foreach ($file in $files) {
    $document = [xml](Get-Content -LiteralPath $file.FullName -Raw)
    foreach ($result in $document.SelectNodes("//*[local-name()='UnitTestResult']")) {
        $message = $result.SelectSingleNode("*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")
        [pscustomobject]@{
            Test = $result.testName
            Seconds = if ($result.HasAttribute('duration')) { [timespan]::Parse($result.duration).TotalSeconds } else { 0 }
            Outcome = $result.outcome
            Reason = if ($message) { ($message.InnerText -replace '^Assert\.Inconclusive failed\.\s*', '').Trim() } else { '' }
        }
    }
})
if ($results.Count -eq 0) { throw "No tests were discovered for $Name." }
# MSTest records an Inconclusive test as NotExecuted. List each one with its reason, so a green run doesn't overstate what ran.
$notRun = @($results | Where-Object { $_.Outcome -in 'NotExecuted', 'Inconclusive' } | Sort-Object Test)
$withoutCredentials = @($notRun | Where-Object { $_.Reason.StartsWith($liveCredentialMarker, [StringComparison]::Ordinal) })
$lines = @("### $Name", '')
if ($withoutCredentials.Count -gt 0) {
    $warning = "> **Warning:** $($withoutCredentials.Count) test(s) that need a live credential did not run, because this run has none. " +
        'Dependabot and fork pull requests get no repository secrets. They are listed under *Not run*.'
    $lines += @($warning, '')
}
$lines += @('| Outcome | Tests |', '|---|---:|')
foreach ($group in $results | Group-Object Outcome) { $lines += "| $($group.Name) | $($group.Count) |" }
if ($notRun.Count -gt 0) {
    $lines += @('', '#### Not run', '', '| Test | Reason |', '|---|---|')
    foreach ($result in $notRun) { $lines += '| {0} | {1} |' -f (Format-Cell $result.Test), (Format-Cell $result.Reason 300) }
}
$lines += @('', '#### Slowest tests', '', '| Test | Seconds |', '|---|---:|')
foreach ($result in $results | Sort-Object Seconds -Descending | Select-Object -First 15) {
    $lines += '| {0} | {1} |' -f (Format-Cell $result.Test), $result.Seconds.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)
}
if ($SummaryPath) { Add-Content -LiteralPath $SummaryPath -Value $lines }
else { $lines | Write-Output }
