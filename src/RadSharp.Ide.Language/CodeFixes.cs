using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace RadSharp.Ide.Language;

public sealed partial class LanguageService
{
    /// <summary>
    /// The code fixes the service offers: Roslyn's own providers (they're internal to the Features assembly,
    /// so they are created by name; one that's missing in a future Roslyn is skipped).
    /// </summary>
    static readonly Lazy<IReadOnlyList<(string Category, CodeFixProvider Provider)>> Providers = new(() =>
    {
        var assembly = Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features");
        var list = new List<(string, CodeFixProvider)>();
        foreach (var (category, type) in new[]
        {
            ("AddUsing", "Microsoft.CodeAnalysis.CSharp.AddImport.CSharpAddImportCodeFixProvider"),
            ("ImplementInterface", "Microsoft.CodeAnalysis.CSharp.ImplementInterface.CSharpImplementInterfaceCodeFixProvider"),
            ("GenerateMethod", "Microsoft.CodeAnalysis.CSharp.CodeFixes.GenerateMethod.GenerateMethodCodeFixProvider"),
        })
        {
            try
            {
                if (assembly.GetType(type) is { } t && Activator.CreateInstance(t) is CodeFixProvider provider) list.Add((category, provider));
            }
            catch (Exception e) when (e is MissingMethodException or TargetInvocationException or TypeLoadException) { }
        }
        return list;
    });

    /// <summary>
    /// Fixes for the diagnostics overlapping <paramref name="range"/> (the caret's line, or a squiggle):
    /// add a <c>using</c>, implement an interface, generate a method. Each fix's edits are computed up front.
    /// </summary>
    public Task<Versioned<IReadOnlyList<CodeFixInfo>>> GetCodeFixesAsync(string path, TextRange range, CancellationToken cancel = default) =>
        Run(async () =>
        {
            var (snapshot, doc) = await SourceAsync(path, cancel).ConfigureAwait(false);
            var model = await doc.GetSemanticModelAsync(cancel).ConfigureAwait(false);
            var span = new TextSpan(range.Start, range.Length);
            var diagnostics = model!.GetDiagnostics(cancellationToken: cancel)
                .Where(d => d.Location.SourceSpan.IntersectsWith(span) || d.Location.SourceSpan.Contains(span.Start) || span.Contains(d.Location.SourceSpan.Start))
                .ToList();

            var fixes = new List<CodeFixInfo>();
            foreach (var diagnostic in diagnostics)
            {
                foreach (var (category, provider) in Providers.Value)
                {
                    if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id)) continue;
                    var actions = new List<CodeAction>();
                    var context = new CodeFixContext(doc, diagnostic, (action, _) => actions.Add(action), cancel);
                    try { await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false); }
                    catch (Exception e) when (e is not OperationCanceledException) { continue; }   // a provider failing shouldn't fail the list

                    foreach (var action in Flatten(actions))
                    {
                        var operations = await action.GetOperationsAsync(cancel).ConfigureAwait(false);
                        var apply = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
                        if (apply == null) continue;
                        var edits = await DiffAsync(snapshot.Solution, apply.ChangedSolution, cancel).ConfigureAwait(false);
                        if (edits.Count > 0 && !fixes.Any(f => f.Title == action.Title && f.DiagnosticId == diagnostic.Id))
                            fixes.Add(new CodeFixInfo(action.Title, diagnostic.Id, category, edits));
                    }
                }
            }
            return new Versioned<IReadOnlyList<CodeFixInfo>>(snapshot.Version, fixes);
        }, cancel);

    static IEnumerable<CodeAction> Flatten(IEnumerable<CodeAction> actions)
    {
        foreach (var action in actions)
        {
            var nested = action.NestedActions;
            if (nested.IsDefaultOrEmpty) yield return action;
            else foreach (var inner in Flatten(nested)) yield return inner;
        }
    }
}
