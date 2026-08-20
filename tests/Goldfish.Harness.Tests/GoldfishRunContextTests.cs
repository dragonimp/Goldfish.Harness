using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class GoldfishRunContextTests
{
    [Fact]
    public void FromAgentInfo_PrefersCanonicalCallerUsernameOverTransportName()
    {
        var context = GoldfishRunContext.FromAgentInfo(new AgentInfo
        {
            ExtraData = new Dictionary<string, string>
            {
                ["caller.username"] = "wzs",
                ["SenderName"] = "transport-display-name",
                ["UserId"] = "internal-user-id"
            }
        }, "session-1");

        Assert.Equal("wzs", context.User.Name);
        Assert.Equal("internal-user-id", context.User.Id);
    }
}
