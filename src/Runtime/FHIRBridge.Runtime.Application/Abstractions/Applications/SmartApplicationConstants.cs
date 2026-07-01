namespace FHIRBridge.Runtime.Application.Abstractions.Applications;

/// <summary>The OAuth 2.0 grant an application type authenticates with.</summary>
public static class SmartOAuthFlows
{
    public const string ClientCredentials = "client_credentials";
    public const string AuthorizationCode = "authorization_code";
}

/// <summary>The SMART scope prefix an application type requests resources under (system/, user/, patient/).</summary>
public static class SmartScopePrefixes
{
    public const string System = "system/";
    public const string User = "user/";
    public const string Patient = "patient/";
}
