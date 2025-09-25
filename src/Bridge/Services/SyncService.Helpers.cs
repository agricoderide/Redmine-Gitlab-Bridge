using System.Security.Cryptography;
using System.Text;
using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.Services;

public sealed partial class SyncService
{

    internal static class DescriptionUtils
    {
        public static string AddOrUpdateSourceLink(string? description, string sourceUrl)
        {
            var body = (description ?? string.Empty).Replace("\r\n", "\n").TrimStart('\n');
            if (body.StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
            {
                var nl = body.IndexOf('\n');
                var rest = nl >= 0 ? body[(nl + 1)..].TrimStart('\n') : string.Empty;
                return rest.Length > 0 ? $"Source: {sourceUrl}\n\n{rest}" : $"Source: {sourceUrl}";
            }
            return body.Length > 0 ? $"Source: {sourceUrl}\n\n{body}" : $"Source: {sourceUrl}";
        }
    }

    public static string ExtractSearchKey(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "";
        username = username.Trim();
        var parts = username.Split(new[] { '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
            return parts[^1];
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

    private static IReadOnlyList<string> NormalizeLabels(List<string>? labels) =>
        (labels ?? new()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool LabelsEqual(List<string>? a, List<string>? b) =>
        NormalizeLabels(a).SequenceEqual(NormalizeLabels(b), StringComparer.OrdinalIgnoreCase);

    public static bool ValueEquals(IssueBasic a, IssueBasic b)
    {
        if (!string.Equals(a.Title, b.Title, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Description ?? "", b.Description ?? "", StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Status ?? "", b.Status ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Nullable.Equals(a.AssigneeId, b.AssigneeId)) return false;
        if (!Nullable.Equals(a.DueDate, b.DueDate)) return false;
        if (!LabelsEqual(a.Labels, b.Labels)) return false;
        return true;
    }
}
