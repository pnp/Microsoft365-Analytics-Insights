<#
.SYNOPSIS
    Compares two Copilot Adoption workbooks exported from the portal, and refuses to compare two
    files that cannot be compared fairly.

.DESCRIPTION
    The Copilot Adoption tool keeps no history. A baseline is an exported workbook (the "Excel
    report" button on the Copilot Adoption page), and measuring change means comparing two exports
    taken on different dates.

    The script first checks that the comparison is fair. Four things must match, as the workbook's
    "How this is calculated" sheet explains:

      1. Product build            Report sheet, "Product build".
      2. Every option             Settings sheet. fromUtc, toUtc and toExclusiveUtc are not compared:
                                  they place the period in time, and differ between any two dates.
      3. Reporting period length  Report sheet, "Period covered".
      4. Population scope         The title and the banner lines at the top of the Report sheet, and
                                  its "Population" row: an email-domain narrowing, a filter on people
                                  and an administrator's filter all show there.

    If any of them differs, the script stops with exit code 1, names each check that failed and shows
    both values. Nothing is compared. -Force compares anyway, and reports every failed check at the
    top of the report and on the error stream.

    It then lists the changes on the "Snapshot facts" sheet, key by key: the value before, the value
    after, the change (a numeric difference when both values are numbers, or a number of days when
    both are dates), and a note when:
      - a key is in the later file only: it was added in a later build, so the earlier export has no
        value for it;
      - a key is in the earlier file only: it was removed in the later build;
      - a value is blank in one file. A blank is unknown, never zero, so no change is calculated.

    It reads only three sheets: the header of "Report", "Snapshot facts" and "Settings". It never
    reads "Run diagnostics", whose keys vary from run to run.

    It has no dependencies. It reads the .xlsx package directly, with System.IO.Compression and the
    package's XML, so it needs neither Excel nor a PowerShell module, and it still reads a workbook
    that has been opened and saved again in Excel. It runs on Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Before
    The earlier export: the baseline.

.PARAMETER After
    The later export.

.PARAMETER Format
    Text (the default) for a report to read, or Csv for a table to open in Excel or paste into a
    board pack. The CSV columns are Section, Key, Before, After, Change and Note. Its first rows are
    the comparability checks, so the file shows on its own that the comparison was fair.

.PARAMETER OutFile
    Writes the report to this file instead of to the output stream. The file is UTF-8 with a
    byte-order mark, so Excel reads non-Latin text such as Greek correctly.

.PARAMETER IncludeUnchanged
    Lists every key, including the ones whose value did not change. By default only changed keys,
    and keys with a note, are listed.

.PARAMETER Force
    Compares even when a comparability check fails. Every failed check is still reported, at the top
    of the report and on the error stream, and the exit code is 0.

.EXAMPLE
    .\Compare-CopilotAdoptionWorkbooks.ps1 -Before .\copilot-adoption-28d-2026-01-05.xlsx -After .\copilot-adoption-28d-2026-04-05.xlsx

    Checks that the two exports can be compared, then lists every figure that changed.

.EXAMPLE
    .\Compare-CopilotAdoptionWorkbooks.ps1 .\baseline.xlsx .\latest.xlsx -Format Csv -OutFile .\copilot-adoption-change.csv

    Writes the comparison as a CSV file for a board pack.

.EXAMPLE
    .\Compare-CopilotAdoptionWorkbooks.ps1 .\baseline.xlsx .\latest.xlsx -Force

    Compares two exports from different product builds anyway. The failed checks are shown at the top.

.OUTPUTS
    System.String. The report, one line at a time, unless -OutFile is used.

.NOTES
    Exit codes:
      0  The comparison was produced. With -Force, a check may have failed; the report says which.
      1  The files are not comparable. Nothing was compared; the error stream says why.
      2  A file could not be read, or is not a Copilot Adoption workbook.

    Numbers use '.' as the decimal separator whatever the culture, as the workbook itself does. Dates
    are UTC, shown as yyyy-MM-dd, with the time when it is not midnight.

    Keep this file ASCII: Windows PowerShell 5.1 reads a script without a byte-order mark in the
    system code page.

.LINK
    https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Copilot-Adoption-Tool
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Before,

    [Parameter(Mandatory = $true, Position = 1)]
    [string] $After,

    [ValidateSet('Text', 'Csv')]
    [string] $Format = 'Text',

    [string] $OutFile,

    [switch] $IncludeUnchanged,

    [switch] $Force
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Invariant = [System.Globalization.CultureInfo]::InvariantCulture

# Settings that place the reporting period in time. Two exports taken on different dates differ here
# by design; the period's LENGTH is what has to match, and that is checked on its own.
$script:PeriodPositionSettings = @('fromUtc', 'toUtc', 'toExclusiveUtc')

# Characters that make a spreadsheet read a CSV cell as a formula. Text from the workbook can be tenant
# data (an agent's name, a filter on a department), so it is defused before it reaches a CSV.
$script:FormulaTriggers = @('=', '+', '-', '@', "`t", "`r")

#region Reading the package

function Get-FullPath([string] $Path) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Read-XmlStream([System.IO.Stream] $Stream) {
    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($Stream, $settings)
    try {
        $document = New-Object System.Xml.XmlDocument
        $document.PreserveWhitespace = $true
        $document.XmlResolver = $null
        $document.Load($reader)
        # The comma stops PowerShell enumerating the document's child nodes on the way out.
        return , $document
    }
    finally {
        $reader.Dispose()
    }
}

function Get-ChildElements([System.Xml.XmlNode] $Node, [string] $LocalName) {
    $found = New-Object System.Collections.Generic.List[System.Xml.XmlElement]
    if ($null -ne $Node) {
        foreach ($child in $Node.ChildNodes) {
            if ($child -is [System.Xml.XmlElement] -and $child.LocalName -ceq $LocalName) {
                $found.Add($child)
            }
        }
    }
    return , $found
}

function Get-RelationshipId([System.Xml.XmlElement] $Element) {
    foreach ($attribute in $Element.Attributes) {
        if ($attribute.LocalName -ceq 'id' -and $attribute.NamespaceURI -like '*/relationships') {
            return $attribute.Value
        }
    }
    return $null
}

function Open-Package([string] $Path, [string] $Role) {
    $full = Get-FullPath $Path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "The $Role file was not found: $full"
    }

    # Shared read access, so a workbook that is still open in Excel can be read.
    $stream = [System.IO.File]::Open($full, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive -ArgumentList $stream, ([System.IO.Compression.ZipArchiveMode]::Read), $false
    }
    catch {
        $stream.Dispose()
        throw "The $Role file is not an Excel workbook (.xlsx): $full"
    }

    # Part names are case-insensitive in an Open XML package.
    $entries = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/').TrimStart('/')
        if (-not $entries.ContainsKey($name)) {
            $entries.Add($name, $entry)
        }
    }

    return [pscustomobject]@{
        Path          = $full
        Role          = $Role
        Stream        = $stream
        Archive       = $archive
        Entries       = $entries
        Sheets        = $null
        SharedStrings = $null
        StringCache   = New-Object 'System.Collections.Generic.Dictionary[int,string]'
        DateStyles    = $null
    }
}

function Close-Package($Package) {
    if ($null -ne $Package) {
        $Package.Archive.Dispose()
        $Package.Stream.Dispose()
    }
}

function Read-Part($Package, [string] $PartName) {
    if (-not $Package.Entries.ContainsKey($PartName)) {
        return $null
    }

    $stream = $Package.Entries[$PartName].Open()
    try {
        return , (Read-XmlStream $stream)
    }
    finally {
        $stream.Dispose()
    }
}

function Resolve-PartName([string] $SourcePart, [string] $Target) {
    $target = $Target.Replace('\', '/')
    if ($target.StartsWith('/')) {
        $combined = $target
    }
    else {
        $slash = $SourcePart.LastIndexOf('/')
        $combined = if ($slash -ge 0) { $SourcePart.Substring(0, $slash) + '/' + $target } else { $target }
    }

    $segments = New-Object System.Collections.Generic.List[string]
    foreach ($segment in $combined.Split('/')) {
        if ($segment -eq '' -or $segment -eq '.') { continue }
        if ($segment -eq '..') {
            if ($segments.Count -gt 0) { $segments.RemoveAt($segments.Count - 1) }
            continue
        }
        $segments.Add([System.Uri]::UnescapeDataString($segment))
    }
    return ($segments -join '/')
}

function Read-Relationships($Package, [string] $SourcePart) {
    $slash = $SourcePart.LastIndexOf('/')
    $relsPart = if ($slash -ge 0) {
        $SourcePart.Substring(0, $slash) + '/_rels/' + $SourcePart.Substring($slash + 1) + '.rels'
    }
    else {
        '_rels/' + $SourcePart + '.rels'
    }

    $relationships = New-Object System.Collections.Generic.List[object]
    $document = Read-Part $Package $relsPart
    if ($null -eq $document) {
        return , $relationships
    }

    foreach ($relationship in (Get-ChildElements $document.DocumentElement 'Relationship')) {
        if ($relationship.GetAttribute('TargetMode') -eq 'External') { continue }
        $relationships.Add([pscustomobject]@{
            Id   = $relationship.GetAttribute('Id')
            Type = $relationship.GetAttribute('Type')
            Part = Resolve-PartName $SourcePart $relationship.GetAttribute('Target')
        })
    }
    return , $relationships
}

function Get-StringItemText([System.Xml.XmlNode] $Node) {
    # A shared or inline string: its own <t>, or the <t> of each rich-text run. Phonetic runs (<rPh>)
    # are reading aids, not part of the value.
    $text = New-Object System.Text.StringBuilder
    foreach ($child in $Node.ChildNodes) {
        if (-not ($child -is [System.Xml.XmlElement])) { continue }
        if ($child.LocalName -ceq 't') {
            [void] $text.Append($child.InnerText)
        }
        elseif ($child.LocalName -ceq 'r') {
            foreach ($run in (Get-ChildElements $child 't')) {
                [void] $text.Append($run.InnerText)
            }
        }
    }
    return $text.ToString()
}

function Test-DateFormat([int] $FormatId, [hashtable] $CustomFormats) {
    # The built-in date and time formats, including the East Asian ones.
    if (($FormatId -ge 14 -and $FormatId -le 22) -or ($FormatId -ge 27 -and $FormatId -le 36) -or
        ($FormatId -ge 45 -and $FormatId -le 47) -or ($FormatId -ge 50 -and $FormatId -le 58)) {
        return $true
    }
    if (-not $CustomFormats.ContainsKey($FormatId)) {
        return $false
    }

    # A custom format is a date when its first section uses a date or time token once quoted text,
    # escaped characters and bracketed colours or conditions are taken out.
    $code = [regex]::Replace([string] $CustomFormats[$FormatId], '"[^"]*"|\\.|\[[^\]]*\]', '')
    $code = $code.Split(';')[0]
    return [regex]::IsMatch($code, '[dDmMyYhHsS]')
}

function Initialize-Workbook($Package) {
    $workbookPart = 'xl/workbook.xml'
    foreach ($relationship in (Read-Relationships $Package '')) {
        if ($relationship.Type -like '*/officeDocument') {
            $workbookPart = $relationship.Part
            break
        }
    }

    $workbook = Read-Part $Package $workbookPart
    if ($null -eq $workbook) {
        throw "The $($Package.Role) file is not an Excel workbook: it has no workbook part. $($Package.Path)"
    }

    $relationships = Read-Relationships $Package $workbookPart
    $partsById = @{}
    $sharedStringsPart = $null
    $stylesPart = $null
    foreach ($relationship in $relationships) {
        $partsById[$relationship.Id] = $relationship.Part
        if ($relationship.Type -like '*/sharedStrings') { $sharedStringsPart = $relationship.Part }
        if ($relationship.Type -like '*/styles') { $stylesPart = $relationship.Part }
    }

    # Sheet names are resolved through workbook.xml and its relationships, never assumed from the
    # part names: Excel renumbers the parts when a workbook is saved again.
    $sheets = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($sheetList in (Get-ChildElements $workbook.DocumentElement 'sheets')) {
        foreach ($sheet in (Get-ChildElements $sheetList 'sheet')) {
            $id = Get-RelationshipId $sheet
            $name = $sheet.GetAttribute('name')
            if ($null -ne $id -and $partsById.ContainsKey($id) -and -not $sheets.ContainsKey($name)) {
                $sheets.Add($name, $partsById[$id])
            }
        }
    }
    $Package.Sheets = $sheets

    # Resolved on demand: a workbook saved again by Excel keeps every string of its per-user sheets in
    # this one table - hundreds of thousands on a large tenant - and only a few hundred are ever read.
    if ($null -ne $sharedStringsPart) {
        $table = Read-Part $Package $sharedStringsPart
        if ($null -ne $table) {
            $Package.SharedStrings = $table.DocumentElement.SelectNodes("*[local-name()='si']")
        }
    }

    $dateStyles = New-Object 'System.Collections.Generic.HashSet[int]'
    if ($null -ne $stylesPart) {
        $styles = Read-Part $Package $stylesPart
        if ($null -ne $styles) {
            $customFormats = @{}
            foreach ($formats in (Get-ChildElements $styles.DocumentElement 'numFmts')) {
                foreach ($format in (Get-ChildElements $formats 'numFmt')) {
                    $customFormats[[int] $format.GetAttribute('numFmtId')] = $format.GetAttribute('formatCode')
                }
            }

            $index = 0
            foreach ($cellFormats in (Get-ChildElements $styles.DocumentElement 'cellXfs')) {
                foreach ($cellFormat in (Get-ChildElements $cellFormats 'xf')) {
                    $formatId = 0
                    [void] [int]::TryParse($cellFormat.GetAttribute('numFmtId'), [ref] $formatId)
                    if (Test-DateFormat $formatId $customFormats) {
                        [void] $dateStyles.Add($index)
                    }
                    $index++
                }
            }
        }
    }
    $Package.DateStyles = $dateStyles
}

#endregion

#region Cells

function New-CellValue([string] $Kind, [string] $Text, $Number) {
    return [pscustomobject]@{ Kind = $Kind; Text = $Text; Number = $Number }
}

function New-BlankValue {
    return New-CellValue 'Blank' '' $null
}

function Format-Number([double] $Number) {
    return $Number.ToString('G15', $script:Invariant)
}

function Format-Date([double] $Serial) {
    try {
        $date = [DateTime]::FromOADate($Serial)
    }
    catch {
        return Format-Number $Serial
    }
    if ($date.TimeOfDay -eq [TimeSpan]::Zero) {
        return $date.ToString('yyyy-MM-dd', $script:Invariant)
    }
    return $date.ToString('yyyy-MM-dd HH:mm:ss', $script:Invariant)
}

function Get-SharedString($Package, [int] $Index) {
    if ($null -eq $Package.SharedStrings -or $Index -lt 0 -or $Index -ge $Package.SharedStrings.Count) {
        return $null
    }

    $text = $null
    if (-not $Package.StringCache.TryGetValue($Index, [ref] $text)) {
        $text = Get-StringItemText $Package.SharedStrings.Item($Index)
        $Package.StringCache[$Index] = $text
    }
    return $text
}

function ConvertTo-CellValue($Package, [System.Xml.XmlElement] $Cell) {
    $type = $Cell.GetAttribute('t')
    $raw = $null
    $inline = $null
    foreach ($child in $Cell.ChildNodes) {
        if (-not ($child -is [System.Xml.XmlElement])) { continue }
        if ($child.LocalName -ceq 'v') { $raw = $child.InnerText }
        elseif ($child.LocalName -ceq 'is') { $inline = $child }
    }

    $text = $null
    switch -CaseSensitive ($type) {
        's' {
            $index = 0
            if ($null -ne $raw -and [int]::TryParse($raw, [System.Globalization.NumberStyles]::Integer, $script:Invariant, [ref] $index)) {
                $text = Get-SharedString $Package $index
            }
        }
        'inlineStr' {
            if ($null -ne $inline) { $text = Get-StringItemText $inline }
        }
        'str' { $text = $raw }
        'b' {
            if ([string]::IsNullOrEmpty($raw)) { return New-BlankValue }
            return New-CellValue 'Text' $(if ($raw -eq '1') { 'TRUE' } else { 'FALSE' }) $null
        }
        'e' {
            if ([string]::IsNullOrEmpty($raw)) { return New-BlankValue }
            return New-CellValue 'Text' $raw $null
        }
        'd' {
            $parsed = [DateTime]::MinValue
            if ([DateTime]::TryParse($raw, $script:Invariant, [System.Globalization.DateTimeStyles]::RoundtripKind, [ref] $parsed)) {
                return New-CellValue 'Date' (Format-Date $parsed.ToOADate()) $parsed.ToOADate()
            }
            $text = $raw
        }
        default {
            if ([string]::IsNullOrWhiteSpace($raw)) { return New-BlankValue }
            $number = 0.0
            if (-not [double]::TryParse($raw, [System.Globalization.NumberStyles]::Float, $script:Invariant, [ref] $number)) {
                return New-CellValue 'Text' $raw $null
            }
            $style = 0
            if ([int]::TryParse($Cell.GetAttribute('s'), [ref] $style) -and $Package.DateStyles.Contains($style)) {
                return New-CellValue 'Date' (Format-Date $number) $number
            }
            return New-CellValue 'Number' (Format-Number $number) $number
        }
    }

    # The workbook writes an unknown figure as an empty cell. A blank is unknown, never zero.
    if ([string]::IsNullOrWhiteSpace($text)) {
        return New-BlankValue
    }
    return New-CellValue 'Text' $text $null
}

function ConvertFrom-ColumnName([string] $Letters) {
    $number = 0
    foreach ($letter in $Letters.ToCharArray()) {
        $number = ($number * 26) + ([int] $letter - [int][char] 'A' + 1)
    }
    return $number
}

function Read-SheetRows($Package, [string] $SheetName) {
    if (-not $Package.Sheets.ContainsKey($SheetName)) {
        throw "The $($Package.Role) file has no '$SheetName' sheet, so it is not a Copilot Adoption workbook - or it was exported by a build too old to compare. $($Package.Path)"
    }

    $document = Read-Part $Package $Package.Sheets[$SheetName]
    if ($null -eq $document) {
        throw "The '$SheetName' sheet of the $($Package.Role) file is missing from the package. $($Package.Path)"
    }

    # Each row as a number and its cells by column number. Only the first two columns - the key or label,
    # and its value - are ever read, so nothing else is decoded.
    $rows = New-Object System.Collections.Generic.List[object]
    $rowNumber = 0
    foreach ($sheetData in (Get-ChildElements $document.DocumentElement 'sheetData')) {
        foreach ($row in (Get-ChildElements $sheetData 'row')) {
            $declared = 0
            $rowNumber = if ([int]::TryParse($row.GetAttribute('r'), [ref] $declared)) { $declared } else { $rowNumber + 1 }

            $cells = @{}
            $column = 0
            foreach ($cell in (Get-ChildElements $row 'c')) {
                $reference = [regex]::Match($cell.GetAttribute('r'), '^([A-Za-z]+)([0-9]+)$')
                $column = if ($reference.Success) { ConvertFrom-ColumnName $reference.Groups[1].Value.ToUpperInvariant() } else { $column + 1 }
                if ($column -le 2) {
                    $cells[$column] = ConvertTo-CellValue $Package $cell
                }
            }
            $rows.Add([pscustomobject]@{ Number = $rowNumber; Cells = $cells })
        }
    }
    return , $rows
}

function Get-Cell($Row, [int] $Column) {
    if ($Row.Cells.ContainsKey($Column)) {
        return $Row.Cells[$Column]
    }
    return New-BlankValue
}

#endregion

#region The three sheets

function Read-KeyValueSheet($Package, [string] $SheetName, [string] $KeyHeader) {
    $rows = Read-SheetRows $Package $SheetName

    $header = -1
    for ($i = 0; $i -lt $rows.Count; $i++) {
        if ((Get-Cell $rows[$i] 1).Text -ceq $KeyHeader -and (Get-Cell $rows[$i] 2).Text -ceq 'Value') {
            $header = $i
            break
        }
    }
    if ($header -lt 0) {
        throw "The '$SheetName' sheet of the $($Package.Role) file has no '$KeyHeader | Value' header row, so it is not a Copilot Adoption workbook. $($Package.Path)"
    }

    $keys = New-Object System.Collections.Generic.List[string]
    $values = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
    for ($i = $header + 1; $i -lt $rows.Count; $i++) {
        $key = (Get-Cell $rows[$i] 1).Text
        if ([string]::IsNullOrWhiteSpace($key)) { continue }
        if ($values.ContainsKey($key)) {
            throw "The '$SheetName' sheet of the $($Package.Role) file has the key '$key' twice, so a lookup against it would be ambiguous. Has it been edited by hand? $($Package.Path)"
        }
        $keys.Add($key)
        $values.Add($key, (Get-Cell $rows[$i] 2))
    }

    return [pscustomobject]@{ Keys = $keys; Values = $values }
}

function Read-ReportHeader($Package) {
    $rows = Read-SheetRows $Package 'Report'

    $title = $null
    $titleIndex = -1
    $header = -1
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $first = Get-Cell $rows[$i] 1
        if ($first.Kind -eq 'Blank') { continue }
        if ($null -eq $title) {
            $title = $first.Text
            $titleIndex = $i
            continue
        }
        if ($first.Text -ceq 'Property' -and (Get-Cell $rows[$i] 2).Text -ceq 'Value') {
            $header = $i
            break
        }
    }
    if ($header -lt 0) {
        throw "The 'Report' sheet of the $($Package.Role) file has no 'Property | Value' table, so it is not a Copilot Adoption workbook. $($Package.Path)"
    }

    # Every line between the title and the table is about the population: the email domain the file
    # was narrowed to, the people filters, and attributes those filters name that no longer exist.
    $scopeLines = New-Object System.Collections.Generic.List[string]
    for ($i = $titleIndex + 1; $i -lt $header; $i++) {
        $line = Get-Cell $rows[$i] 1
        if ($line.Kind -ne 'Blank') { $scopeLines.Add($line.Text) }
    }

    $properties = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
    for ($i = $header + 1; $i -lt $rows.Count; $i++) {
        $label = Get-Cell $rows[$i] 1
        if ($label.Kind -eq 'Blank') { break }
        if (-not $properties.ContainsKey($label.Text)) {
            $properties.Add($label.Text, (Get-Cell $rows[$i] 2))
        }
    }

    return [pscustomobject]@{ Title = $title; ScopeLines = $scopeLines; Properties = $properties }
}

function Read-CopilotAdoptionWorkbook([string] $Path, [string] $Role) {
    $package = Open-Package $Path $Role
    try {
        Initialize-Workbook $package
        return [pscustomobject]@{
            Path     = $package.Path
            Role     = $Role
            Report   = Read-ReportHeader $package
            Facts    = Read-KeyValueSheet $package 'Snapshot facts' 'Key'
            Settings = Read-KeyValueSheet $package 'Settings' 'Setting'
        }
    }
    finally {
        Close-Package $package
    }
}

function Get-ReportText($Workbook, [string] $Property) {
    if ($Workbook.Report.Properties.ContainsKey($Property)) {
        return $Workbook.Report.Properties[$Property].Text
    }
    return ''
}

#endregion

#region Comparing

function Test-SameValue($First, $Second) {
    if ($First.Kind -ne $Second.Kind) { return $false }
    return [string]::Equals($First.Text, $Second.Text, [System.StringComparison]::Ordinal)
}

function Show-Value([string] $Text) {
    if ([string]::IsNullOrEmpty($Text)) { return '(blank)' }
    return "'" + $Text + "'"
}

function Test-Comparable($Earlier, $Later) {
    $checks = New-Object System.Collections.Generic.List[object]

    # 1. Product build.
    $buildBefore = Get-ReportText $Earlier 'Product build'
    $buildAfter = Get-ReportText $Later 'Product build'
    $checks.Add([pscustomobject]@{
        Name    = 'Product build'
        Before  = $buildBefore
        After   = $buildAfter
        Match   = (-not [string]::IsNullOrEmpty($buildBefore)) -and [string]::Equals($buildBefore, $buildAfter, [System.StringComparison]::Ordinal)
        Details = @()
        Why     = 'A figure can move because adoption moved or because the product changed how it counts it.'
    })

    # 2. Every option on the Settings sheet, except the ones that place the period in time.
    $differences = New-Object System.Collections.Generic.List[object]
    $compared = 0
    $allKeys = New-Object 'System.Collections.Generic.SortedSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($key in $Earlier.Settings.Keys) { [void] $allKeys.Add($key) }
    foreach ($key in $Later.Settings.Keys) { [void] $allKeys.Add($key) }
    foreach ($key in $allKeys) {
        if ($script:PeriodPositionSettings -ccontains $key) { continue }
        $compared++
        $inEarlier = $Earlier.Settings.Values.ContainsKey($key)
        $inLater = $Later.Settings.Values.ContainsKey($key)
        $valueBefore = if ($inEarlier) { $Earlier.Settings.Values[$key].Text } else { '(not in this file)' }
        $valueAfter = if ($inLater) { $Later.Settings.Values[$key].Text } else { '(not in this file)' }
        if (-not $inEarlier -or -not $inLater -or -not (Test-SameValue $Earlier.Settings.Values[$key] $Later.Settings.Values[$key])) {
            $differences.Add([pscustomobject]@{ Key = $key; Before = $valueBefore; After = $valueAfter })
        }
    }
    $checks.Add([pscustomobject]@{
        Name    = 'Settings'
        Before  = "$($Earlier.Settings.Keys.Count) options"
        After   = "$($Later.Settings.Keys.Count) options"
        Match   = $differences.Count -eq 0
        Details = $differences.ToArray()
        Why     = 'A threshold or assumption that moved changes the figures without adoption changing at all.'
        Compared = $compared
    })

    # 3. Reporting period length.
    $periodBefore = Get-ReportText $Earlier 'Period covered'
    $periodAfter = Get-ReportText $Later 'Period covered'
    $checks.Add([pscustomobject]@{
        Name    = 'Reporting period length'
        Before  = $periodBefore
        After   = $periodAfter
        Match   = (-not [string]::IsNullOrEmpty($periodBefore)) -and [string]::Equals($periodBefore, $periodAfter, [System.StringComparison]::Ordinal)
        Details = @()
        Why     = 'Many figures are totals over the period, so a longer period is larger without any change in adoption.'
    })

    # 4. Population scope: the "Population" row, the title and every line above the table.
    $populationBefore = Get-ReportText $Earlier 'Population'
    $populationAfter = Get-ReportText $Later 'Population'
    $scopeDetails = New-Object System.Collections.Generic.List[object]
    if (-not [string]::Equals($Earlier.Report.Title, $Later.Report.Title, [System.StringComparison]::Ordinal)) {
        $scopeDetails.Add([pscustomobject]@{ Key = 'Report title'; Before = $Earlier.Report.Title; After = $Later.Report.Title })
    }
    $linesBefore = @($Earlier.Report.ScopeLines)
    $linesAfter = @($Later.Report.ScopeLines)
    $lineCount = [Math]::Max($linesBefore.Count, $linesAfter.Count)
    for ($i = 0; $i -lt $lineCount; $i++) {
        $lineBefore = if ($i -lt $linesBefore.Count) { $linesBefore[$i] } else { '' }
        $lineAfter = if ($i -lt $linesAfter.Count) { $linesAfter[$i] } else { '' }
        if (-not [string]::Equals($lineBefore, $lineAfter, [System.StringComparison]::Ordinal)) {
            $scopeDetails.Add([pscustomobject]@{ Key = "Report banner line $($i + 1)"; Before = $lineBefore; After = $lineAfter })
        }
    }
    $checks.Add([pscustomobject]@{
        Name    = 'Population scope'
        Before  = $populationBefore
        After   = $populationAfter
        Match   = (-not [string]::IsNullOrEmpty($populationBefore)) -and
                  [string]::Equals($populationBefore, $populationAfter, [System.StringComparison]::Ordinal) -and
                  $scopeDetails.Count -eq 0
        Details = $scopeDetails.ToArray()
        Why     = 'A file narrowed to one email domain or filtered to some people, compared with a wider one, reads as a collapse in every headcount.'
    })

    return , $checks
}

function Get-Cautions($Earlier, $Later) {
    $cautions = New-Object System.Collections.Generic.List[object]

    $rowsBefore = Get-ReportText $Earlier 'Individual rows'
    $rowsAfter = Get-ReportText $Later 'Individual rows'
    if (-not [string]::Equals($rowsBefore, $rowsAfter, [System.StringComparison]::Ordinal)) {
        $cautions.Add([pscustomobject]@{
            Name = 'Individual rows'
            Text = "One file was exported with the people in it and the other without them ($(Show-Value $rowsBefore) before, $(Show-Value $rowsAfter) after). The row counts of lists that name people, such as accountabilityRollup.count when the roll-up is grouped by manager, are withheld in the file without them, so a change in those counts is not a change in adoption."
        })
    }

    foreach ($workbook in @($Earlier, $Later)) {
        if ((Get-ReportText $workbook 'Figures incomplete') -ceq 'Yes') {
            $cautions.Add([pscustomobject]@{
                Name = 'Figures incomplete'
                Text = "The $($workbook.Role) file says its figures are incomplete: a query failed or timed out when it was produced, so its figures are a floor rather than a total. Its 'Run diagnostics' sheet says which step degraded."
            })
        }
    }

    if ((Get-ReportText $Earlier 'Product build') -ceq 'DEV_BUILD' -and (Get-ReportText $Later 'Product build') -ceq 'DEV_BUILD') {
        $cautions.Add([pscustomobject]@{
            Name = 'Product build'
            Text = "Both files come from an unreleased local build (DEV_BUILD), so a matching build label does not prove they were produced by the same code."
        })
    }

    $generatedBefore = $null
    $generatedAfter = $null
    if ($Earlier.Report.Properties.ContainsKey('Generated (UTC)')) { $generatedBefore = $Earlier.Report.Properties['Generated (UTC)'] }
    if ($Later.Report.Properties.ContainsKey('Generated (UTC)')) { $generatedAfter = $Later.Report.Properties['Generated (UTC)'] }
    if ($null -ne $generatedBefore -and $null -ne $generatedAfter -and
        $generatedBefore.Kind -eq 'Date' -and $generatedAfter.Kind -eq 'Date' -and
        $generatedAfter.Number -lt $generatedBefore.Number) {
        $cautions.Add([pscustomobject]@{
            Name = 'Generated (UTC)'
            Text = "The 'after' file was produced before the 'before' file ($($generatedAfter.Text) against $($generatedBefore.Text)). Were the two files given the wrong way round?"
        })
    }

    return , $cautions
}

function Format-Change([double] $Change, [string] $Unit) {
    $rounded = [Math]::Round($Change, 10)
    if ($Unit -eq 'days') {
        $text = ([Math]::Round($Change, 2)).ToString('0.##', $script:Invariant)
    }
    else {
        $text = $rounded.ToString('G15', $script:Invariant)
    }
    if ($rounded -gt 0) { $text = '+' + $text }
    if ($Unit) { $text = $text + ' ' + $Unit }
    return $text
}

function Compare-Facts($Earlier, $Later) {
    $allKeys = New-Object 'System.Collections.Generic.SortedSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($key in $Earlier.Facts.Keys) { [void] $allKeys.Add($key) }
    foreach ($key in $Later.Facts.Keys) { [void] $allKeys.Add($key) }

    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($key in $allKeys) {
        $inEarlier = $Earlier.Facts.Values.ContainsKey($key)
        $inLater = $Later.Facts.Values.ContainsKey($key)
        $valueBefore = if ($inEarlier) { $Earlier.Facts.Values[$key] } else { New-BlankValue }
        $valueAfter = if ($inLater) { $Later.Facts.Values[$key] } else { New-BlankValue }

        $change = ''
        $note = ''
        $status = 'Changed'
        if (-not $inEarlier) {
            $status = 'Added'
            $note = 'added in a later build - no value in the earlier export'
        }
        elseif (-not $inLater) {
            $status = 'Removed'
            $note = 'removed in the later build - no value in the later export'
        }
        elseif ($valueBefore.Kind -eq 'Blank' -and $valueAfter.Kind -eq 'Blank') {
            $status = 'Unchanged'
            $note = 'blank in both files - unknown'
        }
        elseif ($valueBefore.Kind -eq 'Blank') {
            $status = 'Blank'
            $note = 'blank in the earlier file - unknown then, so no change is calculated'
        }
        elseif ($valueAfter.Kind -eq 'Blank') {
            $status = 'Blank'
            $note = 'blank in the later file - unknown now, so no change is calculated'
        }
        elseif (Test-SameValue $valueBefore $valueAfter) {
            $status = 'Unchanged'
            if ($valueBefore.Kind -eq 'Number') { $change = '0' }
        }
        elseif ($valueBefore.Kind -eq 'Number' -and $valueAfter.Kind -eq 'Number') {
            $change = Format-Change ($valueAfter.Number - $valueBefore.Number) ''
        }
        elseif ($valueBefore.Kind -eq 'Date' -and $valueAfter.Kind -eq 'Date') {
            $change = Format-Change ($valueAfter.Number - $valueBefore.Number) 'days'
        }

        $rows.Add([pscustomobject]@{
            Key        = $key
            Before     = $valueBefore
            After      = $valueAfter
            Change     = $change
            Note       = $note
            Status     = $status
        })
    }
    return , $rows
}

#endregion

#region Writing

function Limit-Text([string] $Text, [int] $Width) {
    if ($null -eq $Text) { return '' }
    $flat = $Text.Replace("`r", ' ').Replace("`n", ' ')
    if ($flat.Length -le $Width) { return $flat }
    return $flat.Substring(0, $Width - 3) + '...'
}

function Get-PeriodLine($Workbook) {
    $generated = Get-ReportText $Workbook 'Generated (UTC)'
    $from = Get-ReportText $Workbook 'From (UTC)'
    $to = Get-ReportText $Workbook 'To (UTC)'
    $period = Get-ReportText $Workbook 'Period covered'
    $build = Get-ReportText $Workbook 'Product build'
    return "generated $generated, period $from to $to ($period), build $build"
}

function Get-FailedCheckLines($Checks) {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($check in $Checks) {
        if ($check.Match) { continue }
        if ($check.Name -eq 'Settings') {
            $count = @($check.Details).Count
            $lines.Add("  Settings: $count option$(if ($count -ne 1) { 's' }) differ$(if ($count -eq 1) { 's' }).")
        }
        else {
            $lines.Add("  $($check.Name): before $(Show-Value $check.Before), after $(Show-Value $check.After).")
        }
        foreach ($detail in @($check.Details)) {
            $lines.Add("      $($detail.Key): before $(Show-Value $detail.Before), after $(Show-Value $detail.After)")
        }
        $lines.Add("      Why it matters: $($check.Why)")
    }
    return , $lines
}

function ConvertTo-CsvField([string] $Text, [bool] $Defuse) {
    if ($null -eq $Text) { $Text = '' }
    if ($Defuse -and $Text.Length -gt 0 -and $script:FormulaTriggers -contains $Text.Substring(0, 1)) {
        # The apostrophe is inside the quotes, where Excel looks for it; it is not shown in the cell.
        $Text = "'" + $Text
    }
    return '"' + $Text.Replace('"', '""') + '"'
}

function ConvertTo-CsvLine([string] $Section, [string] $Key, $BeforeValue, $AfterValue, [string] $Change, [string] $Note) {
    $fields = @(
        (ConvertTo-CsvField $Section $false),
        (ConvertTo-CsvField $Key $true),
        (ConvertTo-CsvField $BeforeValue.Text ($BeforeValue.Kind -eq 'Text')),
        (ConvertTo-CsvField $AfterValue.Text ($AfterValue.Kind -eq 'Text')),
        (ConvertTo-CsvField $Change $false),
        (ConvertTo-CsvField $Note $false)
    )
    return ($fields -join ',')
}

function Write-CsvReport($Checks, $Cautions, $Rows, [bool] $Forced) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('"Section","Key","Before","After","Change","Note"')

    foreach ($check in $Checks) {
        $note = if ($check.Match) { 'match' } elseif ($Forced) { 'MISMATCH - compared anyway (-Force)' } else { 'MISMATCH' }
        $lines.Add((ConvertTo-CsvLine 'Comparability' $check.Name (New-CellValue 'Text' $check.Before $null) (New-CellValue 'Text' $check.After $null) '' $note))
        foreach ($detail in @($check.Details)) {
            $lines.Add((ConvertTo-CsvLine 'Comparability' "$($check.Name): $($detail.Key)" (New-CellValue 'Text' $detail.Before $null) (New-CellValue 'Text' $detail.After $null) '' 'differs'))
        }
    }

    foreach ($caution in $Cautions) {
        $lines.Add((ConvertTo-CsvLine 'Caution' $caution.Name (New-BlankValue) (New-BlankValue) '' $caution.Text))
    }

    foreach ($row in $Rows) {
        if ($row.Status -eq 'Unchanged' -and -not $IncludeUnchanged) { continue }
        $lines.Add((ConvertTo-CsvLine 'Snapshot facts' $row.Key $row.Before $row.After $row.Change $row.Note))
    }

    return , $lines
}

function Write-TextReport($Earlier, $Later, $Checks, $Cautions, $Rows, [bool] $Forced) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('Copilot Adoption workbook comparison')
    $lines.Add('====================================')
    $lines.Add("Before: $($Earlier.Path)")
    $lines.Add("        $(Get-PeriodLine $Earlier)")
    $lines.Add("After:  $($Later.Path)")
    $lines.Add("        $(Get-PeriodLine $Later)")
    $lines.Add('')

    $failed = @($Checks | Where-Object { -not $_.Match })
    if ($failed.Count -gt 0 -and $Forced) {
        $lines.Add('!!! COMPARED DESPITE FAILED CHECKS (-Force) !!!')
        $lines.Add('These files fail the checks below, so a change in a figure may come from the product, the')
        $lines.Add('settings, the period or the population rather than from adoption:')
        foreach ($line in (Get-FailedCheckLines $Checks)) { $lines.Add($line) }
        $lines.Add('')
    }

    $lines.Add('Comparability')
    foreach ($check in $Checks) {
        $status = if ($check.Match) { 'OK      ' } else { 'MISMATCH' }
        $shown = if ($check.Name -eq 'Settings') {
            if ($check.Match) { "$($check.Compared) options identical (fromUtc, toUtc and toExclusiveUtc place the period in time and are not compared)" }
            else { "$(@($check.Details).Count) of $($check.Compared) options differ" }
        }
        elseif ($check.Match) { $check.Before }
        else { "$(Show-Value $check.Before) | $(Show-Value $check.After)" }
        $lines.Add(('  {0}  {1,-24}  {2}' -f $status, $check.Name, $shown))
    }
    $lines.Add('')

    if ($Cautions.Count -gt 0) {
        $lines.Add('Cautions')
        foreach ($caution in $Cautions) {
            $lines.Add("  - $($caution.Name): $($caution.Text)")
        }
        $lines.Add('')
    }

    $changed = @($Rows | Where-Object { $_.Status -eq 'Changed' }).Count
    $added = @($Rows | Where-Object { $_.Status -eq 'Added' }).Count
    $removed = @($Rows | Where-Object { $_.Status -eq 'Removed' }).Count
    $blank = @($Rows | Where-Object { $_.Status -eq 'Blank' }).Count
    $unchanged = @($Rows | Where-Object { $_.Status -eq 'Unchanged' }).Count
    $lines.Add("Snapshot facts: $changed changed, $added added in a later build, $removed removed in the later build, $blank blank in one file, $unchanged unchanged.")
    $lines.Add('A blank value is unknown, never zero, so no change is calculated for it.')
    $lines.Add('')

    $listed = @($Rows | Where-Object { $IncludeUnchanged -or $_.Status -ne 'Unchanged' })
    if ($listed.Count -eq 0) {
        $lines.Add('No figure changed.')
        return , $lines
    }

    $keyWidth = 3
    foreach ($row in $listed) { $keyWidth = [Math]::Max($keyWidth, [Math]::Min(64, $row.Key.Length)) }
    $valueWidth = 22
    $changeWidth = 14
    $layout = '{0,-' + $keyWidth + '}  {1,-' + $valueWidth + '}  {2,-' + $valueWidth + '}  {3,-' + $changeWidth + '}  {4}'
    $lines.Add(($layout -f 'Key', 'Before', 'After', 'Change', 'Note').TrimEnd())
    $lines.Add(($layout -f '---', '------', '-----', '------', '----').TrimEnd())
    foreach ($row in $listed) {
        $lines.Add(($layout -f (Limit-Text $row.Key 64), (Limit-Text $row.Before.Text $valueWidth), (Limit-Text $row.After.Text $valueWidth), $row.Change, $row.Note).TrimEnd())
    }
    $lines.Add('')
    $lines.Add('Values longer than the column are shortened here; -Format Csv carries them in full.')

    return , $lines
}

function Write-ErrorLines($Lines) {
    foreach ($line in $Lines) {
        [Console]::Error.WriteLine($line)
    }
}

#endregion

try {
    Add-Type -AssemblyName System.IO.Compression

    $earlier = Read-CopilotAdoptionWorkbook $Before 'before'
    $later = Read-CopilotAdoptionWorkbook $After 'after'
}
catch {
    Write-ErrorLines @("Could not compare the workbooks: $($_.Exception.Message)")
    exit 2
}

try {
    $checks = Test-Comparable $earlier $later
    $failed = @($checks | Where-Object { -not $_.Match })

    if ($failed.Count -gt 0) {
        $explanation = New-Object System.Collections.Generic.List[string]
        $explanation.Add("$(if ($Force) { 'WARNING - COMPARED ANYWAY (-Force)' } else { 'NOT COMPARABLE' }): these files fail $($failed.Count) of the $($checks.Count) checks a fair comparison needs.")
        $explanation.Add("  Before: $($earlier.Path)")
        $explanation.Add("  After:  $($later.Path)")
        $explanation.Add('')
        foreach ($line in (Get-FailedCheckLines $checks)) { $explanation.Add($line) }
        $explanation.Add('')
        if (-not $Force) {
            $explanation.Add('Nothing was compared. Re-export the earlier period under the current build and settings - an explicit')
            $explanation.Add('date range on the Copilot Adoption page - with the same period length and population, or run again')
            $explanation.Add('with -Force to compare anyway.')
            Write-ErrorLines $explanation
            exit 1
        }
        Write-ErrorLines $explanation
    }

    $cautions = Get-Cautions $earlier $later
    $rows = Compare-Facts $earlier $later

    if ($Format -eq 'Csv') {
        $report = Write-CsvReport $checks $cautions $rows ([bool] $Force)
    }
    else {
        $report = Write-TextReport $earlier $later $checks $cautions $rows ([bool] $Force)
    }

    if ($OutFile) {
        $target = Get-FullPath $OutFile
        [System.IO.File]::WriteAllLines($target, [string[]] $report.ToArray(), (New-Object System.Text.UTF8Encoding $true))
        Write-Output "Wrote the comparison to $target"
    }
    else {
        foreach ($line in $report) {
            Write-Output $line
        }
    }
    exit 0
}
catch {
    Write-ErrorLines @("Could not compare the workbooks: $($_.Exception.Message)")
    exit 2
}
