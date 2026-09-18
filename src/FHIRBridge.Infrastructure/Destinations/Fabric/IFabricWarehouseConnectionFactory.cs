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
}
