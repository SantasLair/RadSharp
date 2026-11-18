using Tally.Model;

namespace Tally.Data;

/// <summary>Generates a believable, repeatable data set: the same seed gives the same clients, invoices and lines.</summary>
public static class DemoData
{
    static readonly string[] FirstNames =
    [
        "Ada", "Alan", "Amara", "Anna", "Ben", "Carla", "Chen", "Clara", "Daniel", "Diego", "Elena", "Emil", "Eva", "Farah",
        "Felix", "Grace", "Hana", "Hugo", "Ines", "Ivan", "Jonas", "Julia", "Kai", "Lara", "Leon", "Lina", "Luca", "Maya",
        "Mila", "Nadia", "Noah", "Omar", "Paula", "Rosa", "Sami", "Sofia", "Theo", "Uma", "Vera", "Yusuf",
    ];

    static readonly string[] LastNames =
    [
        "Abbott", "Bauer", "Bianchi", "Brandt", "Costa", "Dubois", "Eriksen", "Fischer", "García", "Hansen", "Ivanova",
        "Jensen", "Kaya", "Kowalski", "Larsen", "Lindqvist", "Martin", "Meyer", "Moreau", "Nakamura", "Novak", "Okafor",
        "Petrov", "Quinn", "Rossi", "Santos", "Schmidt", "Silva", "Tanaka", "Vogel", "Weber", "Yilmaz", "Zimmermann",
    ];

    static readonly string[] CompanyWords =
    [
        "Alder", "Beacon", "Cedar", "Copper", "Delta", "Ember", "Fjord", "Granite", "Harbor", "Iris", "Juniper", "Kestrel",
        "Lumen", "Maple", "Nimbus", "Orchard", "Pioneer", "Quarry", "Riverside", "Summit", "Tidewater", "Umber", "Vale",
        "Willow",
    ];

    static readonly string[] CompanyKinds = ["Studio", "Design", "Bakery", "Consulting", "Labs", "Builders", "Media", "Garden Co.", "Clinic", "Workshop"];

    static readonly (string City, string Country, string Prefix)[] Places =
    [
        ("Amsterdam", "Netherlands", "+31"), ("Berlin", "Germany", "+49"), ("Bologna", "Italy", "+39"), ("Copenhagen", "Denmark", "+45"),
        ("Dublin", "Ireland", "+353"), ("Gothenburg", "Sweden", "+46"), ("Hamburg", "Germany", "+49"), ("Kraków", "Poland", "+48"),
        ("Lisbon", "Portugal", "+351"), ("Lyon", "France", "+33"), ("Madrid", "Spain", "+34"), ("Oslo", "Norway", "+47"),
        ("Porto", "Portugal", "+351"), ("Prague", "Czechia", "+420"), ("Rotterdam", "Netherlands", "+31"), ("Seville", "Spain", "+34"),
        ("Toulouse", "France", "+33"), ("Utrecht", "Netherlands", "+31"), ("Vienna", "Austria", "+43"), ("Zürich", "Switzerland", "+41"),
    ];

    static readonly (string Text, decimal Price)[] Services =
    [
        ("Design consultation (hour)", 85m), ("Logo design", 450m), ("Website maintenance (month)", 120m), ("Photography (half day)", 380m),
        ("Copywriting (page)", 65m), ("Illustration", 240m), ("Print layout (page)", 45m), ("Workshop facilitation (day)", 900m),
        ("Hosting (year)", 96m), ("Travel expenses", 38.50m), ("Brand guidelines", 1200m), ("Social media kit", 310m),
    ];

    /// <summary>A store with <paramref name="clients"/> clients and about three invoices each, dated around <paramref name="today"/>.</summary>
    public static MemoryStore CreateStore(int clients, int seed, DateOnly today)
    {
        var store = new MemoryStore(today);
        Fill(store, clients, seed);
        return store;
    }

    public static void Fill(MemoryStore store, int clients, int seed)
    {
        var random = new Random(seed);
        var today = store.Today;
        var invoices = new List<(Invoice Invoice, List<InvoiceLine> Lines)>();
        for (int i = 0; i < clients; i++)
        {
            var first = Pick(random, FirstNames);
            var last = Pick(random, LastNames);
            var place = Pick(random, Places);
            bool company = random.Next(3) > 0;
            var companyName = company ? $"{Pick(random, CompanyWords)} {Pick(random, CompanyKinds)}" : "";
            var domain = company ? companyName.Split(' ')[0].ToLowerInvariant() + ".example" : "mail.example";
            var client = new Client
            {
                Name = $"{first} {last}",
                Company = companyName,
                Email = $"{Ascii(first)}.{Ascii(last)}{(random.Next(4) == 0 ? random.Next(10, 99).ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}@{domain}",
                Phone = $"{place.Prefix} {random.Next(100, 999)} {random.Next(1000, 9999)}",
                Street = $"{Pick(random, CompanyWords)}straat {random.Next(1, 200)}",
                City = place.City,
                Postcode = random.Next(1000, 99999).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Country = place.Country,
                Archived = random.Next(12) == 0,
                CreatedAt = today.AddDays(-random.Next(30, 2000)),
            };
            store.Add(client);

            int count = random.Next(0, 7);
            for (int n = 0; n < count; n++)
            {
                var issued = today.AddDays(-random.Next(0, 720));
                var due = issued.AddDays(random.Next(2) == 0 ? 14 : 30);
                int age = today.DayNumber - issued.DayNumber;
                var status = age < 20 && random.Next(3) == 0 ? InvoiceStatus.Draft
                    : age > 60 && random.Next(10) > 0 ? InvoiceStatus.Paid
                    : random.Next(3) == 0 ? InvoiceStatus.Paid : InvoiceStatus.Sent;
                var invoice = new Invoice
                {
                    ClientId = client.Id,
                    IssueDate = issued,
                    DueDate = due,
                    Status = status,
                    PaidOn = status == InvoiceStatus.Paid ? due.AddDays(-random.Next(0, 10)) : null,
                };
                var lines = Enumerable.Range(0, random.Next(1, 5)).Select(_ =>
                {
                    var (text, price) = Pick(random, Services);
                    return new InvoiceLine
                    {
                        Description = text,
                        Quantity = random.Next(4) == 0 ? random.Next(1, 9) + 0.5m : random.Next(1, 6),
                        UnitPrice = price,
                        TaxRate = random.Next(5) == 0 ? 0m : 21m,
                    };
                }).ToList();
                invoices.Add((invoice, lines));
            }
        }

        // Numbered per year in the order they were issued, as a real ledger is: 2026-00001, 2026-00002…
        var next = new Dictionary<int, int>();
        foreach (var (invoice, lines) in invoices.OrderBy(i => i.Invoice.IssueDate))
        {
            int year = invoice.IssueDate.Year;
            next[year] = next.GetValueOrDefault(year) + 1;
            invoice.Number = $"{year}-{next[year]:D5}";
            store.Add(invoice, lines);
        }
    }

    static T Pick<T>(Random random, T[] items) => items[random.Next(items.Length)];

    static string Ascii(string s) =>
        new([.. s.Normalize(System.Text.NormalizationForm.FormD).Where(c => c < 128 && char.IsLetter(c)).Select(char.ToLowerInvariant)]);
}
