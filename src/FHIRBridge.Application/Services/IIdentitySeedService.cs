namespace FHIRBridge.Application.Services;

public interface IIdentitySeedService
{
    Task SeedAsync(CancellationToken cancellationToken);
}
