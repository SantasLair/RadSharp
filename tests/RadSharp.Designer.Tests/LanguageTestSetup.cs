using System.Runtime.CompilerServices;
using RadSharp.Designer;

namespace RadSharp.Tests;

static class LanguageTestSetup
{
    /// <summary>
    /// The designer's tests run without the language service unless they ask for it (<see cref="LanguageIntegrationTests"/>):
    /// their hosts run posted work inline on pool threads, and their projects reference packages that would be restored.
    /// </summary>
#pragma warning disable CA2255   // a test assembly may set itself up
    [ModuleInitializer]
    internal static void Init() => LanguageHost.Enabled = false;
#pragma warning restore CA2255
}
