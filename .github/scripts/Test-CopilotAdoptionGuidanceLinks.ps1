param(
    [string]$CataloguePath = "src\AnalyticsEngine\Common\Entities\CopilotAdoption\CopilotAdoptionGuidanceCatalogue.cs",
    [string]$OverrideFirstExpectedTitle
)

$ErrorActionPreference = "Stop"

function Normalize-Title([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return "" }
    return ([System.Net.WebUtility]::HtmlDecode($value) -replace "\s+", " ").Trim()
}

function Read-HtmlTitle([string]$html) {
    $match = [regex]::Match($html, "<title[^>]*>(.*?)</title>", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) { return "" }
    return Normalize-Title $match.Groups[1].Value
}

function Read-PptxTitles([byte[]]$content) {
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

    $titles = New-Object System.Collections.Generic.List[string]
    $stream = New-Object System.IO.MemoryStream(,$content)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Read)
        try {
            foreach ($entryName in @("docProps/core.xml", "docProps/app.xml")) {
                $entry = $archive.GetEntry($entryName)
                if ($null -eq $entry) { continue }
                $reader = New-Object System.IO.StreamReader($entry.Open())
                try {
                    [xml]$xml = $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }

                $nsm = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
                $nsm.AddNamespace("dc", "http://purl.org/dc/elements/1.1/")
                $nsm.AddNamespace("vt", "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes")

                $coreTitle = $xml.SelectSingleNode("//dc:title", $nsm)
                if ($null -ne $coreTitle -and -not [string]::IsNullOrWhiteSpace($coreTitle.InnerText)) {
                    $titles.Add((Normalize-Title $coreTitle.InnerText))
                }

                foreach ($partTitle in $xml.SelectNodes("//vt:lpstr", $nsm)) {
                    if (-not [string]::IsNullOrWhiteSpace($partTitle.InnerText)) {
                        $titles.Add((Normalize-Title $partTitle.InnerText))
                    }
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return $titles
}

function Get-UrlContent([string]$url) {
    Add-Type -AssemblyName System.Net.Http | Out-Null

    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $true
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    try {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                $response = $client.GetAsync($url).GetAwaiter().GetResult()
                $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                return [pscustomobject]@{
                    StatusCode = [int]$response.StatusCode
                    ContentType = if ($response.Content.Headers.ContentType) { $response.Content.Headers.ContentType.MediaType } else { "" }
                    FinalUrl = $response.RequestMessage.RequestUri.AbsoluteUri
                    Bytes = $bytes
                    Text = [System.Text.Encoding]::UTF8.GetString($bytes)
                }
            }
            catch {
                if ($attempt -eq 3) { throw }
                Start-Sleep -Seconds (2 * $attempt)
            }
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

$catalogueFullPath = Join-Path (Get-Location) $CataloguePath
$source = Get-Content -LiteralPath $catalogueFullPath -Raw
$countMatch = [regex]::Match($source, 'public\s+const\s+int\s+ExpectedLinkCount\s*=\s*(?<count>\d+)\s*;')
if (-not $countMatch.Success) {
    throw "ExpectedLinkCount was not found in $CataloguePath."
}
$expectedLinkCount = [int]$countMatch.Groups["count"].Value

$linkPattern = 'Link\(\s*(?<code>[^,]+),\s*"(?<title>[^"]+)",\s*"(?<url>https?://[^"]+)",\s*"(?<expectedTitle>[^"]+)",\s*"(?<audience>[^"]+)"\s*\)'
$links = [regex]::Matches($source, $linkPattern) | ForEach-Object {
    [pscustomobject]@{
        ActionCode = $_.Groups["code"].Value.Trim()
        Title = $_.Groups["title"].Value
        Url = $_.Groups["url"].Value
        ExpectedTitle = $_.Groups["expectedTitle"].Value
        Audience = $_.Groups["audience"].Value
    }
}

if ($links.Count -eq 0) {
    throw "No adoption guidance links were found in $CataloguePath."
}

if ($links.Count -ne $expectedLinkCount) {
    throw "Parsed $($links.Count) adoption guidance link(s) from $CataloguePath, but ExpectedLinkCount is $expectedLinkCount. Update the parser or the expected count so catalogue entries cannot be skipped silently."
}

if ($OverrideFirstExpectedTitle) {
    $links[0].ExpectedTitle = $OverrideFirstExpectedTitle
}

$failures = New-Object System.Collections.Generic.List[string]
foreach ($link in $links) {
    Write-Host "Checking $($link.Url)"
    try {
        $result = Get-UrlContent $link.Url
        if ($result.StatusCode -lt 200 -or $result.StatusCode -ge 400) {
            $failures.Add("$($link.Url) returned HTTP $($result.StatusCode)")
            continue
        }

        $expected = Normalize-Title $link.ExpectedTitle
        if ($result.ContentType -eq "application/vnd.openxmlformats-officedocument.presentationml.presentation" -or $result.FinalUrl.EndsWith(".pptx", [StringComparison]::OrdinalIgnoreCase)) {
            $actualTitles = @(Read-PptxTitles $result.Bytes)
            if (-not ($actualTitles -contains $expected)) {
                $failures.Add("$($link.Url) resolved to $($result.FinalUrl), but the PPTX titles did not contain '$expected'. Actual titles: $($actualTitles -join ' | ')")
            }
            continue
        }

        $actual = Read-HtmlTitle $result.Text
        if ($actual -ne $expected) {
            $failures.Add("$($link.Url) resolved to $($result.FinalUrl), but title was '$actual' instead of '$expected'.")
        }
    }
    catch {
        $failures.Add("$($link.Url) failed: $($_.Exception.Message)")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "$($failures.Count) guidance link check(s) failed."
}

Write-Host "All $($links.Count) Copilot adoption guidance links resolved with the expected content title."
