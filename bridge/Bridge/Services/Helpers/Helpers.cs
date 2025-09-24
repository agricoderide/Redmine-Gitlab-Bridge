
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bridge.Contracts;

namespace Bridge.Services;

public static class Helpers
{



    public static string ExtractSearchKey(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "";

        username = username.Trim();

        // if username has separators like john.prior → take last part
        var parts = username.Split(new[] { '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
            return parts[^1];

        // compact handles like "rprior" → drop the first letter if long enough
        if (username.Length >= 4)
            return username.Substring(1);

        return username;
    }

    public static string Compute(IssueBasic i)
    {
        static string N(string? s) => s ?? "";
        var parts = new[]
        {
            N(i.Title),
            N(i.Description),
            string.Join(",", (i.Labels ?? new()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            i.AssigneeId?.ToString() ?? "",
            i.DueDate?.ToString("yyyy-MM-dd") ?? "",
            N(i.Status)
        };
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", parts));
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

}
