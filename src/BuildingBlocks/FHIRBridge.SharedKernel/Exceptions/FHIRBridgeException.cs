namespace FHIRBridge.SharedKernel.Exceptions;

public abstract class FHIRBridgeException : Exception
{
    protected FHIRBridgeException(string message) : base(message)
    {
    }
}
