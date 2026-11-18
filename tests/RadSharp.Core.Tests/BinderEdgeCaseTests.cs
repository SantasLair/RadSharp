using System.ComponentModel;

namespace RadSharp.Tests;

/// <summary>Regression tests for <see cref="Binder{T}"/> edge cases: radio groups across parents, setters with side effects, re-entrant handlers.</summary>
public class BinderEdgeCaseTests
{
    public enum Kind { Person, Company }

    public sealed class Model : INotifyPropertyChanged
    {
        string _country = "", _city = "", _name = "";
        public string Name { get => _name; set { _name = value; Changed(nameof(Name)); } }
        public string Country
        {
            get => _country;
            set { _country = value; Changed(nameof(Country)); City = ""; }   // a dependent property: changing the country clears the city
        }
        public string City { get => _city; set { _city = value; Changed(nameof(City)); } }
        public Kind Kind { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    [Fact]
    public void Radio_buttons_in_different_parents_act_as_one_group_when_the_user_clicks()
    {
        var f = new Form();
        f.SetBounds(0, 0, 400, 200);
        var left = new Panel();
        var right = new Panel();
        left.SetBounds(0, 0, 200, 100);
        right.SetBounds(200, 0, 200, 100);
        f.Add(left);
        f.Add(right);
        var person = new RadioButton { Caption = "Person" };
        var company = new RadioButton { Caption = "Company" };
        person.SetBounds(10, 10, 150, 24);
        company.SetBounds(10, 10, 150, 24);
        left.Add(person);
        right.Add(company);
        f.PerformLayout();
        var m = new Model { Kind = Kind.Person };
        var b = new Binder<Model>(m);
        b.BindRadios(x => x.Kind, (x, v) => x.Kind = v, (person, Kind.Person), (company, Kind.Company));
        Assert.True(person.Checked && !company.Checked);

        f.Simulate.Click(company);
        Assert.True(company.Checked);
        Assert.False(person.Checked);                       // the binder keeps the group to one checked button
        Assert.True(b.IsDirty);
        Assert.True(b.Commit());
        Assert.Equal(Kind.Company, m.Kind);
    }

    [Fact]
    public void A_setter_that_changes_another_bound_property_updates_that_control_after_commit()
    {
        var (f, country, city) = TwoEdits();
        var m = new Model { Country = "UK" };
        m.City = "London";
        var b = new Binder<Model>(m);
        b.Bind(country, x => x.Country, (x, v) => x.Country = v);
        b.Bind(city, x => x.City, (x, v) => x.City = v);
        f.Simulate.Click(country);
        country.SelectAll();
        f.Simulate.Type("FR");
        Assert.True(b.Commit());
        Assert.Equal(("FR", ""), (m.Country, m.City));
        Assert.Equal("", city.Text);                        // the control shows the model, not the stale "London"
        Assert.False(b.IsDirty);
    }

    [Fact]
    public void Switching_Current_from_a_handler_during_commit_never_writes_the_edit_into_the_new_object()
    {
        var (f, name, city) = TwoEdits();
        var a = new Model { Name = "A" };
        var other = new Model { Name = "B" };
        var b = new Binder<Model>(a);
        b.Bind(name, x => x.Name, (x, v) => x.Name = v);
        b.Bind(city, x => x.City, (x, v) => x.City = v);
        // e.g. a list that re-sorts when a name changes and so selects another row, whose Change sets Current
        a.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Model.Name)) b.Current = other; };
        f.Simulate.Click(name);
        name.SelectAll();
        f.Simulate.Type("A2");
        f.Simulate.Click(city);
        f.Simulate.Type("Rome");
        Assert.True(b.Commit());
        Assert.Equal(("A2", "Rome"), (a.Name, a.City));     // both edits went to the object they were made on
        Assert.Equal(("B", ""), (other.Name, other.City));
        Assert.Same(other, b.Current);
        Assert.Equal("B", name.Text);
        Assert.False(b.IsDirty);
    }

    [Fact]
    public void Setting_Current_from_DirtyChanged_while_typing_switches_cleanly()
    {
        var (f, name, city) = TwoEdits();
        var a = new Model { Name = "Alexander" };
        var other = new Model { Name = "B" };
        var b = new Binder<Model>(a);
        b.Bind(name, x => x.Name, (x, v) => x.Name = v);
        b.DirtyChanged += (_, _) => { if (b.IsDirty) b.Current = other; };
        f.Simulate.Click(name);
        f.Simulate.Key(Key.End);
        f.Simulate.Type("!");
        f.Simulate.Type("?");
        Assert.Same(other, b.Current);
    }

    static (Form, Edit, Edit) TwoEdits()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 120);
        var e1 = new Edit();
        var e2 = new Edit();
        e1.SetBounds(10, 10, 200, 26);
        e2.SetBounds(10, 50, 200, 26);
        f.Add(e1);
        f.Add(e2);
        f.PerformLayout();
        return (f, e1, e2);
    }
}
