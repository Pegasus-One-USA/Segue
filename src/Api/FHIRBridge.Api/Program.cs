// Placeholder host for the API skeleton.
// Replace with the real bootstrap (auth, controllers, Swagger, DI) when porting the Api.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "FHIRBridge API placeholder");
app.Run();
