$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Reflection.Metadata
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$root = Join-Path ([IO.Path]::GetTempPath()) ('contoso-signature-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$certificate = $null
function Assert-SignatureFails([string]$Path, [string]$Thumbprint) {
    $failed = $false
    try { & "$PSScriptRoot\Test-InstallerSignature.ps1" -Path $Path -ExpectedThumbprint $Thumbprint | Out-Null }
    catch { $failed = $true }
    if (-not $failed) { throw 'An unsigned, changed or wrong-signer file passed signature validation.' }
}
try {
    $certificate = New-SelfSignedCertificate -Subject 'CN=Contoso CI signature fixture' -Type CodeSigningCert `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddDays(1)
    $file = Join-Path $root 'contoso.exe'
    $bytes = [IO.File]::ReadAllBytes((Join-Path $PSHOME 'pwsh.exe'))
    $pe = [Reflection.PortableExecutable.PEReader]::new([IO.MemoryStream]::new($bytes, $false))
    try {
        $securityEntry = $pe.PEHeaders.PEHeaderStartOffset +
            $(if ($pe.PEHeaders.PEHeader.Magic -eq 'PE32Plus') { 144 } else { 128 })
        $contentOffset = $pe.PEHeaders.SectionHeaders[0].PointerToRawData
        [Array]::Clear($bytes, $securityEntry, 8)
    }
    finally { $pe.Dispose() }
    [IO.File]::WriteAllBytes($file, $bytes)
    Assert-SignatureFails $file $certificate.Thumbprint
    Set-AuthenticodeSignature -FilePath $file -Certificate $certificate -HashAlgorithm SHA256 | Out-Null
    & "$PSScriptRoot\Test-InstallerSignature.ps1" -Path $file -ExpectedThumbprint $certificate.Thumbprint | Out-Null
    Assert-SignatureFails $file ('0' * 40)
    $valid = [IO.File]::ReadAllBytes($file)
    $pe = [Reflection.PortableExecutable.PEReader]::new([IO.MemoryStream]::new($valid, $false))
    try { $signatureOffset = $pe.PEHeaders.PEHeader.CertificateTableDirectory.RelativeVirtualAddress + 8 }
    finally { $pe.Dispose() }
    $length = [BitConverter]::ToUInt32($valid, $signatureOffset - 8) - 8
    $encoded = $valid[$signatureOffset..($signatureOffset + $length - 1)]
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode([byte[]]$encoded)
    $index = [Convert]::ToHexString([byte[]]$encoded).IndexOf(
        [Convert]::ToHexString($cms.SignerInfos[0].GetSignature()), [StringComparison]::Ordinal)
    if ($index -lt 0) { throw 'Synthetic signature fixture has no signer signature.' }
    $changed = $valid.Clone()
    $offset = $signatureOffset + $index / 2
    $changed[$offset] = $changed[$offset] -bxor 1
    [IO.File]::WriteAllBytes($file, $changed)
    Assert-SignatureFails $file $certificate.Thumbprint
    $changed = $valid.Clone()
    $changed[$contentOffset] = $changed[$contentOffset] -bxor 1
    [IO.File]::WriteAllBytes($file, $changed)
    Assert-SignatureFails $file $certificate.Thumbprint
    Write-Output 'Private-CA signer, unsigned-file, wrong-signer, broken-signature and modified-file checks passed.'
}
finally {
    if ($certificate) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -DeleteKey
        $certificate.Dispose()
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
