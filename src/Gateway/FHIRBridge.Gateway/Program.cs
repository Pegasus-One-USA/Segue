// Placeholder host for the API gateway skeleton.
// Replace with the real YARP reverse-proxy + observability bootstrap when porting the Gateway.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "FHIRBridge Gateway placeholder");
app.Run();
