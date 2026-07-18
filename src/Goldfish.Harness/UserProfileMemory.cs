using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Goldfish.Harness;

/// <summary>
/// Extracts only explicit, stable first-person preferences/facts. Arbitrary dialogue,
/// inferred traits and high-risk personal data are deliberately not admitted.
/// </summary>
public static class UserProfileMemory
{
    private const string GlobalAgentId = "__user_profile_global__";
    private const string ProfileWorkspaceId = "__user_profile__";

    private static readonly Regex KeyValuePattern = new(
        @"^(?:请记住[：:\s]*)?(?:我(?:偏好|喜欢|常用|习惯)(?:的)?|我的|偏好的?)(?<category>[\p{L}\p{N}_\-]{1,16})(?:是|为|：|:)(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GeneralPreferencePattern = new(
        @"^我(?:偏好|喜欢)(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ExcludedCategories =
    [
        "密码", "口令", "令牌", "token", "密钥", "apikey", "api_key",
        "身份证", "银行卡", "信用卡", "手机号", "电话", "住址", "家庭地址",
        "病史", "诊断", "宗教", "政治", "生物识别"
    ];

    public static MemoryPartition ProfilePartition(
        MemoryPartition source,
        UserProfileScope scope)
        => source with
        {
            AgentId = scope == UserProfileScope.Global ? GlobalAgentId : source.AgentId,
            WorkspaceId = ProfileWorkspaceId,
            SessionId = string.Empty
        };

    public static async Task<int> ExtractAndStoreAsync(
        IMemoryManager memoryManager,
        MemoryPartition source,
        string userMessage,
        UserProfileMemoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(memoryManager);
        options ??= new UserProfileMemoryOptions();
        if (!options.Enabled || !options.AutoExtract || string.IsNullOrWhiteSpace(source.UserId))
            return 0;

        var profilePartition = ProfilePartition(source, options.Scope);
        var extracted = Extract(userMessage)
            .Where(item => item.Confidence >= options.MinimumConfidence)
            .Take(Math.Max(0, options.MaxMemories))
            .ToList();

        foreach (var item in extracted)
        {
            var identity = string.Join('\n',
                profilePartition.TenantId,
                profilePartition.UserId,
                profilePartition.AgentId,
                item.Category.ToLowerInvariant());
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant()[..32];
            await memoryManager.AddMemoryAsync(profilePartition, new MemoryEntry
            {
                Id = id,
                Type = "UserPreference",
                Category = item.Category,
                Content = $"用户偏好的{item.Category}是{item.Value}。",
                Importance = 0.8,
                Confidence = item.Confidence,
                Sensitivity = MemorySensitivity.Internal,
                Metadata =
                {
                    ["profileScope"] = options.Scope.ToString(),
                    ["source"] = "explicit-user-statement"
                }
            });
        }

        return extracted.Count;
    }

    public static IReadOnlyList<ExtractedUserProfile> Extract(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return [];
        var result = new List<ExtractedUserProfile>();
        foreach (var rawClause in Regex.Split(userMessage.Trim(), @"[，,。；;\n]+"))
        {
            var clause = rawClause.Trim();
            if (clause.Length is < 3 or > 160) continue;

            var match = KeyValuePattern.Match(clause);
            var category = match.Success ? match.Groups["category"].Value.Trim() : string.Empty;
            var value = match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
            if (!match.Success)
            {
                match = GeneralPreferencePattern.Match(clause);
                if (!match.Success) continue;
                category = "一般偏好";
                value = match.Groups["value"].Value.Trim();
            }

            if (value.Length is < 1 or > 100 || IsExcluded(category) || IsExcluded(value))
                continue;
            result.Add(new ExtractedUserProfile(category, value, 0.9));
        }

        return result
            .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
    }

    private static bool IsExcluded(string value)
        => ExcludedCategories.Any(item =>
            value.Contains(item, StringComparison.OrdinalIgnoreCase));
}

public sealed record ExtractedUserProfile(string Category, string Value, double Confidence);
