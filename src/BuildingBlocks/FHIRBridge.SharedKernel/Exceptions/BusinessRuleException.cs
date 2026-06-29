namespace FHIRBridge.SharedKernel.Exceptions;

public sealed class BusinessRuleException : FHIRBridgeException
{
    public BusinessRuleException(string message) : base(message)
    {
    }
}
