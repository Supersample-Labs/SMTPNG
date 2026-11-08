param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{40,128}$')]
    [string]$Thumbprint,
    [string]$FriendlyName = 'SMTPNG',
    [ValidateSet('My', 'WebHosting')]
    [string]$StoreName = 'My'
)
$ErrorActionPreference = 'Stop'
$certificate = Get-Item -LiteralPath "Cert:\LocalMachine\$StoreName\$Thumbprint"
if (-not $certificate.HasPrivateKey) { throw 'The renewed certificate does not have a private key.' }
$certificate.FriendlyName = $FriendlyName
Write-Output "SMTPNG: updated certificate friendly name to '$FriendlyName' in LocalMachine\$StoreName. New STARTTLS connections select the renewed certificate automatically."
