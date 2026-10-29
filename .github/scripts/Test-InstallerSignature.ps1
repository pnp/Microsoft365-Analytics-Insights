[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$ExpectedThumbprint
)
$ErrorActionPreference = 'Stop'
$signature = Get-AuthenticodeSignature -LiteralPath $Path
if (-not $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $ExpectedThumbprint -or
    $signature.Status -eq 'HashMismatch') {
    throw 'Installer is unsigned, has been modified, or was signed by a different certificate.'
}
Add-Type -AssemblyName System.Reflection.Metadata
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
$pe = $null
try {
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    $directory = $pe.PEHeaders.PEHeader.CertificateTableDirectory
    if ($directory.Size -lt 8) { throw 'Installer has no embedded signature.' }
    $stream.Position = $directory.RelativeVirtualAddress
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
    try {
        $length = $reader.ReadUInt32()
        $revision = $reader.ReadUInt16()
        $type = $reader.ReadUInt16()
        if ($length -lt 8 -or $length -gt $directory.Size -or $revision -ne 0x200 -or $type -ne 2) {
            throw 'Invalid installer signature record.'
        }
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode($reader.ReadBytes($length - 8))
    }
    finally { $reader.Dispose() }
    # Verify the embedded cryptographic signature without requiring public-CA chain trust.
    $cms.CheckSignature($true)
    if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -ne $ExpectedThumbprint) {
        throw 'Installer signature does not match the supplied signing certificate.'
    }
}
finally {
    if ($pe) { $pe.Dispose() }
    $stream.Dispose()
}
Write-Output 'Installer signature matches the supplied signing certificate.'
