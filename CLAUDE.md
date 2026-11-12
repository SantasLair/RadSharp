# RadSharp Code Assistant Guide

**RadSharp** is a Delphi-style RAD framework for C# with visual form designer, NativeAOT compilation, and cross-platform support (Windows, macOS, Linux).

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
- **Designer** built in RadSharp itself (dogfooding principle)
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

The orchestration session (the main session) is free to use any skill and any model as the task needs. Choose what fits the work; don't default to a fixed model or skill for a task type.

**Guidelines, not rules:**
- **Cheapest adequate model first.** Use Haiku 5.5 (`claude-haiku-5-5`) for mechanical work, lookups, and simple summaries. Use Sonnet 5.5 (`claude-sonnet-5-5`) for multi-file reasoning, implementation with clear requirements, and bounded reviews. Use Opus 5.5 (`claude-opus-5-5`) for architecture trade-offs, subtle debugging, security review, and large or ambiguous refactors.
- **Go up a tier when a result looks thin or wrong**, and say so when you do.
- **Fable 5.2 (`claude-fable-5-2`) needs approval first.** It costs twice Opus 5.5 and runs for many minutes on hard tasks. Use it only for the hardest problems, after Opus 5.5 has fallen short, and only once the user has approved that specific use. Propose it with the reason and the expected cost, then wait for a yes.
- **Pick the skill that fits** (see Recommended Skills and Usage Patterns below). Skills may run in the main session or in a spawned agent, whichever is cheaper for the task.
- **Set the model explicitly** on every spawned agent so cost is a deliberate choice.

### Usage Patterns

- **Architecture questions** → `/anthropic-skills:deep-research` (model per depth; Sonnet is a typical default, Opus for hard trade-offs)
- **Visual explanations** → `/artifact-diagramming` (Haiku or Sonnet, depending on complexity)
- **Feature roadmap** → `/dataviz` for milestone tracking (Haiku or Sonnet)
- **Before major refactors** → `/code-review` (Sonnet; Opus for high-risk changes)
- **Form UI feedback** → `/artifact-design` (Haiku or Sonnet)
- **Complex planning** → `/EnterPlanMode` (Opus for deep architectural thinking)
