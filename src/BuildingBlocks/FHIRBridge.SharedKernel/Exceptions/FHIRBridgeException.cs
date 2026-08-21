namespace FHIRBridge.SharedKernel.Exceptions;

/// <summary>
/// Base for domain exceptions that the API's global exception handler translates into a client
/// response. <see cref="Message"/> is the full diagnostic text (safe for logs/ErrorLog only);
/// <see cref="UserMessage"/> is what actually reaches the client and must never contain internal
/// identifiers, entity/type names, or other implementation detail. Subtypes whose <see cref="Message"/>
/// was already hand-written to be client-safe can omit <c>userMessage</c> — it then falls back to
/// <see cref="Message"/> unchanged.
/// </summary>
public abstract class FHIRBridgeException : Exception
{
    protected FHIRBridgeException(string message, string? userMessage = null) : base(message)
    {
        UserMessage = userMessage ?? message;
    }

    public string UserMessage { get; }
}
