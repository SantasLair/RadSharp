using System.Text;
using RadSharp.Design.Code;

namespace RadSharp.Tests;

public class SharedFileTests
{
    static string TempFile(string text = "hello")
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("radsharp-shared-").FullName, "f.txt");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_writer_succeeds_while_a_SharedFile_reader_holds_the_file()
    {
        var path = TempFile();
        using (var s = SharedFile.OpenRead(path))
        {
            File.WriteAllText(path, "changed");
            Assert.Equal("changed", new StreamReader(s).ReadToEnd());
        }
    }

    [Fact]
    public void A_FileShare_Read_holder_blocks_the_writer_which_is_what_the_helper_avoids()
    {
        if (!OperatingSystem.IsWindows()) return;   // locks are advisory elsewhere
        var path = TempFile();
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var e = Assert.Throws<IOException>(() => File.WriteAllText(path, "x"));
        Assert.True(SharedFile.IsSharingViolation(e));
    }

    [Fact]
    public void ReadAllText_honours_a_bom_and_matches_File_ReadAllText()
    {
        var path = TempFile();
        File.WriteAllBytes(path, [.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes("héllo")]);
        Assert.Equal("héllo", SharedFile.ReadAllText(path));
        Assert.Equal(File.ReadAllText(path), SharedFile.ReadAllText(path));
        File.WriteAllBytes(path, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("wide")).ToArray());
        Assert.Equal("wide", SharedFile.ReadAllText(path));
        Assert.Equal(File.ReadAllBytes(path), SharedFile.ReadAllBytes(path));
    }
}
