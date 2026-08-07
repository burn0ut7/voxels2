# s&box source strategy

Use a hybrid strategy: inspect the installed engine in place, keep small searchable caches of public text sources, and use changing hosted indexes online.

## Evidence ladder

### Project workspace

Read applicable instructions, `.sbproj`, generated `.csproj` files, project code, libraries, scenes, and settings first. They establish the requested product behavior and local conventions, not the validity of an uncertain engine API.

### Installed engine

Get the engine root and version from live `editor_status`. Treat these as exact for the editor the user will run:

- `bin/managed/Sandbox.*.dll`: exact shipped declarations. Inspect signatures, accessibility, accessors, inheritance, constraints, and attributes with `scripts/inspect-installed-api.ps1`.
- generated project references and the compiler: proof that the project surface can consume a declaration.
- `bin/managed/Sandbox.*.xml`: shipped API documentation and member IDs. Search narrative documentation with `scripts/search-installed-api.ps1`; XML does not encode complete C# declarations.
- `addons/base/code`: version-matched public Base Library patterns.
- `samples` and `templates`: minimal version-matched examples.
- `.vs/output` and generated project files: actual references and compile output.
- `logs`: diagnostic history; live `read_console` is preferable for a bounded fresh view.

Inspect these files in place. Copying them creates stale duplicates and can mix licenses.

### Official documentation

Use [sbox.game/dev/doc](https://sbox.game/dev/doc) for concepts, supported workflows, and tutorials. Its Markdown source is [Facepunch/sbox-docs](https://github.com/Facepunch/sbox-docs). A normal checkout includes large videos and images; use a Markdown-only sparse external cache for fast local `rg` searches. Refresh the cache before consequential research and cite the commit.

Documentation explains intended use; it does not prove that a member exists in the installed engine.

### Official API reference and schema

Use [sbox.game/api](https://sbox.game/api) for public types, signatures, inheritance, and documentation. The [API schema page](https://sbox.game/api/schema) exposes the machine-readable `api.json` that powers the site. The schema is regenerated from the latest staging build, so record its release URL/date and confirm emitted identifiers against installed metadata or compilation.

Cache `api.json` outside projects for machine search when doing repeated API work. Refresh it from the schema page rather than bundling a snapshot in this skill. Search it with `scripts/search-api-schema.ps1`.

### Public engine source

Use [Facepunch/sbox-public](https://github.com/Facepunch/sbox-public) for implementation details, lifecycle behavior, and deeper examples. Keep a shallow external clone and record the commit for each claim. Master can differ from the installed Steam build; a public implementation is not proof that an internal member is available to game code.

Prefer installed Base Library/sample source when it answers the same question because it matches the open editor.

Search the workspace, installed examples, cached docs, and cached public source with `scripts/search-sbox-source.ps1`. Preserve its surface and revision fields when a result informs a decision.

### Package examples and game data

Use [official Code Search](https://sbox.game/codesearch) for examples across open-source games and libraries. Keep this online: its value is the changing server-side index, and copying every package creates a large stale corpus. Verify copied patterns independently; popularity is not API authority.

Use live MCP asset, component, package, and scene tools for mounted resources and authored game data. Search and inspect only the relevant assets/packages instead of mirroring compiled game data. Record package identity/version and asset path.

### MCP boundary

The editor `sbox` MCP proves the open editor's compiler, mounted assets/packages, scenes, play state, screenshots, and console output. Its hotloaded registry is not automatically a Wiki or general API corpus.

When a distinct s&box documentation, API, source, or game-data MCP is connected, read its status and revision contract before using it. Treat it as an access layer for the corpus it declares; keep installed declarations and live observations authoritative for compatibility and behavior. A tool whose contract identifies Reforger or another game is not s&box evidence.

## Routing by question

| Question | Route |
| --- | --- |
| Does this type/member exist here? | Installed DLL inspection → generated references → minimal compile spike → installed XML/online API for explanation |
| What is the intended workflow? | Official docs → version-matched installed example → live editor observation |
| How is it implemented? | Installed source → public source at recorded commit → behavior probe |
| How do real projects use it? | Installed samples/Base Library → Code Search packages → verify API ledger |
| Which component/property is live? | MCP `get_component_type`/scene inspection → installed API metadata |
| Which asset/package should be used? | MCP asset/package search and inspection → package metadata → live preview/test |
| Did the change work? | Settled live compiler → play observation/state/screenshot → fresh console logs |

## Cache policy

Keep caches under a user cache directory such as `%LOCALAPPDATA%\Codex\sbox-reference`, separate from game projects and the skill package. The cache manifest records repository revisions, schema provenance, refresh state, and the installed engine identity used for comparison.

Before using cached evidence, report:

- installed engine version;
- repository commit and fetch time;
- API schema release URL/date;
- whether the network was unavailable during refresh.

Read these values from `manifest.json`. A missing manifest makes cache provenance unknown even when the cached files exist.

When revisions differ, use public sources to generate a candidate and let installed metadata plus compilation decide compatibility.
