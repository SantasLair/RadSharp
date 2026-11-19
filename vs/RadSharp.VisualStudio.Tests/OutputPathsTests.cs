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
        public void Missing_parts_give_nothing()
        {
            Assert.Null(OutputPaths.Combine(@"C:\src\App", null, "App.dll"));
            Assert.Null(OutputPaths.Combine(@"C:\src\App", @"bin\", ""));
            Assert.Null(OutputPaths.Combine(null, @"bin\", "App.dll"));
            Assert.Null(OutputPaths.Combine(@"C:\src\App", @"bin\", "App.pdb"));
        }
    }
}
