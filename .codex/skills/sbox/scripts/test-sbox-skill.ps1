[CmdletBinding()]
param(
	[string] $EnginePath = "C:\Program Files (x86)\Steam\steamapps\common\sbox",

	[string] $ProjectPath = (Get-Location).Path,

	[switch] $KeepTemporary
)

$ErrorActionPreference = "Stop"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("sbox-skill-test-" + [guid]::NewGuid().ToString("N"))
$results = [System.Collections.Generic.List[object]]::new()
[IO.Directory]::CreateDirectory( $temporaryRoot ) | Out-Null

function Assert-True
{
	param( [bool] $Condition, [string] $Message )

	if ( -not $Condition ) { throw $Message }
}

function Invoke-Case
{
	param( [string] $Name, [scriptblock] $Body )

	$stopwatch = [Diagnostics.Stopwatch]::StartNew()
	try
	{
		$null = & $Body
		$results.Add( [pscustomobject]@{
			Name = $Name
			Passed = $true
			Milliseconds = $stopwatch.ElapsedMilliseconds
			Error = $null
		} )
	}
	catch
	{
		$results.Add( [pscustomobject]@{
			Name = $Name
			Passed = $false
			Milliseconds = $stopwatch.ElapsedMilliseconds
			Error = $_.Exception.Message
		} )
	}
	finally
	{
		$stopwatch.Stop()
	}
}

function Write-Utf8
{
	param( [string] $Path, [string] $Text )

	$parent = Split-Path $Path -Parent
	if ( $parent ) { [IO.Directory]::CreateDirectory( $parent ) | Out-Null }
	[IO.File]::WriteAllText( $Path, $Text, [Text.UTF8Encoding]::new( $false ) )
}

function Initialize-FixtureRepository
{
	param( [string] $Path )

	& git init --initial-branch=master $Path | Out-Null
	if ( $LASTEXITCODE -ne 0 ) { throw "Unable to initialize fixture repository: $Path" }
	& git -C $Path config core.autocrlf false
	if ( $LASTEXITCODE -ne 0 ) { throw "Unable to configure fixture repository: $Path" }
	& git -C $Path add --all
	if ( $LASTEXITCODE -ne 0 ) { throw "Unable to stage fixture repository: $Path" }
	& git -C $Path -c user.name=sbox-skill-test -c user.email=sbox-skill-test@example.invalid commit -m fixture | Out-Null
	if ( $LASTEXITCODE -ne 0 ) { throw "Unable to commit fixture repository: $Path" }
}

$workspaceFixture = Join-Path $temporaryRoot "workspace"
$cacheFixture = Join-Path $temporaryRoot "cache"
$docsFixture = Join-Path $cacheFixture "sbox-docs"
$publicFixture = Join-Path $cacheFixture "sbox-public"
$schemaFixture = Join-Path $cacheFixture "api.json"
$fixtureToken = "sbox-regression-fixture-token"

try
{
	Write-Utf8 -Path (Join-Path $workspaceFixture "Fixture.cs") -Text "// $fixtureToken`n"
	Write-Utf8 -Path (Join-Path $docsFixture "fixture.md") -Text "# $fixtureToken`n"
	Write-Utf8 -Path (Join-Path $publicFixture "Fixture.cs") -Text "// $fixtureToken`n"

	$schemaDocument = [ordered]@{
		Types = @(
			[ordered]@{
				Assembly = "Sandbox.Engine"
				Name = "RegressionFixture"
				FullName = "Sandbox.RegressionFixture"
				DocId = "T:Sandbox.RegressionFixture"
				Documentation = [ordered]@{ Summary = $fixtureToken }
				Methods = @(
					[ordered]@{
						Name = "Run"
						FullName = "Sandbox.RegressionFixture.Run"
						DocId = "M:Sandbox.RegressionFixture.Run"
						ReturnType = "System.Void"
						Parameters = @()
						IsPublic = $true
						Attributes = @()
						Documentation = [ordered]@{ Summary = $fixtureToken }
					}
				)
			}
		)
	}
	Write-Utf8 -Path $schemaFixture -Text (($schemaDocument | ConvertTo-Json -Depth 10) + [Environment]::NewLine)

	Initialize-FixtureRepository -Path $docsFixture
	Initialize-FixtureRepository -Path $publicFixture

	$sourceUrl = "https://cdn.sbox.game/releases/2000-01-02-03-04-05.zip.json"
	$seedManifest = [ordered]@{
		FormatVersion = 1
		Schema = [ordered]@{ SourceUrl = $sourceUrl }
	}
	Write-Utf8 -Path (Join-Path $cacheFixture "manifest.json") -Text (($seedManifest | ConvertTo-Json -Depth 5) + [Environment]::NewLine)

	Invoke-Case -Name "PowerShell syntax" -Body {
		$syntaxErrors = [System.Collections.Generic.List[object]]::new()
		foreach ( $scriptFile in Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.ps1" -File )
		{
			$tokens = $null
			$errors = $null
			[void][Management.Automation.Language.Parser]::ParseFile( $scriptFile.FullName, [ref]$tokens, [ref]$errors )
			foreach ( $error in $errors ) { $syntaxErrors.Add( "$($scriptFile.Name): $($error.Message)" ) }
		}
		Assert-True ($syntaxErrors.Count -eq 0) ($syntaxErrors -join "; ")
	}

	Invoke-Case -Name "Installed metadata inspector" -Body {
		$inspector = Join-Path $PSScriptRoot "inspect-installed-api.ps1"
		foreach ( $kind in @("Type", "Constructor", "Method", "Property", "Field", "Event") )
		{
			$matches = @(& $inspector -Pattern ".*" -EnginePath $EnginePath -AssemblyPattern "Sandbox.Engine.dll" -Kind $kind -Limit 1)
			Assert-True ($matches.Count -eq 1) "Inspector returned no $kind from Sandbox.Engine.dll."
		}

		$enabled = @(& $inspector -Pattern '^Sandbox\.Component\.Enabled$' -EnginePath $EnginePath -Kind Property -Limit 1)
		$active = @(& $inspector -Pattern '^Sandbox\.Component\.Active$' -EnginePath $EnginePath -Kind Property -Limit 1)
		Assert-True ($enabled.Count -eq 1 -and $null -ne $enabled[0].Setter) "Component.Enabled should expose a setter."
		Assert-True ($active.Count -eq 1 -and $null -eq $active[0].Setter) "Component.Active should remain getter-only."
	}

	Invoke-Case -Name "Source search surfaces" -Body {
		$search = Join-Path $PSScriptRoot "search-sbox-source.ps1"
		foreach ( $surface in @("Workspace", "Docs", "Public") )
		{
			$matches = @(& $search -Pattern $fixtureToken -ProjectPath $workspaceFixture -EnginePath $EnginePath -CacheRoot $cacheFixture -Surface $surface -FixedString -Limit 2)
			Assert-True ($matches.Count -ge 1 -and $matches[0].Surface -like "$surface*") "No $surface fixture result was returned."
		}
		$caseInsensitive = @(& $search -Pattern $fixtureToken.ToUpperInvariant() -ProjectPath $workspaceFixture -Surface Workspace -FixedString -Limit 1)
		Assert-True ($caseInsensitive.Count -eq 1) "Source search should be case-insensitive."
		$installed = @(& $search -Pattern "namespace Sandbox" -EnginePath $EnginePath -Surface Installed -FixedString -Limit 1)
		Assert-True ($installed.Count -eq 1 -and $installed[0].Surface -like "Installed:*") "No installed source result was returned."
	}

	Invoke-Case -Name "Cached schema search" -Body {
		$matches = @(& (Join-Path $PSScriptRoot "search-api-schema.ps1") -Pattern $fixtureToken -SchemaPath $schemaFixture -Limit 5)
		Assert-True ($matches.Count -ge 2) "Schema search did not return the fixture type and member."

		$invalidLimitRejected = $false
		try
		{
			& (Join-Path $PSScriptRoot "search-api-schema.ps1") -Pattern $fixtureToken -SchemaPath $schemaFixture -Limit 0 | Out-Null
		}
		catch
		{
			$invalidLimitRejected = $_.Exception.Message -like "*Limit must be greater than zero*"
		}
		Assert-True $invalidLimitRejected "Schema search accepted a non-positive limit."
	}

	Invoke-Case -Name "Installed XML search" -Body {
		$matches = @(& (Join-Path $PSScriptRoot "search-installed-api.ps1") -Pattern 'T:Sandbox\.Component' -EnginePath $EnginePath -Context 0)
		Assert-True ($matches.Count -ge 1) "Installed XML search did not find Sandbox.Component."

		$invalidContextRejected = $false
		try
		{
			& (Join-Path $PSScriptRoot "search-installed-api.ps1") -Pattern 'Sandbox' -EnginePath $EnginePath -Context -1 | Out-Null
		}
		catch
		{
			$invalidContextRejected = $_.Exception.Message -like "*Context must be zero or greater*"
		}
		Assert-True $invalidContextRejected "Installed XML search accepted negative context."
	}

	Invoke-Case -Name "Offline cache refresh" -Body {
		$json = & (Join-Path $PSScriptRoot "refresh-reference-cache.ps1") -CacheRoot $cacheFixture -EnginePath $EnginePath -EngineVersion "regression" -Offline
		$manifest = $json | ConvertFrom-Json
		Assert-True ($manifest.FormatVersion -eq 2) "Refresh did not write the current manifest format."
		Assert-True ($manifest.Offline -eq $true) "Refresh did not record offline mode."
		Assert-True ($manifest.Schema.SourceUrl -eq $sourceUrl) "Offline refresh did not preserve the immutable schema URL."
		Assert-True ($manifest.Schema.Resolution -eq "offline-existing") "Offline refresh recorded the wrong schema resolution."
		Assert-True (-not [string]::IsNullOrWhiteSpace( $manifest.Schema.Sha256 )) "Offline refresh did not hash the schema."
		Assert-True (@($manifest.Repositories).Count -eq 2) "Offline refresh did not account for both repositories."

		$invalidUrlRejected = $false
		try
		{
			& (Join-Path $PSScriptRoot "refresh-reference-cache.ps1") -CacheRoot $cacheFixture -ApiSchemaUrl "https://example.invalid/api.json" | Out-Null
		}
		catch
		{
			$invalidUrlRejected = $_.Exception.Message -like "*must be an immutable HTTPS s&box schema URL*"
		}
		Assert-True $invalidUrlRejected "Refresh accepted a schema URL outside the official immutable CDN shape."
	}

}
finally
{
	if ( -not $KeepTemporary )
	{
		$resolvedRoot = [IO.Path]::GetFullPath( $temporaryRoot )
		$tempPrefix = [IO.Path]::GetFullPath( [IO.Path]::GetTempPath() )
		$leaf = Split-Path $resolvedRoot -Leaf
		if ( -not $resolvedRoot.StartsWith( $tempPrefix, [StringComparison]::OrdinalIgnoreCase ) -or $leaf -notlike "sbox-skill-test-*" )
		{
			throw "Refusing to remove unexpected temporary path: $resolvedRoot"
		}
		foreach ( $file in [IO.Directory]::EnumerateFiles( $resolvedRoot, "*", [IO.SearchOption]::AllDirectories ) )
		{
			[IO.File]::SetAttributes( $file, [IO.FileAttributes]::Normal )
		}
		[IO.Directory]::Delete( $resolvedRoot, $true )
	}
}

$failed = @($results | Where-Object { -not $_.Passed })
$summary = [pscustomobject]@{
	Passed = $failed.Count -eq 0
	PassedCount = @($results | Where-Object Passed).Count
	FailedCount = $failed.Count
	TemporaryRoot = $(if ( $KeepTemporary ) { $temporaryRoot } else { $null })
	Results = @($results)
}

$summary
if ( $failed.Count -gt 0 )
{
	throw "s&box skill regression failed: $($failed.Name -join ', ')"
}
