// Bridge/Services/RedmineParsing.cs
using System.Text.Json;

namespace Bridge.Services;

public static class RedmineParsing
{
    // Return first valid absolute URL (line-based), or null if none
    public static string? ExtractSingleGitLabUrlFromProject(JsonElement project, string customFieldName)
    {
        if (!project.TryGetProperty("custom_fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var field in fields.EnumerateArray())
        {
            if (!field.TryGetProperty("name", out var nameProp)) continue;
            if (!string.Equals(nameProp.GetString(), customFieldName, StringComparison.Ordinal)) continue;

            var raw = field.GetProperty("value").GetString() ?? "";
            foreach (var token in raw.Split(new[] { '\r','\n',';',' ', ',' },
                                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Uri.TryCreate(token, UriKind.Absolute, out _))
                    return token; // pick the first
            }
        }
        return null;
    }

    public static string PathFromUrl(string url)
    {
        var u = new Uri(url);
        return u.AbsolutePath.Trim('/'); // e.g. mygroup/myrepo
    }
}
