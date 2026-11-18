using System.ComponentModel;
using System.Globalization;

namespace RadSharp.Tests;

/// <summary><see cref="Binder{T}"/> (docs/binding-and-actions.md, section 1), driven through simulated input.</summary>
public class BinderTests
{
    public enum Kind { Person, Company }

    public sealed class Contact : INotifyPropertyChanged
    {
        string _name = "", _city = "";
        public string Name { get => _name; set { _name = value; Changed(nameof(Name)); } }
        public string City { get => _city; set { _city = value; Changed(nameof(City)); } }
        public string Notes { get; set; } = "";
        public bool Vip { get; set; }
        public Kind Kind { get; set; }
        public int Seats { get; set; }
        public int Rating { get; set; }
        public int Tier { get; set; }
        public int Terms { get; set; } = 30;
        public decimal Rate { get; set; }
        public DateOnly Since { get; set; } = new(2026, 1, 1);
        public DateOnly? LastOrder { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    sealed class Rig
    {
        public readonly Form Form = new();
        public readonly Edit Name = new(), City = new(), Rate = new();
        public readonly Memo Notes = new();
        public readonly CheckBox Vip = new() { Caption = "VIP" };
        public readonly RadioButton Person = new() { Caption = "Person" }, Company = new() { Caption = "Company" };
        public readonly SpinEdit Seats = new() { MinValue = 1, MaxValue = 50 };
        public readonly TrackBar Rating = new() { Max = 5 };
        public readonly ComboBox Tier = new() { Items = ["Bronze", "Silver", "Gold"] };
        public readonly ComboBox Terms = new() { Items = ["Net 14", "Net 30", "Net 60"] };
        public readonly DateTimePicker Since = new() { Format = "yyyy-MM-dd" }, LastOrder = new() { Format = "yyyy-MM-dd" };
        public readonly Binder<Contact> Binder = new();
        public int DirtyChanges;

        public Rig()
        {
            Shortcut.CommandModifier = KeyModifiers.Ctrl;
            Form.SetBounds(0, 0, 600, 600);
            int y = 10;
            foreach (var c in new Control[] { Name, City, Rate, Notes, Vip, Person, Company, Seats, Rating, Tier, Terms, Since, LastOrder })
            {
                c.SetBounds(10, y, 200, c is Memo ? 60 : 26);
                Form.Add(c);
                y += c is Memo ? 66 : 32;
            }
            Form.PerformLayout();
            Binder.Bind(Name, c => c.Name, (c, v) => c.Name = v).Validate(v => v.Length > 0 ? null : "Enter a name.");
            Binder.Bind(City, c => c.City, (c, v) => c.City = v);
            Binder.Bind(Rate, c => c.Rate, (c, v) => c.Rate = v, BindFormat.Decimal("0.00", CultureInfo.GetCultureInfo("de-DE")));
            Binder.Bind(Notes, c => c.Notes, (c, v) => c.Notes = v);
            Binder.Bind(Vip, c => c.Vip, (c, v) => c.Vip = v);
            Binder.BindRadios(c => c.Kind, (c, v) => c.Kind = v, (Person, Kind.Person), (Company, Kind.Company));
            Binder.Bind(Seats, c => c.Seats, (c, v) => c.Seats = v);
            Binder.Bind(Rating, c => c.Rating, (c, v) => c.Rating = v);
            Binder.BindIndex(Tier, c => c.Tier, (c, v) => c.Tier = v);
            Binder.Bind(Terms, c => c.Terms, (c, v) => c.Terms = v, [14, 30, 60]);
            Binder.Bind(Since, c => c.Since, (c, v) => c.Since = v);
            Binder.Bind(LastOrder, c => c.LastOrder, (c, v) => c.LastOrder = v);
            Binder.DirtyChanged += (_, _) => DirtyChanges++;
        }

        public void TypeInto(Control c, string text)
        {
            Form.Simulate.Click(c);
            if (c is EditBase e) e.SelectAll();
            Form.Simulate.Type(text);
        }
    }

    static Contact Sample() => new()
    {
        Name = "Ada", City = "London", Notes = "first", Vip = true, Kind = Kind.Company, Seats = 3, Rating = 4, Tier = 1,
        Terms = 60, Rate = 12.5m, Since = new(2024, 5, 6), LastOrder = new(2026, 2, 3),
    };

    [Fact]
    public void Setting_Current_loads_every_control_without_making_it_dirty()
    {
        var r = new Rig();
        r.Binder.Current = Sample();
        Assert.Equal(("Ada", "London", "12,50", "first"), (r.Name.Text, r.City.Text, r.Rate.Text, r.Notes.Text));
        Assert.True(r.Vip.Checked && r.Company.Checked && !r.Person.Checked);
        Assert.Equal((3, 4, 1, 2), (r.Seats.Value, r.Rating.Position, r.Tier.ItemIndex, r.Terms.ItemIndex));
        Assert.Equal((new DateOnly(2024, 5, 6), new DateOnly(2026, 2, 3)), (r.Since.Value, r.LastOrder.Value));
        Assert.False(r.Binder.IsDirty);
        Assert.Equal(0, r.DirtyChanges);
    }

    [Fact]
    public void Typing_makes_it_dirty_typing_it_back_makes_it_clean()
    {
        var r = new Rig();
        var c = Sample();
        r.Binder.Current = c;
        r.Form.Simulate.Click(r.City);
        r.Form.Simulate.Key(Key.End);
        r.Form.Simulate.Type("!");
        Assert.True(r.Binder.IsDirty);
        Assert.Equal("London", c.City);                     // buffered: nothing written yet
        r.Form.Simulate.Key(Key.Backspace);
        Assert.False(r.Binder.IsDirty);
        Assert.Equal(2, r.DirtyChanges);
    }

    [Fact]
    public void Commit_writes_the_edited_fields_and_Revert_restores_the_controls()
    {
        var r = new Rig();
        var c = Sample();
        r.Binder.Current = c;
        r.TypeInto(r.Name, "Grace");
        r.Form.Simulate.Click(r.Vip);                        // uncheck
        r.Form.Simulate.Click(r.Person);
        r.TypeInto(r.Rate, "7,5");
        r.Form.Simulate.Click(r.Rating);
        r.Form.Simulate.Key(Key.Home);                       // rating 0
        r.Form.Simulate.Click(r.Tier);                       // opens the list...
        r.Form.Simulate.Key(Key.Escape);
        r.Form.Simulate.Key(Key.Down);                       // ...Down picks the next item
        Assert.True(r.Binder.IsDirty);
        Assert.Equal(6, r.Binder.Fields.Count(f => f.IsDirty));

        Assert.True(r.Binder.Commit());
        Assert.Empty(r.Binder.Errors);
        Assert.Equal(("Grace", false, Kind.Person, 7.5m, 0, 2), (c.Name, c.Vip, c.Kind, c.Rate, c.Rating, c.Tier));
        Assert.Equal("7,50", r.Rate.Text);                  // shown formatted again
        Assert.False(r.Binder.IsDirty);

        r.TypeInto(r.City, "Paris");
        r.Binder.Revert();
        Assert.Equal("London", r.City.Text);
        Assert.False(r.Binder.IsDirty);
    }

    [Fact]
    public void Invalid_input_is_reported_not_thrown_and_nothing_is_written()
    {
        var r = new Rig();
        var c = Sample();
        r.Binder.Current = c;
        r.TypeInto(r.Rate, "lots");
        r.TypeInto(r.Name, "x");
        r.Form.Simulate.Key(Key.Backspace);                 // empty name fails its validator
        r.TypeInto(r.City, "Rome");

        Assert.False(r.Binder.Commit());
        Assert.Equal(new[] { "Enter a name.", "Enter a number." }, r.Binder.Errors.Select(e => e.Message));
        Assert.Same(r.Name, r.Binder.Errors[0].Control);
        Assert.Equal("Enter a number.", r.Binder.Fields.Single(f => f.Control == r.Rate).Error);
        Assert.Equal(("Ada", "London", 12.5m), (c.Name, c.City, c.Rate));   // all or nothing
        Assert.True(r.Binder.FocusFirstError());
        Assert.True(r.Name.Focused);

        r.Form.Simulate.Type("Ann");
        r.TypeInto(r.Rate, "1.234,5");
        Assert.True(r.Binder.Validate());
        Assert.True(r.Binder.Commit());
        Assert.Equal(("Ann", "Rome", 1234.5m), (c.Name, c.City, c.Rate));
    }

    [Fact]
    public void A_spin_edit_commits_what_was_typed_and_checks_its_limits()
    {
        var r = new Rig();
        var c = Sample();
        r.Binder.Current = c;
        r.TypeInto(r.Seats, "12");                          // typed, not confirmed by Enter or leaving the box
        Assert.Equal(3, r.Seats.Value);
        Assert.True(r.Binder.IsDirty);
        Assert.True(r.Binder.Commit());
        Assert.Equal(12, c.Seats);

        r.TypeInto(r.Seats, "99");
        Assert.False(r.Binder.Commit());
        Assert.Equal("Enter a number from 1 to 50.", r.Binder.Errors.Single().Message);
    }

    [Fact]
    public void Combo_text_dates_and_optional_dates()
    {
        var f = new Form();
        f.SetBounds(0, 0, 400, 200);
        var cities = new ComboBox { Items = ["London", "Paris"] };
        var since = new DateTimePicker { Format = "yyyy-MM-dd" };
        var last = new DateTimePicker { Format = "yyyy-MM-dd", ShowCheckbox = true };
        cities.SetBounds(10, 10, 150, 26);
        since.SetBounds(10, 50, 150, 26);
        last.SetBounds(10, 90, 150, 26);
        f.Add(cities);
        f.Add(since);
        f.Add(last);
        f.PerformLayout();
        var c = Sample();
        var binder = new Binder<Contact>(c);
        binder.Bind(cities, x => x.City, (x, v) => x.City = v);
        binder.Bind(since, x => x.Since, (x, v) => x.Since = v);
        binder.Bind(last, x => x.LastOrder, (x, v) => x.LastOrder = v);
        Assert.Equal(0, cities.ItemIndex);
        Assert.True(last.Checked);

        f.Simulate.Click(cities);
        f.Simulate.Key(Key.Escape);
        f.Simulate.Key(Key.Down);
        since.Focus();
        f.Simulate.Key(Key.Up);                             // the year segment: 2025
        f.Simulate.Click(last.Left + 8, last.Top + last.Height / 2);   // the check box: no date
        Assert.Null(last.Value);
        Assert.True(binder.Commit());
        Assert.Equal(("Paris", new DateOnly(2025, 5, 6), (DateOnly?)null), (c.City, c.Since, c.LastOrder));

        since.ShowCheckbox = true;
        since.Checked = false;
        Assert.False(binder.Commit());
        Assert.Equal("Enter a date.", binder.Errors.Single().Message);
    }

    [Fact]
    public void A_model_change_elsewhere_updates_fields_the_user_has_not_edited()
    {
        var r = new Rig();
        var c = Sample();
        r.Binder.Current = c;
        r.TypeInto(r.City, "Paris");
        c.Name = "Ada L.";                                  // raises PropertyChanged
        c.City = "Berlin";
        Assert.Equal("Ada L.", r.Name.Text);                // not edited: follows the model
        Assert.Equal("Paris", r.City.Text);                 // edited: the user's text stays
        Assert.True(r.Binder.IsDirty);
        Assert.True(r.Binder.Commit());
        Assert.Equal(("Ada L.", "Paris"), (c.Name, c.City)); // the edit wins over the outside change

        c.Notes = "no event";                               // a plain property: Refresh picks it up
        Assert.Equal("first", r.Notes.Text);
        r.Binder.Refresh();
        Assert.Equal("no event", r.Notes.Text);
        Assert.False(r.Binder.IsDirty);
    }

    [Fact]
    public void Without_a_current_object_the_controls_are_cleared_and_disabled()
    {
        var r = new Rig();
        Assert.False(r.Name.Enabled);                       // Current starts null
        r.Binder.Current = Sample();
        Assert.True(r.Name.Enabled && r.Rating.Enabled && r.Person.Enabled);
        r.Notes.Enabled = false;                            // the application's own choice

        r.Binder.Current = null;
        Assert.False(r.Name.Enabled || r.Vip.Enabled || r.Company.Enabled || r.Since.Enabled);
        Assert.Equal(("", "", false, false, -1), (r.Name.Text, r.Seats.Text, r.Vip.Checked, r.Company.Checked, r.Tier.ItemIndex));
        Assert.False(r.Binder.IsDirty);
        Assert.True(r.Binder.Commit());

        r.Binder.Current = Sample();
        Assert.True(r.Name.Enabled);
        Assert.False(r.Notes.Enabled);                      // not re-enabled: the binder didn't disable it
    }

    [Fact]
    public void Change_handlers_see_the_dirty_flag_and_can_tell_loads_apart()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 100);
        var edit = new Edit();
        edit.SetBounds(10, 10, 200, 26);
        f.Add(edit);
        f.PerformLayout();
        var binder = new Binder<Contact>();
        var seen = new List<(bool Loading, bool Dirty)>();
        edit.Change += _ => seen.Add((binder.IsLoading, binder.IsDirty));   // subscribed before the binder, like generated code
        binder.Bind(edit, c => c.Name, (c, v) => c.Name = v);
        seen.Clear();
        binder.Current = new Contact { Name = "Bo" };
        f.Simulate.Click(edit);
        f.Simulate.Key(Key.End);
        f.Simulate.Type("b");
        Assert.Equal(new[] { (true, false), (false, true) }, seen);
    }

    [Fact]
    public void Read_only_fields_and_dispose()
    {
        var r = new Rig();
        var f = r.Form;
        var shown = new Edit();
        shown.SetBounds(300, 50, 100, 26);
        f.Add(shown);
        f.PerformLayout();
        var b = new Binder<Contact>(Sample());
        var field = b.Bind(shown, c => c.Name);
        Assert.True(field.IsReadOnly);
        f.Simulate.Click(shown);
        f.Simulate.Type("zz");
        Assert.False(b.IsDirty);

        b.Dispose();
        b.Current!.Name = "After";
        Assert.Equal("Adazz", shown.Text);                  // no longer listening
    }
}
