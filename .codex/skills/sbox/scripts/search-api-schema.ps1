[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $Pattern,

	[string] $SchemaPath = (Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Codex\sbox-reference\api.json"),

	[int] $Limit = 50
)

$ErrorActionPreference = "Stop"

if ( $Limit -lt 1 )
{
	throw "Limit must be greater than zero."
}

if ( -not (Test-Path -LiteralPath $SchemaPath) )
{
	throw "API schema not found: $SchemaPath. Download the current api.json from https://sbox.game/api/schema."
}

$regex = [regex]::new( $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase )
$schema = Get-Content -Raw -LiteralPath $SchemaPath | ConvertFrom-Json
$results = [System.Collections.Generic.List[object]]::new()

function Add-Match
{
	param(
		[string] $Kind,
		[object] $Type,
		[object] $Member
	)

	if ( $results.Count -ge $Limit ) { return }

	$summary = $Member.Documentation.Summary
	$candidates = @($Member.Name, $Member.FullName, $Member.DocId, $summary) |
		Where-Object { $_ -is [string] }

	if ( $candidates | Where-Object { $regex.IsMatch( $_ ) } | Select-Object -First 1 )
	{
		$results.Add( [pscustomobject]@{
			Kind = $Kind
			Assembly = $Type.Assembly
			Type = $Type.FullName
			Member = $Member.FullName
			DocId = $Member.DocId
			ReturnType = $Member.ReturnType
			PropertyType = $Member.PropertyType
			FieldType = $Member.FieldType
			Parameters = @($Member.Parameters | Where-Object { $null -ne $_ })
			IsPublic = $Member.IsPublic
			Attributes = @($Member.Attributes | ForEach-Object { $_.FullName })
			Source = $(if ( $Member.Loc ) { $Member.Loc } elseif ( $Member.l ) { $Member.l } else { $null })
			Summary = $summary
		} )
	}
}

foreach ( $type in $schema.Types )
{
	Add-Match -Kind "Type" -Type $type -Member $type

	foreach ( $property in $type.PSObject.Properties )
	{
		if ( $results.Count -ge $Limit ) { break }
		if ( $property.Name -in @("Documentation", "Loc", "l") ) { continue }
		if ( $property.Value -is [string] -or $null -eq $property.Value ) { continue }

		foreach ( $member in @($property.Value) )
		{
			if ( $member -isnot [System.Management.Automation.PSCustomObject] ) { continue }
			Add-Match -Kind $property.Name -Type $type -Member $member
			if ( $results.Count -ge $Limit ) { break }
		}
	}

	if ( $results.Count -ge $Limit ) { break }
}

$results
