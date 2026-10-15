using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwgCraft.Core;

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A misspelled property name in a hand-edited file fails at load instead of being ignored.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static T Read<T>(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<T>(text, Options)
               ?? throw new InvalidDataException($"{path} is empty or null.");
    }

    /// <summary>Writes to a temp file then swaps it in, keeping the previous version as .bak.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options) + Environment.NewLine, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
        else File.Move(tmp, path);
    }
}
