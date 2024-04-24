Param(
    [Parameter(Mandatory=$True,Position=0)]
    [string] $PfxFilePath,
    [Parameter(Mandatory=$True,Position=1)]
    [string] $outputPath,
	[Parameter(Mandatory=$True,Position=2)]
    [string] $PfxPassword
)

# The path to the snk file we're creating
[string] $snkFileTitle = [IO.Path]::GetFileNameWithoutExtension($PfxFilePath) + ".snk";

# Read in the bytes of the pfx file
[byte[]] $pfxBytes = Get-Content $PfxFilePath -Encoding Byte;

# Get a cert object from the pfx bytes with the private key marked as exportable
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
    $pfxBytes,
    $PfxPassword,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable);

# Export a CSP blob from the cert (which is the same format as an SNK file)
[byte[]] $snkBytes = ([Security.Cryptography.RSACryptoServiceProvider]$cert.PrivateKey).ExportCspBlob($true);

# Write the CSP blob/SNK bytes to the snk file
$outputFileName = ($outputPath + "\" + $snkFileTitle)
Write-Host "Writing '$outputFileName'..."
[IO.File]::WriteAllBytes($outputFileName, $snkBytes);

