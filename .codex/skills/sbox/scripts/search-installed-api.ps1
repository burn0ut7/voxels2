[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $Pattern,

	[string] $EnginePath = "C:\Program Files (x86)\Steam\steamapps\common\sbox",

	[int] $Context = 4
)

$ErrorActionPreference = "Stop"
$managedPath = Join-Path $EnginePath "bin\managed"

if ( $Context -lt 0 )
{
	throw "Context must be zero or greater."
}

if ( -not (Test-Path -LiteralPath $managedPath) )
{
	throw "Managed engine directory not found: $managedPath. Pass -EnginePath from sbox editor_status."
}

$xmlFiles = Get-ChildItem -LiteralPath $managedPath -Filter "Sandbox.*.xml" -File

if ( $xmlFiles.Count -eq 0 )
{
	throw "No Sandbox XML documentation found in $managedPath."
}

Select-String -Path $xmlFiles.FullName -Pattern $Pattern -Context $Context, $Context |
	Select-Object Path, LineNumber, Line, Context
