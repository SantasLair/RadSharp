# The AOT-safe stack

RadSharp targets NativeAOT: `dotnet publish` should produce a small, fast-starting native executable with no
reflection surprises at runtime. This page is the short version of what keeps that true, and how to catch a
regression before `publish` does.

## Why RadSharp is AOT-safe by construction

- **No reflection for form loading.** A `.radform.json` file is read and written by hand-rolled code
  (`FormJsonReader`/`FormJsonWriter` in `RadSharp.Core`), not `System.Text.Json`'s reflection-based
  serializer.
- **No reflection for metadata.** Every component's properties and events (`PropertyMeta`, `EventMeta`) are
  emitted at compile time by `RadSharp.Generators`, a Roslyn source generator — not discovered by walking
  `Type.GetProperties()` at runtime.
- **Compile-time handler binding.** A form referencing a handler that doesn't exist in its code-behind is a
  build error (`RAD1001`), not a runtime `MissingMethodException` from `Delegate.CreateDelegate` with a
  string name.
- **`RadSharp.Core`, `RadSharp.Design` and `RadSharp.Platform.Sdl` all set `<IsAotCompatible>true</IsAotCompatible>`**
  in their `.csproj`, so a consuming app's own AOT/trim analyzer run checks these assemblies too, not just
  application code.

## What to avoid in your own code

These defeat trimming or fail at publish/runtime under NativeAOT, and the analyzers (below) flag most of them
as build warnings if you turn them on:

- `Type.GetType("SomeNamespace.SomeClass")` or any other string-based type lookup.
- `System.Text.Json` in its default reflection mode (source-generated `JsonSerializerContext` is fine).
- `System.Reflection.Emit`, `Expression.Compile()`, or any other runtime code generation.
- A NuGet package that itself isn't trim/AOT-compatible — check its own `IsAotCompatible`/`IsTrimmable`
  metadata, or publish a throwaway sample against it before depending on it.

## Turning the analyzers on

Every project the designer creates (**File › New Application…**) and the `dotnet new radsharp-app` template
already set both flags:

```xml
<EnableAotAnalyzer>true</EnableAotAnalyzer>
<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
```

With these on, `dotnet build` reports `IL2026`/`IL3050`-style warnings for anything reflection- or
codegen-based right away, instead of waiting for a `dotnet publish` failure. If you started a project before
these were added, or wrote a `.csproj` by hand, add the two lines above.

## Verifying a publish

```bash
dotnet publish --use-current-runtime
```

Don't add `-p:PublishAot=true` on the command line — every RadSharp app project already sets `PublishAot` in
its `.csproj`; passing it again is redundant and, on some setups, triggers a spurious `NETSDK1207`. If
`vswhere.exe` isn't on `PATH` on Windows, the link step fails with `'vswhere.exe' is not recognized` even
though the C++ workload is installed — see `getting-started.md`.
