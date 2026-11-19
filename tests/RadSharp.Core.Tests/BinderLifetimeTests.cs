using System.ComponentModel;

namespace RadSharp.Tests;

/// <summary>
/// <see cref="Binder{T}"/> lifetime and robustness: a binder with an owner form survives repeated shows (the Delphi "create
/// the dialog once, show it many times" pattern), explicit Dispose is terminal, loads nest, clean fields are parsed, and a
/// throwing getter leaves the form as it was.
/// </summary>
[Collection("PlatformHost")]   // closing forms notifies the process-wide platform host
public class BinderLifetimeTests
{
    public sealed class Rec : INotifyPropertyChanged
    {
        string _name = "";
        public Rec(string name) => _name = name;
        public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
        public string Other { get; set; } = "";
        public int Terms { get; set; }
        public Kind Kind { get; set; } = Kind.None;
        public DateOnly Since { get; set; }
        public int Subscribers;
        PropertyChangedEventHandler? _handlers;
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { _handlers += value; Subscribers++; }
            remove { _handlers -= value; Subscribers--; }
        }
        public void Raise(string n) => _handlers?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public enum Kind { None, A, B }

    sealed class Dialog
    {
        public readonly Form Form = new();
        public readonly Edit Name = new();
        public readonly Binder<Rec> Binder;

        public Dialog()
        {
            Form.SetBounds(0, 0, 300, 100);
            Name.SetBounds(10, 10, 200, 26);
            Form.Add(Name);
            Form.PerformLayout();
            Binder = new Binder<Rec>(Form);
            Binder.Bind(Name, r => r.Name, (r, v) => r.Name = v);
        }

        public void Show() { Form.PerformLayout(); Form.NotifyShown(); }

        public void Type(string text)
        {
            Form.Simulate.Click(Name);
            Name.SelectAll();
            Form.Simulate.Type(text);
        }
    }

    [Fact]
    public void A_binder_with_an_owner_survives_the_dialog_being_shown_again_for_another_record()
    {
        var d = new Dialog();
        var a = new Rec("A");
        var b = new Rec("B");
        d.Binder.Current = a;
        d.Show();
        Assert.Equal("A", d.Name.Text);
        d.Form.Close();

        d.Binder.Current = b;
        d.Show();
        Assert.Equal("B", d.Name.Text);
        d.Type("B2");
        Assert.True(d.Binder.IsDirty);
        Assert.True(d.Binder.Commit());
        Assert.Equal("B2", b.Name);
        Assert.Equal("A", a.Name);
        d.Form.Close();
    }

    [Fact]
    public void Showing_again_reloads_the_current_record_and_drops_edits_left_from_the_last_show()
    {
        var d = new Dialog();
        var a = new Rec("A");
        d.Binder.Current = a;
        d.Show();
        d.Type("typed");
        d.Form.Close();
        a.Name = "changed meanwhile";   // not heard: the binder doesn't listen while the dialog is closed
        d.Show();
        Assert.Equal("changed meanwhile", d.Name.Text);
        Assert.False(d.Binder.IsDirty);
    }

    [Fact]
    public void Closing_the_owner_stops_listening_to_the_model_and_showing_it_again_listens_again()
    {
        var d = new Dialog();
        var a = new Rec("A");
        d.Binder.Current = a;
        d.Show();
        Assert.Equal(1, a.Subscribers);
        d.Form.Close();
        Assert.Equal(0, a.Subscribers);
        d.Show();
        Assert.Equal(1, a.Subscribers);
        a.Name = "live";
        Assert.Equal("live", d.Name.Text);
    }

    [Fact]
    public void Explicit_Dispose_is_terminal()
    {
        var d = new Dialog();
        var a = new Rec("A");
        d.Binder.Current = a;
        d.Binder.Dispose();
        d.Binder.Dispose();   // twice is fine
        Assert.Equal(0, a.Subscribers);
        Assert.Throws<ObjectDisposedException>(() => d.Binder.Current = new Rec("B"));
        Assert.Throws<ObjectDisposedException>(() => d.Binder.Commit());
        Assert.Throws<ObjectDisposedException>(() => d.Binder.Validate());
        Assert.Throws<ObjectDisposedException>(() => d.Binder.Revert());
        Assert.Throws<ObjectDisposedException>(() => d.Binder.Refresh());
        d.Show();   // the owner raising events afterwards does nothing
        d.Form.Close();
        Assert.Equal(0, a.Subscribers);
    }

    [Fact]
    public void IsLoading_stays_true_for_the_whole_load_even_when_a_handler_makes_the_model_raise_PropertyChanged()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 100);
        Edit e1 = new(), e2 = new();
        e1.SetBounds(10, 10, 200, 26);
        e2.SetBounds(10, 40, 200, 26);
        f.Add(e1);
        f.Add(e2);
        f.PerformLayout();
        var b = new Binder<Rec>();
        b.Bind(e1, r => r.Name, (r, v) => r.Name = v);
        b.Bind(e2, r => r.Other, (r, v) => r.Other = v);
        var m1 = new Rec("one") { Other = "o1" };
        var m2 = new Rec("two") { Other = "o2" };
        b.Current = m1;
        f.Simulate.Click(e2);
        e2.SelectAll();
        f.Simulate.Type("typed");                         // e2 is dirty, so a refresh keeps it until the real load replaces it
        bool? e2SawLoading = null;
        e1.Change += _ => m2.Raise("Poke");               // touches the model during the load: a nested Refresh
        e2.Change += _ => e2SawLoading = b.IsLoading;
        b.Current = m2;
        Assert.Equal("o2", e2.Text);
        Assert.True(e2SawLoading);
        Assert.False(b.IsLoading);
    }
}
