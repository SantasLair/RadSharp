using Xunit;

namespace RadSharp.VisualStudio.Tests
{
    public class OutputPathsTests
    {
        [Theory]
        [InlineData(@"C:\p\bin\Debug\net10.0\App.dll", true)]
        [InlineData(@"C:\p\bin\Debug\App.EXE", true)]
        [InlineData(@"bin\Debug\net10.0\App.dll", false)]
        [InlineData(@"C:\p\bin\Debug\net10.0\", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void TargetPath_must_be_a_full_assembly_path(string? path, bool expected) =>
            Assert.Equal(expected, OutputPaths.IsAssembly(path));

        [Fact]
        public void A_relative_output_folder_is_under_the_project() =>
            Assert.Equal(@"C:\src\App\bin\Debug\net10.0\App.dll", OutputPaths.Combine(@"C:\src\App", @"bin\Debug\net10.0\", "App.dll"));

        [Fact]
        public void A_full_output_folder_is_kept() =>
            Assert.Equal(@"D:\out\App.dll", OutputPaths.Combine(@"C:\src\App", @"D:\out\", "App.dll"));

        [Fact]
        public void An_exe_apphost_gives_way_to_the_managed_dll_next_to_it()
        {
            Assert.Equal(@"C:\p\bin\App.dll", OutputPaths.PreferManaged(@"C:\p\bin\App.exe", p => p == @"C:\p\bin\App.dll"));
            Assert.Equal(@"C:\p\bin\App.exe", OutputPaths.PreferManaged(@"C:\p\bin\App.exe", _ => false));   // a real managed exe
            Assert.Equal(@"C:\p\bin\App.dll", OutputPaths.PreferManaged(@"C:\p\bin\App.dll", _ => true));
            Assert.Equal(@"C:\p\bin\App.dll", OutputPaths.Combine(@"C:\p", @"bin\", "App.exe", p => p.EndsWith("App.dll")));
        }

        [Fact]
        public void A_failed_project_lookup_keeps_the_previous_path_and_only_no_project_clears_it()
        {
            Assert.Equal(@"C:\p\App.dll", OutputPaths.AfterLookup(@"C:\p\App.dll", null, lookupFailed: true));
            Assert.Null(OutputPaths.AfterLookup(@"C:\p\App.dll", null, lookupFailed: false));
            Assert.Equal(@"C:\q\App.dll", OutputPaths.AfterLookup(@"C:\p\App.dll", @"C:\q\App.dll", lookupFailed: false));
        }

        [Fact]
        public void Missing_parts_give_nothing()
        {
            Assert.Null(OutputPaths.Combine(@"C:\src\App", null, "App.dll"));
            Assert.Null(OutputPaths.Combine(@"C:\src\App", @"bin\", ""));
            Assert.Null(OutputPaths.Combine(null, @"bin\", "App.dll"));
            Assert.Null(OutputPaths.Combine(@"C:\src\App", @"bin\", "App.pdb"));
        }
    }
}
