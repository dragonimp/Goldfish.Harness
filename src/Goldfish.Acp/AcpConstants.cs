namespace Goldfish.Acp;

public static class AcpConstants
{
    public const int ProtocolVersion = 1;
    public const string JsonRpcVersion = "2.0";
    public const string SchemaRelease = "schema-v1.21.0";
    public const string VersionHeaderName = "X-AgentFree-Acp-Version";
    public const string SessionUpdateMethod = "session/update";
    public const string RequestPermissionMethod = "session/request_permission";
}
