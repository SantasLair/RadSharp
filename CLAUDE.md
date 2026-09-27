# RADSharp Code Assistant Guide

**RADSharp** is a Delphi-style RAD framework for C# with visual form designer, NativeAOT compilation, and cross-platform support (Windows, macOS, Linux).

## Recommended Skills

These skills are configured for this project and will help you work effectively:

### Code Review & Quality
- **`/code-review`** — Review code changes for bugs, correctness, and quality improvements. Essential for Roslyn work and source generators.
- **`/security-review`** — Security audit for input handling, form rendering, and framework safety.

### Documentation & Communication
- **`/dataviz`** — Create roadmap visualizations, test coverage charts, and component hierarchy diagrams.
- **`/anthropic-skills:docs`** — Living documentation in Claude Docs for architecture, design decisions, and team knowledge.
- **`/anthropic-skills:deep-research`** — Research best practices, architectural patterns, and design decisions.

### Design & Artifacts
- **`/artifact-design`** — Design guidance for form UI and designer app interface.
- **`/artifact-diagramming`** — Explain rendering pipeline, layout algorithm, and component architecture.

### Testing & Running
- **`/run`** — Launch and test the designer, samples, and individual components.

## Architecture Highlights

- **Custom-drawn controls** via SkiaSharp (CPU raster, bundled fonts, pixel-perfect across OSes)
- **Layout engine** following Delphi's Align + Anchors model
- **Form files** as JSON (like `.dfm` files), embedded as resources
- **Source generators** emit metadata and code-behind (AOT-safe, no reflection)
- **Designer** built in RADSharp itself (dogfooding principle)
- **Roslyn-based** F9 live run with collectible `AssemblyLoadContext`
- **Golden-image tests** for cross-platform pixel-perfect validation
- **NativeAOT** shipping via `dotnet publish`

## Getting Started

```bash
dotnet test                                   # 153 tests (layout, golden images, input, editors, generators, designer)
dotnet run --project samples/TileEditor       # Run a sample
dotnet run --project src/RadSharp.Designer    # Launch the designer
```

## Key Files

- `src/RadSharp.Core/` — Controls, layout, rendering, text, metadata
- `src/RadSharp.Generators/` — Source generators for code-behind and metadata
- `src/RadSharp.Platform.Sdl/` — SDL3 host layer (windows, input, dialogs)
- `src/RadSharp.Design/` — Designer surface and document model
- `src/RadSharp.Design.Code/` — Roslyn integration, handler stubs, live run
- `docs/` — Reference, getting started, distribution, gap analysis
- `spec/` — Form format, layout conformance vectors (version-stable)

## Working with Claude Code

### Agent Strategy

**Main session:** Haiku 4.5 (fast, efficient parsing and planning)

**Spawned agents** (task-appropriate models):
- **Planning & Architecture** → Opus 5.5 (deep reasoning, complex design)
- **Code Review & Refactoring** → Sonnet 5 (thorough analysis, nuanced suggestions)
- **Research & Investigation** → Sonnet 5 (exploration, pattern finding)
- **Fast iteration tasks** → Haiku 4.5 (when speed matters, quality sufficient)

### Usage Patterns

- **Architecture questions** → `/anthropic-skills:deep-research` (spawns Sonnet for thorough research)
- **Visual explanations** → `/artifact-diagramming` for rendering/layout diagrams (Haiku)
- **Feature roadmap** → `/dataviz` for milestone tracking (Haiku)
- **Before major refactors** → `/code-review` (spawns Sonnet for careful analysis)
- **Form UI feedback** → `/artifact-design` (Haiku)
- **Complex planning** → `/EnterPlanMode` (spawns Opus for deep architectural thinking)
