namespace FHIRBridge.SharedKernel.Exceptions;

public sealed class NotFoundException : FHIRBridgeException
{
    public NotFoundException(string entityName, object id)
        : base($"{entityName} with id '{id}' was not found.")
    {
    }
}
