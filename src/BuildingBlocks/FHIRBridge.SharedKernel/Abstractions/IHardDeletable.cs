namespace FHIRBridge.SharedKernel.Abstractions;

/// <summary>
/// Marks an <see cref="ISoftDeletable"/> entity whose removal must be a real DELETE. The auditing interceptor does
/// not convert its <c>Remove()</c> into a soft delete; the row is physically removed and the interceptor still
/// writes its "Deleted" audit row.
/// <para>For rows a unique index covers that must be grantable again after removal (a soft-deleted row would keep
/// occupying the index), such as permission allocations.</para>
/// </summary>
public interface IHardDeletable
{
}
