using System.Text.Json;
using System.Text.Json.Serialization;
using Hub.Api;
using Hub.Booking;
using Hub.Core;
using Hub.Mapping;
using Hub.Migrations.Postgres;
using Hub.Suppliers.Fakes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var services = builder.Services;

services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.GetCorrelationId();
});
services.AddExceptionHandler<ApiExceptionHandler>();
services.AddHubOpenApi();

services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName)).ValidateOnStart();
services.AddOptions<SseOptions>().Bind(configuration.GetSection(SseOptions.SectionName)).ValidateOnStart();
services.AddOptions<IdentityStoreOptions>().Bind(configuration.GetSection(IdentityStoreOptions.SectionName)).ValidateOnStart();
services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName)).ValidateOnStart();
services.TryAddEnumerable(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>());
services.AddOptions<DemoOptions>().Bind(configuration.GetSection(DemoOptions.SectionName));
services.TryAddEnumerable(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>());
services.TryAddEnumerable(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IValidateOptions<SseOptions>, SseOptionsValidator>());
services.TryAddEnumerable(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IValidateOptions<IdentityStoreOptions>, IdentityStoreOptionsValidator>());

services.AddSingleton<ApiKeyStore>();
// The core services are enough for a header-key scheme; the full AddAuthentication also pulls in data protection,
// which this API never uses and which would log warnings about where it keeps keys.
services.AddAuthenticationCore(options => options.DefaultScheme = ApiKeyDefaults.Scheme);
services.AddWebEncoders();
new AuthenticationBuilder(services).AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null);
services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Client, policy => policy.RequireRole(Roles.Client, Roles.Admin))
    .AddPolicy(Policies.Admin, policy => policy.RequireRole(Roles.Admin));

services.AddHubCore(configuration);
services.AddHotelMapping(configuration);
services.AddCrosswalk(configuration);
services.AddFakeSuppliers(configuration);
services.AddHubBookings(configuration, runBackgroundServices: configuration.GetValue("Hub:Bookings:RunBackgroundServices", true));

var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
if (database.Provider == DatabaseProvider.Postgres)
{
    services.AddBookingDatabase(options => options.ConfigureBooking(database.ConnectionString), useMigrations: true);
    services.AddCrosswalkDatabase(options => options.ConfigureCrosswalk(database.CrosswalkConnectionString), useMigrations: true);
}
else
{
    services.AddBookingDatabase(options => options.UseSqlite(database.ConnectionString), useMigrations: false);
    services.AddCrosswalkDatabase(options => options.UseSqlite(database.CrosswalkConnectionString), useMigrations: false);
}

var identityStore = configuration.GetSection(IdentityStoreOptions.SectionName).Get<IdentityStoreOptions>() ?? new IdentityStoreOptions();
switch (identityStore.Provider)
{
    case IdentityStoreProvider.Postgres:
        services.AddEfIdentityStore(
            options => options.ConfigureMapping(identityStore.ConnectionString),
            SchemaInitialization.Migrate,
            identityStore.CreateDatabase ? ct => PostgresMigrations.EnsureDatabaseExistsAsync(identityStore.ConnectionString, ct) : null);
        break;
    case IdentityStoreProvider.Sqlite:
        services.AddEfIdentityStore(options => options.UseSqlite(identityStore.ConnectionString));
        break;
    default:
        services.AddInMemoryIdentityStore();
        break;
}

services.AddHostedService<DemoIdentitySeeder>();

services.AddHealthChecks()
    .AddCheck<SupplierRegistryHealthCheck>("suppliers", tags: ["ready"])
    .AddCheck<IdentityStoreHealthCheck>("identity-store", tags: ["ready"])
    .AddCheck<BookingDatabaseHealthCheck>("booking-database", tags: ["ready"])
    .AddCheck<CrosswalkDatabaseHealthCheck>("crosswalk-database", tags: ["ready"]);

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthResponseWriter.WriteAsync }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync,
}).AllowAnonymous();
app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();

var api = app.MapGroup("/api/v1");
api.MapSearchEndpoints();
api.MapSupplierEndpoints();
api.MapMappingEndpoints();
api.MapBookingEndpoints();
api.MapCrosswalkEndpoints();

app.Run();

public partial class Program;
