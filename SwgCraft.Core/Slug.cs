using System.Text;

namespace SwgCraft.Core;

public static class Slug
{
    /// <summary>"Steel: Kiirium" -> "steel_kiirium".</summary>
    public static string From(string s)
    {
        var sb = new StringBuilder();
        bool lastUnderscore = true;
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) { sb.Append(ch); lastUnderscore = false; }
            else if (!lastUnderscore) { sb.Append('_'); lastUnderscore = true; }
        }
        return sb.ToString().TrimEnd('_');
    }
}
