[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $Pattern,

	[string] $EnginePath = "C:\Program Files (x86)\Steam\steamapps\common\sbox",

	[string] $AssemblyPattern = "Sandbox.*.dll",

	[ValidateSet("All", "Type", "Constructor", "Method", "Property", "Field", "Event")]
	[string] $Kind = "All",

	[int] $Limit = 50,

	[switch] $IncludeNonPublic
)

$ErrorActionPreference = "Stop"

if ( $Limit -lt 1 )
{
	throw "Limit must be greater than zero."
}

$managedPath = Join-Path $EnginePath "bin\managed"
$cecilPath = Join-Path $managedPath "Mono.Cecil.dll"

if ( -not (Test-Path -LiteralPath $managedPath) )
{
	throw "Managed engine directory not found: $managedPath. Pass -EnginePath from sbox editor_status."
}

if ( -not (Test-Path -LiteralPath $cecilPath) )
{
	throw "Mono.Cecil.dll not found: $cecilPath."
}

if ( -not ("Mono.Cecil.ModuleDefinition" -as [type]) )
{
	Add-Type -Path $cecilPath
}

$regex = [regex]::new( $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase )
$assemblyFiles = Get-ChildItem -LiteralPath $managedPath -Filter $AssemblyPattern -File | Sort-Object Name

if ( $assemblyFiles.Count -eq 0 )
{
	throw "No assemblies matching '$AssemblyPattern' found in $managedPath."
}

function Get-MethodAccessibility
{
	param( [Mono.Cecil.MethodDefinition] $Method )

	if ( $Method.IsPublic ) { return "public" }
	if ( $Method.IsFamilyOrAssembly ) { return "protected internal" }
	if ( $Method.IsFamily ) { return "protected" }
	if ( $Method.IsFamilyAndAssembly ) { return "private protected" }
	if ( $Method.IsAssembly ) { return "internal" }
	return "private"
}

function Get-FieldAccessibility
{
	param( [Mono.Cecil.FieldDefinition] $Field )

	if ( $Field.IsPublic ) { return "public" }
	if ( $Field.IsFamilyOrAssembly ) { return "protected internal" }
	if ( $Field.IsFamily ) { return "protected" }
	if ( $Field.IsFamilyAndAssembly ) { return "private protected" }
	if ( $Field.IsAssembly ) { return "internal" }
	return "private"
}

function Get-TypeAccessibility
{
	param( [Mono.Cecil.TypeDefinition] $Type )

	if ( $Type.IsPublic -or $Type.IsNestedPublic ) { return "public" }
	if ( $Type.IsNestedFamilyOrAssembly ) { return "protected internal" }
	if ( $Type.IsNestedFamily ) { return "protected" }
	if ( $Type.IsNestedFamilyAndAssembly ) { return "private protected" }
	if ( $Type.IsNestedAssembly -or $Type.IsNotPublic ) { return "internal" }
	return "private"
}

function Test-AccessibleMethod
{
	param( [Mono.Cecil.MethodDefinition] $Method )

	return $IncludeNonPublic -or $Method.IsPublic -or $Method.IsFamily -or $Method.IsFamilyOrAssembly
}

function Test-AccessibleField
{
	param( [Mono.Cecil.FieldDefinition] $Field )

	return $IncludeNonPublic -or $Field.IsPublic -or $Field.IsFamily -or $Field.IsFamilyOrAssembly
}

function Test-AccessibleType
{
	param( [Mono.Cecil.TypeDefinition] $Type )

	return $IncludeNonPublic -or $Type.IsPublic -or $Type.IsNestedPublic -or $Type.IsNestedFamily -or $Type.IsNestedFamilyOrAssembly
}

function Get-AllTypes
{
	param( [System.Collections.IEnumerable] $Types )

	foreach ( $type in $Types )
	{
		$type
		if ( $type.HasNestedTypes )
		{
			Get-AllTypes -Types $type.NestedTypes
		}
	}
}

function Get-AttributeNames
{
	param( [Mono.Collections.Generic.Collection[Mono.Cecil.CustomAttribute]] $Attributes )

	@($Attributes | ForEach-Object { $_.AttributeType.FullName })
}

function Get-GenericParameterText
{
	param( [System.Collections.IEnumerable] $GenericParameters )

	$items = @($GenericParameters | ForEach-Object {
		$constraints = @($_.Constraints | ForEach-Object { $_.ConstraintType.FullName })
		[pscustomobject]@{
			Name = $_.Name
			Attributes = $_.Attributes.ToString()
			Constraints = $constraints
		}
	})

	return $items
}

function Get-ParameterInfo
{
	param( [System.Collections.IEnumerable] $Parameters )

	@($Parameters | ForEach-Object {
		[pscustomobject]@{
			Name = $_.Name
			Type = $_.ParameterType.FullName
			IsIn = $_.IsIn
			IsOut = $_.IsOut
			IsOptional = $_.IsOptional
			HasDefault = $_.HasDefault
			Default = $(if ( $_.HasDefault ) { $_.Constant } else { $null })
			Attributes = Get-AttributeNames -Attributes $_.CustomAttributes
		}
	})
}

function Test-Match
{
	param( [string[]] $Candidates )

	foreach ( $candidate in $Candidates )
	{
		if ( $candidate -and $regex.IsMatch( $candidate ) ) { return $true }
	}

	return $false
}

function Get-MethodModifiers
{
	param( [Mono.Cecil.MethodDefinition] $Method )

	$modifiers = [System.Collections.Generic.List[string]]::new()
	if ( $Method.IsStatic ) { $modifiers.Add( "static" ) }
	if ( $Method.IsAbstract ) { $modifiers.Add( "abstract" ) }
	elseif ( $Method.IsVirtual -and -not $Method.IsNewSlot ) { $modifiers.Add( "override" ) }
	elseif ( $Method.IsVirtual ) { $modifiers.Add( "virtual" ) }
	if ( $Method.IsFinal -and $Method.IsVirtual ) { $modifiers.Add( "sealed" ) }
	return @($modifiers)
}

$results = [System.Collections.Generic.List[object]]::new()

foreach ( $assemblyFile in $assemblyFiles )
{
	if ( $results.Count -ge $Limit ) { break }

	$module = [Mono.Cecil.ModuleDefinition]::ReadModule( $assemblyFile.FullName )
	try
	{
		foreach ( $type in Get-AllTypes -Types $module.Types )
		{
			if ( $results.Count -ge $Limit ) { break }
			if ( -not (Test-AccessibleType -Type $type) ) { continue }

			$typeName = $type.FullName.Replace( "/", "." )
			if ( $Kind -in @("All", "Type") -and (Test-Match -Candidates @($type.Name, $typeName)) )
			{
				$results.Add( [pscustomobject]@{
					Kind = "Type"
					Assembly = $assemblyFile.Name
					QualifiedName = $typeName
					Accessibility = Get-TypeAccessibility -Type $type
					BaseType = $type.BaseType.FullName
					Interfaces = @($type.Interfaces | ForEach-Object { $_.InterfaceType.FullName })
					GenericParameters = @(Get-GenericParameterText -GenericParameters $type.GenericParameters)
					Attributes = @(Get-AttributeNames -Attributes $type.CustomAttributes)
					MetadataToken = $type.MetadataToken.ToInt32()
				} )
			}

			foreach ( $method in $type.Methods )
			{
				if ( $results.Count -ge $Limit ) { break }
				$memberKind = $(if ( $method.IsConstructor ) { "Constructor" } else { "Method" })
				if ( $Kind -notin @("All", $memberKind) ) { continue }
				if ( -not (Test-AccessibleMethod -Method $method) ) { continue }

				$qualifiedName = "$typeName.$($method.Name)"
				if ( -not (Test-Match -Candidates @($method.Name, $qualifiedName, $method.FullName)) ) { continue }

				$results.Add( [pscustomobject]@{
					Kind = $memberKind
					Assembly = $assemblyFile.Name
					DeclaringType = $typeName
					QualifiedName = $qualifiedName
					Signature = $method.FullName
					Accessibility = Get-MethodAccessibility -Method $method
					Modifiers = @(Get-MethodModifiers -Method $method)
					ReturnType = $method.ReturnType.FullName
					Parameters = @(Get-ParameterInfo -Parameters $method.Parameters)
					GenericParameters = @(Get-GenericParameterText -GenericParameters $method.GenericParameters)
					Attributes = @(Get-AttributeNames -Attributes $method.CustomAttributes)
					MetadataToken = $method.MetadataToken.ToInt32()
				} )
			}

			foreach ( $property in $type.Properties )
			{
				if ( $results.Count -ge $Limit ) { break }
				if ( $Kind -notin @("All", "Property") ) { continue }
				$accessors = @($property.GetMethod, $property.SetMethod) | Where-Object { $null -ne $_ }
				if ( -not $IncludeNonPublic -and -not ($accessors | Where-Object { Test-AccessibleMethod -Method $_ }) ) { continue }

				$qualifiedName = "$typeName.$($property.Name)"
				if ( -not (Test-Match -Candidates @($property.Name, $qualifiedName, $property.FullName)) ) { continue }

				$results.Add( [pscustomobject]@{
					Kind = "Property"
					Assembly = $assemblyFile.Name
					DeclaringType = $typeName
					QualifiedName = $qualifiedName
					Signature = $property.FullName
					PropertyType = $property.PropertyType.FullName
					Parameters = @(Get-ParameterInfo -Parameters $property.Parameters)
					Getter = $(if ( $property.GetMethod ) { [pscustomobject]@{ Accessibility = Get-MethodAccessibility -Method $property.GetMethod; Signature = $property.GetMethod.FullName } } else { $null })
					Setter = $(if ( $property.SetMethod ) { [pscustomobject]@{ Accessibility = Get-MethodAccessibility -Method $property.SetMethod; Signature = $property.SetMethod.FullName } } else { $null })
					Attributes = @(Get-AttributeNames -Attributes $property.CustomAttributes)
					MetadataToken = $property.MetadataToken.ToInt32()
				} )
			}

			foreach ( $field in $type.Fields )
			{
				if ( $results.Count -ge $Limit ) { break }
				if ( $Kind -notin @("All", "Field") ) { continue }
				if ( -not (Test-AccessibleField -Field $field) ) { continue }

				$qualifiedName = "$typeName.$($field.Name)"
				if ( -not (Test-Match -Candidates @($field.Name, $qualifiedName, $field.FullName)) ) { continue }

				$results.Add( [pscustomobject]@{
					Kind = "Field"
					Assembly = $assemblyFile.Name
					DeclaringType = $typeName
					QualifiedName = $qualifiedName
					Signature = $field.FullName
					FieldType = $field.FieldType.FullName
					Accessibility = Get-FieldAccessibility -Field $field
					IsStatic = $field.IsStatic
					IsReadOnly = $field.IsInitOnly
					IsConstant = $field.HasConstant
					Constant = $(if ( $field.HasConstant ) { $field.Constant } else { $null })
					Attributes = @(Get-AttributeNames -Attributes $field.CustomAttributes)
					MetadataToken = $field.MetadataToken.ToInt32()
				} )
			}

			foreach ( $event in $type.Events )
			{
				if ( $results.Count -ge $Limit ) { break }
				if ( $Kind -notin @("All", "Event") ) { continue }
				$accessors = @($event.AddMethod, $event.RemoveMethod) | Where-Object { $null -ne $_ }
				if ( -not $IncludeNonPublic -and -not ($accessors | Where-Object { Test-AccessibleMethod -Method $_ }) ) { continue }

				$qualifiedName = "$typeName.$($event.Name)"
				if ( -not (Test-Match -Candidates @($event.Name, $qualifiedName, $event.FullName)) ) { continue }

				$results.Add( [pscustomobject]@{
					Kind = "Event"
					Assembly = $assemblyFile.Name
					DeclaringType = $typeName
					QualifiedName = $qualifiedName
					Signature = $event.FullName
					EventType = $event.EventType.FullName
					AddAccessibility = $(if ( $event.AddMethod ) { Get-MethodAccessibility -Method $event.AddMethod } else { $null })
					RemoveAccessibility = $(if ( $event.RemoveMethod ) { Get-MethodAccessibility -Method $event.RemoveMethod } else { $null })
					Attributes = @(Get-AttributeNames -Attributes $event.CustomAttributes)
					MetadataToken = $event.MetadataToken.ToInt32()
				} )
			}
		}
	}
	finally
	{
		$module.Dispose()
	}
}

$results
