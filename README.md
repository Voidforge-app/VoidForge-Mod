# Voidforge - Blueprint Extractor

A UMM mod for OwlCat's Rogue Trader that extracts game data (weapons, armor, careers, talents, companions, etc.) into JSON files, as well as the relevant icons and compaion portraits. Those JSON files are the data source for the [VoidForge](https://voidforge.app) build planner.

---

## For players

**You do not need this mod to use [VoidForge](https://voidforge.app)!** You can plan and share builds right now without installing anything.

This mod exists so that the game data powering VoidForge can be refreshed when Owlcat releases a patch. Unless you want to help keep the site's data current after a game update, you can stop reading here.

---

## Installing the mod

1. Install [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) and set it up for Rogue Trader if you haven't already.
2. Download the latest `BlueprintExtractor-x.x.x.zip` from the [Releases](https://github.com/Voidforge-app/VoidForge-Mod/releases) page.
3. Drag and drop the zip onto the Unity Mod Manager window (or use the **Mods** tab to install it manually).
4. Launch the game. Once in the main menu, open the UMM overlay with <kbd>Ctrl+F10</kbd>.
5. Find `BlueprintExtractor` in the mod list and click `Export All`.
6. The exported JSON files land in `Documents\VoidForge\<game-version>\` - share that folder with the VoidForge maintainer.

---

## Contributing to the `BlueprintExtractor`

The rest of this document is for developers who want to build the mod from source, fix a bug, or add support for new data after a game update.

### How it works

The mod runs inside the game's process via UMM. It patches `BlueprintsCache.Init` with Harmony and waits until the blueprint cache is fully loaded before allowing exports. Once ready, a button in the UMM panel (Ctrl+F10) then triggers the full export pipeline.

#### Export pipeline

```
BlueprintsCache.Init (game startup) -> HarmonyPostfix sets blueprintsReady = true

UMM panel "Export All" button
  └-> MainExporter.ExportAll()
        ├-> ItemReachabilityIndex.Build()   - finds player-obtainable items
        ├-> WeaponExporter
        ├-> ArmorExporter
        ├-> EquipmentExporter
        ├-> CareerExporter
        ├-> OriginExporter
        ├-> FeaturesExporter
        ├-> EncyclopediaExporter
        └-> CompanionExporter
```

Every exporter writes a single `<name>.json` file wrapped in a standard envelope:

```json
{
  "version": "1.6.0.472",
  "revision": "...",
  "exportedAt": "2025-01-01T00:00:00Z",
  "count": 123,
  "items": [...]
}
```

Output lands in `Documents\VoidForge\<game-version>\` by default, but the path is customizable inside the UMM panel.

#### Reachability filter

The game has thousands of blueprint objects - most are internal, unobtainable, or editor-only. Rather than exporting everything and filtering downstream, the extractor builds a reachability index first.

For **items**: `ItemReachabilityIndex` walks every vendor table and loot container in the blueprint cache and collects the GUIDs of all items reachable from them. Companion starting loadouts are also included. Each exported item gets a `reachable` flag based on this set.

For **features/talents**: only features reachable via career paths, chargen groups, and occupation selections are exported. Everything else is engine internals and is discarded.

### Prerequisites

- Warhammer 40,000: Rogue Trader (Steam or GOG)
- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) installed for Rogue Trader
- .NET SDK 8+ (for building the static extractor, pipeline, and uploader)
- .NET Framework 4.8.1 (used by the mod itself - included with Windows)

### Building

```powershell
dotnet build VoidForge.slnx
```

Run from the repository root. On first build, the project auto-detects your game install path from `%LocalAppDataLow%\Owlcat Games\Warhammer 40000 Rogue Trader\Player.log` and writes it to `GamePath.props`. Subsequent builds use the cached path.

The build also automatically deploys the compiled mod to your UMM folder.

### Project structure

```
BlueprintExtractor/               - The UMM mod (net481, runs inside the game)
  Main.cs                         - UMM entry point, Harmony patch, GUI
  ExportEnvelope.cs               - Generic JSON envelope wrapper
  Exporters/
    MainExporter.cs               - Orchestrator, calls all per-type exporters
    WeaponExporter.cs
    ArmorExporter.cs
    EquipmentExporter.cs
    CareerExporter.cs
    OriginExporter.cs
    FeaturesExporter.cs
    EncyclopediaExporter.cs
    CompanionExporter.cs
    IconExporter.cs               - Exports item icons and companion portraits as PNGs
  Extraction/
    FeatureExtractor.cs           - Core feature/blueprint field extraction
    RankEntryExtractor.cs         - Career rank tree traversal
    ItemReachabilityIndex.cs      - Builds the set of player-obtainable item GUIDs
    UiParamExtractor.cs           - Resolves {uip|...} formula placeholders in descriptions
    ItemFilter.cs                 - Filters out internal/unusable items
    UnitFilter.cs                 - Identifies named companion units
    BlueprintFieldExtractor.cs
    ReflectionHelpers.cs
    TextureExtractor.cs
  Infrastructure/
    BlueprintsCatalog.cs          - Typed enumeration over the game's blueprint cache
    ExportWriter.cs               - JSON serialization + output path resolution
    GameVersion.cs                - Reads game version/revision via reflection
    ModLogger.cs                  - Structured logging to a per-export log file
    ExplorationPathAttribute.cs
StaticExtractor/                   - Offline .jbp extractor (net8)
  Program.cs                      - CLI entry point, orchestrates all exporters
  Blueprints/                     - .jbp blueprint reading and graph traversal
  Extraction/                     - Feature, rank entry, reachability helpers
  Exporters/                      - Category exporters mirroring runtime output
  Localization/                   - Localized string resolution
  Infrastructure/                 - Export writer, baseline, manifest, merge

Pipeline/                          - Interactive CLI orchestrator
  Program.cs                      - Static -> runtime -> merge -> upload flow

Uploader/                          - Rclone upload tool (net8)
  Program.cs                      - Dev/prod bucket sync via rclone

```

Exports are written to `Documents\VoidForge` as their default source root.

### Releasing

`release.ps1` handles the full release flow: version bump, build, GitHub release creation.

```powershell
.\release.ps1 -Bump patch
.\release.ps1 -Bump minor
.\release.ps1 -Bump major
.\release.ps1 -Version 1.2.3 # Avoid this one unless you have a very good reason
.\release.ps1 -Bump patch -Message "Adds X and fixes Y"
```

Requirements: clean working tree, `gh` CLI installed and authenticated.

The script updates the version in `BlueprintExtractor.csproj`, `Info.json` (UMM metadata), and `Repository.json` (UMM auto-update manifest), then commits, tags, pushes, and publishes the GitHub release with an auto-generated changelog.

### Full export pipeline (new game version)

When a new game version drops, the `VoidForge` pipeline tool runs the whole process:

```powershell
dotnet run --project Pipeline
```

This will:
1. Auto-detect the game root and version from `Version.info`
2. Run static extraction from `.jbp` files
3. Prompt you to launch the game and run the runtime mod export
4. Merge runtime + static into `Documents\VoidForge\<version>\`
5. Prompt to upload to R2

For non-interactive use:
```powershell
dotnet run --project Pipeline -- "Z:\Steam\...\WhRtModificationTemplate" --version 1.6.1.514 --no-upload
```

#### Standalone static extraction

If you only need the static baseline (no merge):

```powershell
dotnet run --project StaticExtractor -- "Z:\Steam\...\WhRtModificationTemplate" --output "Documents\VoidForge"
```

Options:
- `--version <ver>` - override game version (auto-detected from `Version.info` by default)
- `--types Type1,Type2` - only extract specific blueprint types (fast test mode)
- `--no-reachability` - skip the reference graph build
- `--merge-runtime <path>` - merge a runtime export into the static output

### Adding a new exporter

**Runtime (in-game mod):**
1. Create `BlueprintExtractor/Exporters/YourExporter.cs` with a single static `Export(...)` method matching the signature of existing exporters.
2. Add a call to it in `MainExporter.ExportAll()`.

**Static (.jbp extraction):**
1. Create `StaticExtractor/Exporters/StaticYourExporter.cs` matching the pattern of existing static exporters.
2. Add a call to it in `StaticMainExporter.ExportAll()`.

Then run `dotnet build VoidForge.slnx` and test with a fresh export.

### Code conventions

- **Naming** - descriptive variable names throughout; no abbreviations like `bp`, `e`, `s`. Lambda parameters follow the same rule.
- **File-scoped namespaces** - every file uses `namespace Foo;` not a block.
- **Multi-line doc comments** use `/** */` style; single-line inline comments inside methods use `//`.
- **One responsibility per file** - exporters delegate data extraction to `Extraction/`, shared plumbing lives in `Infrastructure/`.
- **No dead code** - remove unused stubs rather than leaving them.

---

## License

MIT - see [LICENSE](./LICENSE).
