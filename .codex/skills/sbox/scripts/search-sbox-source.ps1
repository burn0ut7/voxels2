[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $Pattern,

	[string] $ProjectPath = (Get-Location).Path,

	[string] $EnginePath = "C:\Program Files (x86)\Steam\steamapps\common\sbox",

	[string] $CacheRoot = (Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Codex\sbox-reference"),

	[ValidateSet("Workspace", "Installed", "Docs", "Public")]
	[string[]] $Surface = @("Workspace", "Installed", "Docs", "Public"),

	[string[]] $Glob = @("*.cs", "*.razor", "*.scss", "*.md"),

	[int] $Limit = 100,

	[switch] $FixedString
)

$ErrorActionPreference = "Stop"

if ( $Limit -lt 1 )
{
	throw "Limit must be greater than zero."
}

if ( -not (Get-Command rg -ErrorAction SilentlyContinue) )
{
	throw "ripgrep (rg) is required for source search."
}

function Get-Revision
{
	param( [string] $Path )

	if ( Test-Path -LiteralPath (Join-Path $Path ".git") )
	{
		$revision = git -C $Path rev-parse HEAD
		if ( $LASTEXITCODE -eq 0 ) { return $revision }
	}

	return "installed"
}

$roots = [System.Collections.Generic.List[object]]::new()

if ( "Workspace" -in $Surface -and (Test-Path -LiteralPath $ProjectPath) )
{
	$roots.Add( [pscustomobject]@{ Surface = "Workspace"; Path = (Resolve-Path -LiteralPath $ProjectPath).Path; Revision = "workspace" } )
}
elseif ( "Workspace" -in $Surface )
{
	Write-Warning "Workspace search root is unavailable: $ProjectPath"
}

if ( "Installed" -in $Surface )
{
	$installedRootCount = $roots.Count
	foreach ( $relativePath in @("addons\base\code", "addons\tools\Code", "samples", "templates") )
	{
		$path = Join-Path $EnginePath $relativePath
		if ( Test-Path -LiteralPath $path )
		{
			$roots.Add( [pscustomobject]@{ Surface = "Installed:$relativePath"; Path = $path; Revision = "installed" } )
		}
	}
	if ( $roots.Count -eq $installedRootCount )
	{
		Write-Warning "Installed source roots are unavailable under: $EnginePath"
	}
}

if ( "Docs" -in $Surface )
{
	$path = Join-Path $CacheRoot "sbox-docs"
	if ( Test-Path -LiteralPath $path )
	{
		$roots.Add( [pscustomobject]@{ Surface = "Docs"; Path = $path; Revision = Get-Revision -Path $path } )
	}
	else
	{
		Write-Warning "Docs cache is unavailable: $path. Run refresh-reference-cache.ps1 to create it."
	}
}

if ( "Public" -in $Surface )
{
	$path = Join-Path $CacheRoot "sbox-public"
	if ( Test-Path -LiteralPath $path )
	{
		$roots.Add( [pscustomobject]@{ Surface = "Public"; Path = $path; Revision = Get-Revision -Path $path } )
	}
	else
	{
		Write-Warning "Public source cache is unavailable: $path. Run refresh-reference-cache.ps1 to create it."
	}
}

$results = [System.Collections.Generic.List[object]]::new()

foreach ( $root in $roots )
{
	if ( $results.Count -ge $Limit ) { break }

	$arguments = [System.Collections.Generic.List[string]]::new()
	$arguments.Add( "--json" )
	$arguments.Add( "--line-number" )
	$arguments.Add( "--no-messages" )
	$arguments.Add( "--ignore-case" )
	if ( $FixedString ) { $arguments.Add( "--fixed-strings" ) }
	foreach ( $item in $Glob )
	{
		$arguments.Add( "--glob" )
		$arguments.Add( $item )
	}
	$arguments.Add( "--" )
	$arguments.Add( $Pattern )
	$arguments.Add( $root.Path )

	$remaining = $Limit - $results.Count
	$matchLines = @(& rg @arguments | Where-Object { $_ -match '^\{"type":"match"' } | Select-Object -First $remaining)
	$searchExitCode = $LASTEXITCODE

	foreach ( $line in $matchLines )
	{
		$record = $line | ConvertFrom-Json
		$results.Add( [pscustomobject]@{
			Surface = $root.Surface
			Revision = $root.Revision
			Path = $record.data.path.text
			LineNumber = $record.data.line_number
			Line = $record.data.lines.text.TrimEnd( "`r", "`n" )
		} )
	}

	if ( $searchExitCode -notin @(0, 1) -and $results.Count -lt $Limit )
	{
		throw "rg failed for $($root.Path) with exit code $searchExitCode."
	}
}

$results
