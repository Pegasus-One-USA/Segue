namespace FHIRBridge.Domain.Enums;

/// <summary>
/// What a source connection may be used for. Read is the default and is what every connection created before this
/// setting existed is. Write lets an EHR Write-Back destination send creates over the connection's credentials;
/// Write alone stops the connection being used as a workflow source.
///
/// <para>Not <c>[Flags]</c>, and Read stays 0 so the CLR default, the EF default and the migration default agree.
/// Use <see cref="SourceConnectionAccessExtensions"/> rather than comparing values.</para>
/// </summary>
public enum SourceConnectionAccess
{
    Read = 0,
    Write = 1,
    ReadWrite = 2,
}

public static class SourceConnectionAccessExtensions
{
    public static bool AllowsRead(this SourceConnectionAccess access) =>
        access is SourceConnectionAccess.Read or SourceConnectionAccess.ReadWrite;

    public static bool AllowsWrite(this SourceConnectionAccess access) =>
        access is SourceConnectionAccess.Write or SourceConnectionAccess.ReadWrite;
}
