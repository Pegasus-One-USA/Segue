namespace FHIRBridge.Application.Security;

public static class UnifiedPermissions
{
    public const string TenantsRead = "tenants.read";
    public const string TenantsWrite = "tenants.write";
    public const string ConfigurationWrite = "configuration.write";
    public const string PipelineExecute = "pipeline.execute";
    public const string AuditLogsRead = "auditlogs.read";
    public const string SourceConnectionsTest = "sourceconnections.test";
}
