using FHIRBridge.Domain.Entities;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Opens an authenticated TDS connection to a Fabric Warehouse.
///
/// Deliberately separate from <see cref="IOneLakeClientFactory"/> even though both resolve the same Entra identity,
/// because the token they need is not the same one: OneLake is a storage audience and the Warehouse is a SQL
/// audience (<c>https://database.windows.net/.default</c>). A token minted for one is rejected by the other, so
/// widening the OneLake factory to cover both would produce an authentication failure that looks like a
/// permissions problem. Keeping them apart also gives the Warehouse strategy the same test seam the OneLake path
/// has — a live Fabric tenant is not needed to unit-test the code around the connection.
/// </summary>
public interface IFabricWarehouseConnectionFactory
{
    Task<SqlConnection> OpenAsync(
        DestinationConfiguration destination,
        FabricDestinationSettings settings,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a Warehouse connection from AD-HOC settings that have no saved <see cref="DestinationConfiguration"/>
    /// behind them — the destination wizard's table picker and its schema-authoring actions, which run
    /// before anything is provisioned (the SQL-family destinations have always worked that way; see
    /// SqlDestinationSchemaService's probe path). The secret, when the identity is a service principal, is passed
    /// directly rather than resolved from Key Vault, because there is no stored secret reference yet.
    /// </summary>
    Task<SqlConnection> OpenAdHocAsync(
        FabricDestinationSettings settings,
        string? secret,
        CancellationToken cancellationToken);
}
