using Xunit;

namespace RadSharp.VisualStudio.Tests
{
    public class FormFilesTests
    {
        [Theory]
        [InlineData(@"C:\app\BillForm.radform.json", @"C:\app\BillForm.cs")]
        [InlineData(@"C:\app\Forms\Main.RADFORM.JSON", @"C:\app\Forms\Main.cs")]
        [InlineData(@"C:\app\settings.json", null)]
        [InlineData(@"C:\app\BillForm.cs", null)]
        [InlineData(null, null)]
        public void A_form_file_pairs_with_the_cs_file_of_the_same_name(string? form, string? code) =>
            Assert.Equal(code, FormFiles.CodeFileFor(form));

        [Theory]
        [InlineData(@"C:\app\BillForm.cs", @"C:\app\BillForm.radform.json")]
        [InlineData(@"C:\app\BillForm.Designer.cs", @"C:\app\BillForm.Designer.radform.json")]
        [InlineData(@"C:\app\BillForm.radform.json", null)]
        [InlineData(@"C:\app\readme.md", null)]
        [InlineData(null, null)]
        public void A_cs_file_pairs_with_the_form_file_of_the_same_name(string? code, string? form) =>
            Assert.Equal(form, FormFiles.FormFileFor(code));

        [Fact]
        public void Only_files_that_exist_are_paired()
        {
            bool Exists(string p) => p == @"C:\app\BillForm.cs" || p == @"C:\app\BillForm.radform.json";
            Assert.Equal(@"C:\app\BillForm.radform.json", FormFiles.ExistingFormFileFor(@"C:\app\BillForm.cs", Exists));
            Assert.Equal(@"C:\app\BillForm.cs", FormFiles.ExistingCodeFileFor(@"C:\app\BillForm.radform.json", Exists));
            Assert.Null(FormFiles.ExistingFormFileFor(@"C:\app\Program.cs", Exists));
            Assert.Null(FormFiles.ExistingCodeFileFor(@"C:\app\NoteForm.radform.json", Exists));
        }
    }
}
