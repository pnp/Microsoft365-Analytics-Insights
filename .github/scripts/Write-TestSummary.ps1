[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [Parameter(Mandatory)][string]$Name,
    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY
)
$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -Recurse)
if ($files.Count -eq 0) { throw "No TRX results were produced for $Name." }
$results = @(foreach ($file in $files) {
    $document = [xml](Get-Content -LiteralPath $file.FullName -Raw)
    foreach ($result in $document.SelectNodes("//*[local-name()='UnitTestResult']")) {
        [pscustomobject]@{
            Test = $result.testName
            Seconds = if ($result.HasAttribute('duration')) { [timespan]::Parse($result.duration).TotalSeconds } else { 0 }
            Outcome = $result.outcome
        }
    }
})
if ($results.Count -eq 0) { throw "No tests were discovered for $Name." }
$lines = @("### $Name", '', '| Outcome | Tests |', '|---|---:|')
foreach ($group in $results | Group-Object Outcome) { $lines += "| $($group.Name) | $($group.Count) |" }
$lines += @('', '#### Slowest tests', '', '| Test | Seconds |', '|---|---:|')
foreach ($result in $results | Sort-Object Seconds -Descending | Select-Object -First 15) {
    $testName = $result.Test.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
    $lines += '| {0} | {1} |' -f $testName, $result.Seconds.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)
}
if ($SummaryPath) { Add-Content -LiteralPath $SummaryPath -Value $lines }
else { $lines | Write-Output }
