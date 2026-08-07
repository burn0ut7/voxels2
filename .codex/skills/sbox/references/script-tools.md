# Bundled s&box script tools

Use this reference to choose and run the skill's PowerShell helpers without reading their implementation.

## Contents

- [Common contract](#common-contract)
- [Choose a script](#choose-a-script)
- [Inspect installed declarations](#inspect-installed-declarations)
- [Search installed API documentation](#search-installed-api-documentation)
- [Search source surfaces](#search-source-surfaces)
- [Search the official API schema cache](#search-the-official-api-schema-cache)
- [Refresh reference caches](#refresh-reference-caches)
- [Maintain the script package](#maintain-the-script-package)

## Common contract

Run commands from the directory containing `SKILL.md`. All search patterns are case-insensitive regular expressions unless a parameter says otherwise. Use single-quoted PowerShell strings so backslashes and `$` remain literal.

Obtain the active engine directory and version from live `editor_status`. Pass `Paths.Engine` as `-EnginePath` whenever it differs from the scripts' default Steam location. Cached sources live at `%LOCALAPPDATA%\Codex\sbox-reference` by default and can be relocated consistently with `-CacheRoot` or `-SchemaPath`.

The search helpers emit PowerShell objects to the success stream. Capture them with `$results = @(...)`, inspect interactively with `Format-List`, or serialize with `ConvertTo-Json -Depth 8`. Zero emitted objects means no match among the available searched surfaces. Terminating errors describe invalid parameters, missing required files, or unavailable dependencies.

## Choose a script

| Question | Script | Authority represented |
| --- | --- | --- |
| What exact type or member is shipped? | `inspect-installed-api.ps1` | Installed `Sandbox.*.dll` metadata |
| What does the installed XML say about it? | `search-installed-api.ps1` | Installed `Sandbox.*.xml` documentation |
| Where is a pattern used in projects, examples, docs, or public engine source? | `search-sbox-source.ps1` | Selected source surfaces with revision labels |
| What does the cached online API release expose? | `search-api-schema.ps1` | Cached official `api.json` release |
| How are the docs, public source, and schema cache created or updated? | `refresh-reference-cache.ps1` | Official repositories and schema endpoint |
| Do the bundled helpers still work? | `test-sbox-skill.ps1` | Disposable script regression fixtures plus installed API checks |

## Inspect installed declarations

```powershell
./scripts/inspect-installed-api.ps1 -Pattern '^Sandbox\.Component\.Enabled$' -Kind Property -EnginePath '<editor_status Paths.Engine>'
```

Parameters:

- `-Pattern` is required and matches simple names, qualified names, and metadata signatures.
- `-EnginePath` points to the s&box installation containing `bin/managed`.
- `-AssemblyPattern` selects DLL filenames; default `Sandbox.*.dll`.
- `-Kind` accepts `All`, `Type`, `Constructor`, `Method`, `Property`, `Field`, or `Event`.
- `-Limit` caps results; default 50 and minimum 1.
- `-IncludeNonPublic` includes private and internal declarations in addition to the accessible surface.

Results identify kind, assembly, qualified name, accessibility, signature/type information, parameters, generic constraints, attributes, and metadata token. Types add base type and interfaces; properties add getter/setter details; fields add static/read-only/constant details; events add accessor visibility.

The script requires the installed `Mono.Cecil.dll`. A missing engine directory, Cecil assembly, or matching assembly pattern is a terminating error.

## Search installed API documentation

```powershell
./scripts/search-installed-api.ps1 -Pattern 'T:Sandbox\.Component' -Context 2 -EnginePath '<editor_status Paths.Engine>'
```

`-Pattern` is required. `-EnginePath` selects the installation, and `-Context` selects the number of XML lines before and after each match; default 4 and minimum 0. Results contain `Path`, `LineNumber`, `Line`, and `Context`. The script terminates when the managed directory or `Sandbox.*.xml` files are absent.

Use XML results for summaries and documentation IDs. Use `inspect-installed-api.ps1` for complete declarations because XML does not encode the full C# surface.

## Search source surfaces

```powershell
./scripts/search-sbox-source.ps1 -Pattern 'OnUpdate' -Surface Workspace,Installed -ProjectPath '<project root>' -EnginePath '<editor_status Paths.Engine>'
./scripts/search-sbox-source.ps1 -Pattern 'Component.Enabled' -Surface Docs,Public -FixedString
```

Parameters:

- `-Pattern` is required; add `-FixedString` for literal matching.
- `-ProjectPath` selects the workspace; default current directory.
- `-EnginePath` selects installed Base Library, tools, samples, and templates.
- `-CacheRoot` selects cached `sbox-docs` and `sbox-public` repositories.
- `-Surface` accepts any combination of `Workspace`, `Installed`, `Docs`, and `Public`; default all four.
- `-Glob` selects file patterns; default `*.cs`, `*.razor`, `*.scss`, and `*.md`.
- `-Limit` caps the combined results; default 100 and minimum 1.

Results contain `Surface`, `Revision`, `Path`, `LineNumber`, and `Line`. Unavailable roots emit warnings and are skipped. An empty result accompanied by no availability warning means the available surfaces contained no textual match. The script requires `rg` and terminates on an unexpected search failure.

## Search the official API schema cache

```powershell
./scripts/search-api-schema.ps1 -Pattern '^Sandbox\.Component' -Limit 25
```

`-Pattern` is required. `-SchemaPath` selects `api.json`; default `%LOCALAPPDATA%\Codex\sbox-reference\api.json`. `-Limit` caps results at 50 by default and has a minimum of 1.

Results contain kind, assembly, declaring type, member, documentation ID, return/property/field type, parameters, public status, attributes, source location, and summary. The script terminates when the schema file is absent; use `refresh-reference-cache.ps1` to create it. This schema describes its recorded online release rather than necessarily matching the installed editor.

## Refresh reference caches

```powershell
./scripts/refresh-reference-cache.ps1 -EnginePath '<editor_status Paths.Engine>' -EngineVersion '<editor_status EngineVersion>'
./scripts/refresh-reference-cache.ps1 -EnginePath '<editor_status Paths.Engine>' -EngineVersion '<editor_status EngineVersion>' -Offline
```

The online form creates or fast-forwards shallow caches of `Facepunch/sbox-docs` and `Facepunch/sbox-public`, resolves the latest immutable official API schema URL, downloads `api.json`, and writes `manifest.json`. Existing repository caches with local changes cause a terminating error. Network failures preserve usable existing cache data and appear in the returned manifest; a required cache with no usable existing data causes termination.

Parameters:

- `-CacheRoot` selects the cache directory.
- `-EnginePath` and `-EngineVersion` record the installed editor alongside cached revisions.
- `-Offline` reads and fingerprints existing caches without network access; every required repository cache must already exist.
- `-ApiSchemaUrl` replays an immutable `https://cdn.sbox.game/releases/YYYY-MM-DD-HH-MM-SS.zip.json` release and cannot be combined with `-Offline`.

The command emits the manifest JSON and writes the same data to `<CacheRoot>/manifest.json`. Read `Repositories[].Status`, `Schema.Resolution`, `Schema.Error`, and `NetworkErrors` before treating a refresh as current. The manifest also contains repository commits/dates plus installed assembly version and SHA-256 data.

## Maintain the script package

```powershell
./scripts/test-sbox-skill.ps1 -EnginePath '<editor_status Paths.Engine>' -ProjectPath '<project root>'
```

`-EnginePath` and `-ProjectPath` override the defaults. `-KeepTemporary` preserves the disposable fixture directory and reports its path for diagnosis. The suite syntax-checks every `.ps1` file and exercises installed metadata/XML inspection, every source-search surface, schema search, and offline cache refresh. It emits a summary with `Passed`, pass/fail counts, temporary path, and per-case timings/errors; any failed case also terminates the command.

The suite uses temporary repositories and a temporary cache. Its cache-refresh coverage is offline and does not validate current network access or the live official endpoints.
