using RadSharp.Tests.Forms;

namespace RadSharp.Tests;

/// <summary>A form class generated at build time from Forms/OrderForm.radform.json, used for real.</summary>
public class GeneratedFormTests
{
    [Fact]
    public void Generated_form_loads_its_embedded_file_and_binds_handlers()
    {
        var form = new OrderForm();
        Assert.Equal("Order", form.Caption);
        Assert.Equal((300, 200), (form.DesignWidth, form.DesignHeight));
        Assert.Equal("Gift wrap", form.chkGift.Caption);
        Assert.Same(form.btnOrder, form.FindControl("btnOrder"));

        form.Simulate.Click(form.chkGift);
        form.Simulate.Click(form.btnOrder);
        form.Simulate.Key(Key.Enter);  // default button
        form.Resize(400, 200);

        Assert.Equal(["gift True", "order", "order", "resize 400x200"], form.Log);
        Assert.Equal(308, form.btnOrder.Left);  // anchored right: 208 + 100
    }

    [Fact]
    public void Generated_form_saves_back_to_its_source_file()
    {
        var form = new OrderForm();
        var source = File.ReadAllText(Path.Combine(ProjectDir(), "Forms", "OrderForm.radform.json")).ReplaceLineEndings("\n");
        Assert.Equal(source, FormWriter.Save(form));
    }

    static string ProjectDir([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
}
