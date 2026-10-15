using System.Text;

namespace SwgCraft.Core;

// ---------------------------------------------------------------- strings.json: the client's English string tables (.stf)

/// <summary>Persisted shape of strings.json: every imported .stf string table, each with its ID/text pairs.</summary>
public sealed class StringsDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<StringFile> Files { get; set; } = new();
}

/// <summary>One .stf string table.</summary>
public sealed class StringFile
{
    /// <summary>
    /// Path below the import folder, without the extension and with '/' separators: "obj_n", "space/space_item".
    /// This is the part of a "@space/space_item:some_id" reference before the colon.
    /// </summary>
    public string Name { get; set; } = "";
    public List<StringEntry> Entries { get; set; } = new();
}

public sealed class StringEntry
{
    public string Id { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Read-only lookup over a strings document: file names and IDs ignore case, as the game does.</summary>
public sealed class StringCatalog
{
    public static StringCatalog Empty { get; } = new(new StringsDocument());

    readonly Dictionary<string, Dictionary<string, string>> _files = new(StringComparer.OrdinalIgnoreCase);

    public StringsDocument Document { get; }

    public StringCatalog(StringsDocument document)
    {
        Document = document;
        foreach (var f in document.Files)
        {
            if (string.IsNullOrEmpty(f.Name) || _files.ContainsKey(f.Name)) continue;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in f.Entries)
                if (!string.IsNullOrEmpty(e.Id)) map.TryAdd(e.Id, e.Value);
            _files[f.Name] = map;
        }
    }

    /// <summary>The text for an ID in a file, or null.</summary>
    public string? Find(string file, string id) =>
        _files.TryGetValue(file, out var map) && map.TryGetValue(id, out var v) ? v : null;

    /// <summary>Resolves a "@file:id" reference (the leading @ is optional); null if it isn't one or isn't found.</summary>
    public string? Resolve(string reference)
    {
        var r = (reference ?? "").Trim().TrimStart('@');
        int colon = r.LastIndexOf(':');
        return colon <= 0 || colon == r.Length - 1 ? null : Find(r[..colon], r[(colon + 1)..]);
    }
}

// ---------------------------------------------------------------- reading .stf files

/// <summary>
/// Reads a SWG string table (.stf). Little-endian layout:
/// <code>
/// uint32 magic (0x0000ABCD), byte flag, uint32 next free index, uint32 count,
/// count x { uint32 index, uint32 crc, uint32 length, UTF-16 text[length] }   -- the texts
/// count x { uint32 index, uint32 length, 8-bit text[length] }                -- the IDs, matched to the texts by index
/// </code>
/// </summary>
public static class StfReader
{
    public const uint Magic = 0x0000ABCD;

    /// <exception cref="InvalidDataException">The data is not a valid string table.</exception>
    public static List<StringEntry> Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        try
        {
            uint magic = r.ReadUInt32();
            if (magic != Magic) throw new InvalidDataException($"not a string table (starts with 0x{magic:X8}, expected 0x{Magic:X8})");
            r.ReadByte();      // flag
            r.ReadUInt32();    // next free index
            uint count = r.ReadUInt32();
            if (count > 10_000_000) throw new InvalidDataException($"implausible entry count {count}");

            var texts = new Dictionary<uint, string>((int)Math.Min(count, 100_000));
            for (uint i = 0; i < count; i++)
            {
                uint index = r.ReadUInt32();
                r.ReadUInt32();                        // crc
                uint length = r.ReadUInt32();          // in UTF-16 characters
                texts[index] = Encoding.Unicode.GetString(ReadExactly(r, checked((long)length * 2)));
            }

            var entries = new List<StringEntry>((int)Math.Min(count, 100_000));
            for (uint i = 0; i < count; i++)
            {
                uint index = r.ReadUInt32();
                uint length = r.ReadUInt32();          // in bytes
                var id = Encoding.Latin1.GetString(ReadExactly(r, length));
                entries.Add(new StringEntry { Id = id, Value = texts.TryGetValue(index, out var t) ? t : "" });
            }
            return entries;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("the file ends early (truncated or not a string table)");
        }
    }

    static byte[] ReadExactly(BinaryReader r, long length)
    {
        var s = r.BaseStream;
        if (length < 0 || (s.CanSeek && length > s.Length - s.Position)) throw new EndOfStreamException();
        var bytes = r.ReadBytes((int)length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }

    /// <summary>Writes a string table in the same layout (used by the tests to make sample files).</summary>
    public static void Write(Stream stream, IReadOnlyList<StringEntry> entries)
    {
        using var w = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        w.Write(Magic);
        w.Write((byte)1);
        w.Write((uint)entries.Count + 1);
        w.Write((uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            w.Write((uint)(i + 1));
            w.Write(0xFFFFFFFFu);
            w.Write((uint)entries[i].Value.Length);
            w.Write(Encoding.Unicode.GetBytes(entries[i].Value));
        }
        for (int i = 0; i < entries.Count; i++)
        {
            var id = Encoding.Latin1.GetBytes(entries[i].Id);
            w.Write((uint)(i + 1));
            w.Write((uint)id.Length);
            w.Write(id);
        }
    }
}

public sealed record StfImportResult(StringsDocument Document, int StringCount, IReadOnlyList<string> Problems);

/// <summary>Reads every .stf file below a folder (the client's string/en folder) into a strings document.</summary>
public static class StfImporter
{
    /// <summary>"C:\x\string\en" + "C:\x\string\en\space\space_item.stf" -> "space/space_item".</summary>
    public static string NameFor(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        rel = Path.ChangeExtension(rel, null) ?? rel;
        return rel.Replace('\\', '/');
    }

    /// <summary>Files that can't be read are skipped and listed in <see cref="StfImportResult.Problems"/>; the rest are imported.</summary>
    public static StfImportResult ImportFolder(string root, IProgress<string>? progress = null)
    {
        var doc = new StringsDocument();
        var problems = new List<string>();
        int strings = 0;

        var files = Directory.EnumerateFiles(root, "*.stf", SearchOption.AllDirectories)
                             .Select(f => (Path: f, Name: NameFor(root, f)))
                             .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                             .ToList();
        int done = 0;
        foreach (var (path, name) in files)
        {
            if (++done % 50 == 0) progress?.Report($"Reading {done} of {files.Count}: {name}");
            try
            {
                List<StringEntry> entries;
                using (var fs = File.OpenRead(path)) entries = StfReader.Read(fs);

                // an ID that appears twice keeps its first text
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var kept = new List<StringEntry>(entries.Count);
                int dropped = 0;
                foreach (var e in entries)
                    if (e.Id.Length > 0 && seen.Add(e.Id)) kept.Add(e); else dropped++;
                if (dropped > 0) problems.Add($"{name}: skipped {dropped} empty or repeated ID(s).");

                doc.Files.Add(new StringFile { Name = name, Entries = kept });
                strings += kept.Count;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{name}: not imported ({ex.Message}).");
            }
        }
        return new StfImportResult(doc, strings, problems);
    }
}

// ---------------------------------------------------------------- validation

public static class StringsValidator
{
    public static List<ValidationIssue> Validate(StringsDocument doc)
    {
        var issues = new List<ValidationIssue>();
        if (doc.Files.Count == 0)
            issues.Add(new ValidationIssue(Severity.Warning, "list.empty", "No string files yet. Use Import to read the client's .stf files."));

        var names = new Dictionary<string, List<StringFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in doc.Files)
        {
            var name = f.Name?.Trim() ?? "";
            if (name.Length == 0) { issues.Add(new ValidationIssue(Severity.Error, "file.name.empty", "A string file has no name.", f)); continue; }
            if (!names.TryGetValue(name, out var l)) names[name] = l = new List<StringFile>();
            l.Add(f);
            if (name.Contains('\\') || name.EndsWith(".stf", StringComparison.OrdinalIgnoreCase))
                issues.Add(new ValidationIssue(Severity.Warning, "file.name.format",
                    $"File name '{name}' should use '/' between folders and leave out the .stf extension.", f));

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var e in f.Entries)
            {
                n++;
                var id = e.Id?.Trim() ?? "";
                if (id.Length == 0) issues.Add(new ValidationIssue(Severity.Error, "id.empty", $"{name}: string {n} has no ID.", f));
                else if (!ids.Add(id)) issues.Add(new ValidationIssue(Severity.Error, "id.duplicate", $"{name}: ID '{id}' is used more than once.", f));
            }
        }
        foreach (var (name, list) in names)
            if (list.Count > 1)
                foreach (var f in list)
                    issues.Add(new ValidationIssue(Severity.Error, "file.name.duplicate", $"The file name '{name}' is used {list.Count} times.", f));
        return issues;
    }
}
