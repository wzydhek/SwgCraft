using SwgCraft.Core;

namespace SwgCraft.Tests;

public sealed class StaticDataSeederTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}");
    string Shipped => Path.Combine(_root, "exe", "data");
    string User => Path.Combine(_root, "appdata", "data");

    public StaticDataSeederTests() => Directory.CreateDirectory(Shipped);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
    }

    void Ship(string name, string content) => File.WriteAllText(Path.Combine(Shipped, name), content);
    string Mine(string name) => File.ReadAllText(Path.Combine(User, name));

    [Fact]
    public void The_first_run_copies_every_shipped_json_file()
    {
        Ship("schematics.json", "{ \"a\": 1 }");
        Ship("tabs.json", "{ \"b\": 1 }");
        File.WriteAllText(Path.Combine(Shipped, "notes.txt"), "not data");

        var messages = StaticDataSeeder.Seed(Shipped, User);
        Assert.Equal("{ \"a\": 1 }", Mine("schematics.json"));
        Assert.Equal("{ \"b\": 1 }", Mine("tabs.json"));
        Assert.False(File.Exists(Path.Combine(User, "notes.txt")));
        Assert.Contains(messages, m => m.StartsWith("Copied 2"));

        Assert.Empty(StaticDataSeeder.Seed(Shipped, User));   // second run: nothing to do, nothing to say
    }

    [Fact]
    public void A_newer_shipped_file_replaces_an_untouched_copy_but_never_an_edited_one()
    {
        Ship("schematics.json", "v1");
        Ship("tabs.json", "v1");
        StaticDataSeeder.Seed(Shipped, User);

        File.WriteAllText(Path.Combine(User, "tabs.json"), "my edits");   // the user edits one file
        Ship("schematics.json", "v2");                                    // a new release ships both changed
        Ship("tabs.json", "v2");

        var messages = StaticDataSeeder.Seed(Shipped, User);
        Assert.Equal("v2", Mine("schematics.json"));
        Assert.Equal("my edits", Mine("tabs.json"));
        Assert.Contains(messages, m => m.StartsWith("Updated") && m.Contains("schematics.json"));
        Assert.Contains(messages, m => m.Contains("tabs.json") && m.Contains("edited"));

        Assert.Empty(StaticDataSeeder.Seed(Shipped, User));   // the kept edit is reported once per shipped version
    }

    [Fact]
    public void A_file_deleted_by_the_user_is_copied_again_and_an_existing_unknown_copy_is_kept()
    {
        Ship("tabs.json", "shipped");
        Directory.CreateDirectory(User);
        File.WriteAllText(Path.Combine(User, "tabs.json"), "already here");   // e.g. copied by hand before this feature

        var first = StaticDataSeeder.Seed(Shipped, User);
        Assert.Equal("already here", Mine("tabs.json"));
        Assert.Contains(first, m => m.Contains("edited"));

        File.Delete(Path.Combine(User, "tabs.json"));
        StaticDataSeeder.Seed(Shipped, User);
        Assert.Equal("shipped", Mine("tabs.json"));
    }

    [Fact]
    public void Nothing_happens_when_there_is_no_shipped_folder_or_it_is_the_same_folder()
    {
        Assert.Empty(StaticDataSeeder.Seed(Path.Combine(_root, "missing"), User));
        Ship("tabs.json", "x");
        Assert.Empty(StaticDataSeeder.Seed(Shipped, Shipped));
    }
}
