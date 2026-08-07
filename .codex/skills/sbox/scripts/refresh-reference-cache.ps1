[CmdletBinding()]
param(
	[string] $CacheRoot = (Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Codex\sbox-reference"),

	[string] $ApiSchemaUrl,

	[string] $EnginePath = "C:\Program Files (x86)\Steam\steamapps\common\sbox",

	[string] $EngineVersion = "not-supplied",

	[switch] $Offline
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
$manifestPath = Join-Path $CacheRoot "manifest.json"
$previousManifest = $null

if ( Test-Path -LiteralPath $manifestPath )
{
	try
	{
		$previousManifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
	}
	catch
	{
		throw "Reference cache manifest is invalid: $manifestPath. $($_.Exception.Message)"
	}
}

$networkErrors = [System.Collections.Generic.List[string]]::new()
$schemaPageUrl = "https://sbox.game/api/schema"

function Test-ImmutableSchemaUrl
{
	param( [string] $Url )

	$uri = $null
	if ( -not [Uri]::TryCreate( $Url, [UriKind]::Absolute, [ref]$uri ) ) { return $false }

	return $uri.Scheme -eq "https" -and
		$uri.Host -eq "cdn.sbox.game" -and
		$uri.AbsolutePath -match '^/releases/\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}\.zip\.json$' -and
		[string]::IsNullOrEmpty( $uri.Query ) -and
		[string]::IsNullOrEmpty( $uri.Fragment )
}

function Resolve-LatestSchemaUrl
{
	$response = Invoke-WebRequest -UseBasicParsing -Headers @{
		"User-Agent" = "CodexBot sbox-reference-cache/1.0"
	} -Uri $schemaPageUrl

	$urls = @([regex]::Matches(
		$response.Content,
		'https://cdn\.sbox\.game/releases/\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}\.zip\.json'
	) | ForEach-Object { $_.Value } | Sort-Object -Unique -Descending)

	if ( $urls.Count -eq 0 )
	{
		throw "The official schema page did not expose an immutable schema URL: $schemaPageUrl"
	}

	$url = $urls[0]
	if ( -not (Test-ImmutableSchemaUrl -Url $url) )
	{
		throw "The official schema page returned an unexpected schema URL: $url"
	}

	return $url
}

if ( $Offline -and $ApiSchemaUrl )
{
	throw "-ApiSchemaUrl cannot be combined with -Offline."
}

if ( $ApiSchemaUrl -and -not (Test-ImmutableSchemaUrl -Url $ApiSchemaUrl) )
{
	throw "-ApiSchemaUrl must be an immutable HTTPS s&box schema URL shaped like https://cdn.sbox.game/releases/YYYY-MM-DD-HH-MM-SS.zip.json."
}

function Sync-Repository
{
	param(
		[string] $Name,
		[string] $Url,
		[string[]] $SparsePatterns,
		[bool] $UseOffline
	)

	$target = Join-Path $CacheRoot $Name

	if ( Test-Path -LiteralPath (Join-Path $target ".git") )
	{
		$dirty = git -C $target status --porcelain
		if ( $LASTEXITCODE -ne 0 ) { throw "Unable to inspect repository: $target" }
		if ( $dirty ) { throw "Reference cache has local changes and was not updated: $target" }

		if ( $SparsePatterns )
		{
			$SparsePatterns | git -C $target sparse-checkout set --no-cone --stdin | Out-Host
			if ( $LASTEXITCODE -ne 0 ) { throw "Unable to apply sparse checkout: $target" }
		}

		$status = "offline-existing"
		if ( -not $UseOffline )
		{
			git -C $target pull --ff-only --depth 1 origin master | Out-Host
			if ( $LASTEXITCODE -eq 0 )
			{
				$status = "refreshed"
			}
			else
			{
				$status = "stale-network-failure"
				$networkErrors.Add( "Unable to update repository: $target" )
			}
		}
	}
	else
	{
		if ( $UseOffline )
		{
			throw "Reference cache is absent and cannot be created offline: $target"
		}

		if ( $SparsePatterns )
		{
			git clone --depth 1 --filter=blob:none --no-checkout --branch master $Url $target | Out-Host
			if ( $LASTEXITCODE -ne 0 ) { throw "Unable to clone repository: $Url" }

			$SparsePatterns | git -C $target sparse-checkout set --no-cone --stdin | Out-Host
			if ( $LASTEXITCODE -ne 0 ) { throw "Unable to configure sparse checkout: $target" }

			git -C $target checkout master | Out-Host
			if ( $LASTEXITCODE -ne 0 ) { throw "Unable to check out repository: $target" }
		}
		else
		{
			git clone --depth 1 --filter=blob:none --branch master $Url $target | Out-Host
			if ( $LASTEXITCODE -ne 0 ) { throw "Unable to clone repository: $Url" }
		}

		$status = "cloned"
	}

	$commit = git -C $target rev-parse HEAD
	if ( $LASTEXITCODE -ne 0 ) { throw "Unable to read repository revision: $target" }
	$commitDate = git -C $target show -s --format=%cI HEAD
	if ( $LASTEXITCODE -ne 0 ) { throw "Unable to read repository commit date: $target" }

	[pscustomobject]@{
		Name = $Name
		Url = $Url
		Path = $target
		Commit = $commit
		CommitDate = $commitDate
		Status = $status
	}
}

$repositories = @(
	Sync-Repository -Name "sbox-docs" -Url "https://github.com/Facepunch/sbox-docs.git" -SparsePatterns @("/*.md", "/docs/**/*.md") -UseOffline $Offline.IsPresent
	Sync-Repository -Name "sbox-public" -Url "https://github.com/Facepunch/sbox-public.git" -SparsePatterns @() -UseOffline $Offline.IsPresent
)

$schemaPath = Join-Path $CacheRoot "api.json"
$schemaRefreshed = $false
$schemaDownloadError = $null
$schemaResolutionError = $null
$schemaResolution = $(if ( $Offline ) { "offline-existing" } elseif ( $ApiSchemaUrl ) { "explicit-override" } else { "official-page" })

if ( -not $Offline -and -not $ApiSchemaUrl )
{
	try
	{
		$ApiSchemaUrl = Resolve-LatestSchemaUrl
	}
	catch
	{
		$schemaResolutionError = $_.Exception.Message
		$schemaResolution = "stale-resolution-failure"
		$networkErrors.Add( "Unable to resolve the latest API schema from $schemaPageUrl. $schemaResolutionError" )
		if ( -not (Test-Path -LiteralPath $schemaPath) ) { throw }
	}
}

if ( $ApiSchemaUrl )
{
	$downloadPath = Join-Path $CacheRoot "api.json.download"
	try
	{
		Invoke-WebRequest -UseBasicParsing -Uri $ApiSchemaUrl -OutFile $downloadPath
		$downloadedSchema = Get-Content -Raw -LiteralPath $downloadPath | ConvertFrom-Json
		if ( $null -eq $downloadedSchema.Types -or @($downloadedSchema.Types).Count -eq 0 )
		{
			throw "Downloaded schema does not contain a non-empty Types collection."
		}

		Move-Item -Force -LiteralPath $downloadPath -Destination $schemaPath
		$schemaRefreshed = $true
	}
	catch
	{
		$schemaDownloadError = $_.Exception.Message
		$networkErrors.Add( "Unable to refresh API schema from $ApiSchemaUrl. $schemaDownloadError" )
		if ( -not (Test-Path -LiteralPath $schemaPath) ) { throw }
	}
	finally
	{
		if ( Test-Path -LiteralPath $downloadPath ) { Remove-Item -LiteralPath $downloadPath }
	}
}
elseif ( -not (Test-Path -LiteralPath $schemaPath) )
{
	$schemaPath = $null
}

$schemaSourceUrl = $ApiSchemaUrl
if ( -not $schemaSourceUrl -and $previousManifest.Schema.SourceUrl )
{
	$schemaSourceUrl = $previousManifest.Schema.SourceUrl
}

$schema = $null
if ( $schemaPath )
{
	$schemaFile = Get-Item -LiteralPath $schemaPath
	$schema = [pscustomobject]@{
		Path = $schemaFile.FullName
		SourceUrl = $schemaSourceUrl
		DiscoveryPageUrl = $schemaPageUrl
		Release = $(if ( $schemaSourceUrl -match '/releases/(?<release>[^/]+)\.zip\.json$' ) { $Matches.release } else { $null })
		Resolution = $schemaResolution
		Refreshed = $schemaRefreshed
		LastWriteTimeUtc = $schemaFile.LastWriteTimeUtc.ToString( "o" )
		Bytes = $schemaFile.Length
		Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $schemaFile.FullName).Hash.ToLowerInvariant()
		Error = $(if ( $schemaDownloadError ) { $schemaDownloadError } else { $schemaResolutionError })
	}
}

$assemblyPath = Join-Path $EnginePath "bin\managed\Sandbox.Engine.dll"
$installedEngine = [pscustomobject]@{
	EditorVersion = $EngineVersion
	EnginePath = $EnginePath
	AssemblyPath = $(if ( Test-Path -LiteralPath $assemblyPath ) { $assemblyPath } else { $null })
	AssemblyFileVersion = $(if ( Test-Path -LiteralPath $assemblyPath ) { (Get-Item -LiteralPath $assemblyPath).VersionInfo.FileVersion } else { $null })
	AssemblySha256 = $(if ( Test-Path -LiteralPath $assemblyPath ) { (Get-FileHash -Algorithm SHA256 -LiteralPath $assemblyPath).Hash.ToLowerInvariant() } else { $null })
}

$manifest = [pscustomobject]@{
	FormatVersion = 2
	CacheRoot = $CacheRoot
	ManifestPath = $manifestPath
	RefreshedAtUtc = [DateTime]::UtcNow.ToString("o")
	Offline = $Offline.IsPresent
	InstalledEngine = $installedEngine
	Repositories = $repositories
	Schema = $schema
	NetworkErrors = @($networkErrors)
}

$json = $manifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText( $manifestPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new( $false ) )
$json
