using SwgCraft.Core;

namespace SwgCraft.Tests;

public sealed class StringTableTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); } catch (IOException) { }
    }

    static StringEntry E(string id, string value) => new() { Id = id, Value = value };

    void WriteStf(string relativePath, params StringEntry[] entries)
    {
        var path = Path.Combine(_folder, "string", "en", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        StfReader.Write(fs, entries);
    }

    string Root => Path.Combine(_folder, "string", "en");

    [Fact]
    public void A_string_table_is_read_with_ids_and_unicode_text()
    {
        var ms = new MemoryStream();
        StfReader.Write(ms, new[] { E("blaster", "Blaster"), E("quote", "It's a “good” one\nline two"), E("empty", "") });
        ms.Position = 0;
        var entries = StfReader.Read(ms);
        Assert.Equal(new[] { "blaster", "quote", "empty" }, entries.Select(e => e.Id).ToArray());
        Assert.Equal("It's a “good” one\nline two", entries[1].Value);
        Assert.Equal("", entries[2].Value);
    }

    [Fact]
    public void Ids_are_matched_to_texts_by_index_not_by_position()
    {
        // texts written in order 2,1; ids in order 1,2
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.Latin1, leaveOpen: true))
        {
            w.Write(StfReader.Magic); w.Write((byte)1); w.Write(3u); w.Write(2u);
            foreach (var (idx, text) in new[] { (2u, "two"), (1u, "one") })
            {
                w.Write(idx); w.Write(0xFFFFFFFFu); w.Write((uint)text.Length); w.Write(System.Text.Encoding.Unicode.GetBytes(text));
            }
            foreach (var (idx, id) in new[] { (1u, "a"), (2u, "b") })
            {
                w.Write(idx); w.Write((uint)id.Length); w.Write(System.Text.Encoding.Latin1.GetBytes(id));
            }
        }
        ms.Position = 0;
        var entries = StfReader.Read(ms);
        Assert.Equal("one", entries.Single(e => e.Id == "a").Value);
        Assert.Equal("two", entries.Single(e => e.Id == "b").Value);
    }

    [Fact]
    public void Garbage_and_truncated_files_are_rejected_clearly()
    {
        Assert.Throws<InvalidDataException>(() => StfReader.Read(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 })));

        var ms = new MemoryStream();
        StfReader.Write(ms, new[] { E("blaster", "Blaster rifle") });
        var cut = ms.ToArray()[..^5];
        Assert.Throws<InvalidDataException>(() => StfReader.Read(new MemoryStream(cut)));
    }

    [Fact]
    public void A_folder_is_imported_with_names_relative_to_it_and_bad_files_reported()
    {
        WriteStf("obj_n.stf", E("blaster", "Blaster"), E("rifle", "Rifle"));
        WriteStf(Path.Combine("space", "space_item.stf"), E("engine", "Engine"), E("engine", "Duplicate"), E("", "no id"));
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(Path.Combine(Root, "broken.stf"), new byte[] { 0, 1, 2 });

        var result = StfImporter.ImportFolder(Root);
        Assert.Equal(new[] { "obj_n", "space/space_item" }, result.Document.Files.Select(f => f.Name).ToArray());
        Assert.Equal(3, result.StringCount);
        Assert.Contains(result.Problems, p => p.StartsWith("broken:"));
        Assert.Contains(result.Problems, p => p.StartsWith("space/space_item:") && p.Contains("skipped 2"));
        Assert.Equal("Engine", result.Document.Files[1].Entries.Single().Value);   // the first text of a repeated ID is kept
        Assert.Empty(StringsValidator.Validate(result.Document));
    }

    [Fact]
    public void References_resolve_ignoring_case()
    {
        var doc = new StringsDocument();
        doc.Files.Add(new StringFile { Name = "space/space_item", Entries = { E("engine", "Engine") } });
        var cat = new StringCatalog(doc);
        Assert.Equal("Engine", cat.Resolve("@space/space_item:engine"));
        Assert.Equal("Engine", cat.Resolve("Space/Space_Item:ENGINE"));
        Assert.Null(cat.Resolve("@space/space_item:nope"));
        Assert.Null(cat.Resolve("no colon"));
    }

    [Fact]
    public void The_validator_catches_missing_and_repeated_names_and_ids()
    {
        var doc = new StringsDocument();
        doc.Files.Add(new StringFile { Name = "obj_n", Entries = { E("a", "A"), E("A", "again"), E("", "no id") } });
        doc.Files.Add(new StringFile { Name = "OBJ_N" });
        doc.Files.Add(new StringFile { Name = "craft\\x.stf" });
        var issues = StringsValidator.Validate(doc);
        Assert.Contains(issues, i => i.Code == "id.duplicate" && i.Severity == Severity.Error);
        Assert.Contains(issues, i => i.Code == "id.empty" && i.Severity == Severity.Error);
        Assert.Contains(issues, i => i.Code == "file.name.duplicate" && i.Severity == Severity.Error);
        Assert.Contains(issues, i => i.Code == "file.name.format" && i.Severity == Severity.Warning);
    }

    [Fact]
    public void Strings_are_saved_and_loaded()
    {
        WriteStf("obj_n.stf", E("blaster", "Blaster"));
        var store = new StaticDataStore(Path.Combine(_folder, "data"));
        Assert.True(store.SaveStrings(StfImporter.ImportFolder(Root).Document).Saved);
        var back = store.LoadStrings();
        Assert.Null(back.Error);
        Assert.Equal("Blaster", new StringCatalog(back.Value).Find("obj_n", "blaster"));
    }
}
