using System.Security.Cryptography;
using System.Text.Json;

namespace SwgCraft.Core;

/// <summary>
/// Copies the static JSON files that ship next to the exe (the "data" folder) into the user's own static data folder
/// (%AppData%\SwgCraft\data), which a release build reads and writes.
/// <list type="bullet">
/// <item>A file the user doesn't have yet is copied (on the first run, that is all of them).</item>
/// <item>When a newer app version ships a changed file, it replaces the user's copy only if the user never edited it;
///       an edited copy is kept and a message says a newer one was not applied.</item>
/// </list>
/// A small manifest (.seeded.json) in the user's folder remembers what was copied, so edits can be told apart from shipped content.
/// </summary>
public static class StaticDataSeeder
{
    public const string ManifestName = ".seeded.json";

    /// <summary>File name -> SHA-256 (hex) of the shipped file as it was last copied.</summary>
    sealed class Manifest
    {
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <returns>Messages worth showing on startup (copies made, newer files not applied, problems); empty when nothing happened.</returns>
    public static List<string> Seed(string shippedFolder, string userFolder)
    {
        var messages = new List<string>();
        if (!Directory.Exists(shippedFolder)) return messages;
        if (string.Equals(Path.GetFullPath(shippedFolder).TrimEnd('\\', '/'), Path.GetFullPath(userFolder).TrimEnd('\\', '/'),
                          StringComparison.OrdinalIgnoreCase))
            return messages;   // the same folder: nothing to copy

        Directory.CreateDirectory(userFolder);
        var manifestPath = Path.Combine(userFolder, ManifestName);
        var manifest = ReadManifest(manifestPath);
        var copied = new List<string>();
        var updated = new List<string>();
        bool changed = false;

        foreach (var shipped in Directory.EnumerateFiles(shippedFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(shipped);
            if (name.StartsWith('.')) continue;
            var target = Path.Combine(userFolder, name);
            try
            {
                var shippedHash = Hash(shipped);
                if (!File.Exists(target))
                {
                    File.Copy(shipped, target);
                    copied.Add(name);
                }
                else
                {
                    manifest.Files.TryGetValue(name, out var lastSeeded);
                    if (lastSeeded == shippedHash) continue;              // nothing new shipped
                    var targetHash = Hash(target);
                    if (targetHash == shippedHash) { }                     // already identical: just remember it
                    else if (lastSeeded != null && targetHash == lastSeeded)
                    {
                        File.Copy(shipped, target, overwrite: true);       // untouched by the user: take the newer version
                        updated.Add(name);
                    }
                    else
                    {
                        messages.Add($"A newer {name} came with this version of the app, but your copy has been edited, so it was kept. " +
                                     $"The new one is at {shipped}.");   // said once per shipped version: its hash is remembered below
                    }
                }
                manifest.Files[name] = shippedHash;
                changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add($"Could not copy {name} to {userFolder}: {ex.Message}");
            }
        }

        if (changed)
        {
            try { JsonFile.WriteAtomic(manifestPath, manifest); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add($"Could not write {manifestPath}: {ex.Message}");
            }
        }
        if (copied.Count > 0) messages.Insert(0, $"Copied {copied.Count} static data file(s) to {userFolder}: {string.Join(", ", copied)}.");
        if (updated.Count > 0) messages.Insert(0, $"Updated static data from this version of the app: {string.Join(", ", updated)}.");
        return messages;
    }

    static Manifest ReadManifest(string path)
    {
        if (!File.Exists(path)) return new Manifest();
        try
        {
            var m = JsonFile.Read<Manifest>(path);
            return new Manifest { Files = new Dictionary<string, string>(m.Files ?? new(), StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException) { return new Manifest(); }
    }

    static string Hash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}
