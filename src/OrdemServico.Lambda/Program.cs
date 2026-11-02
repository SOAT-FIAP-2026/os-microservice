using System.Text;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleSystemsManagement;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OrdemServico.Lambda.Api;
using OrdemServico.Lambda.Configuration;
using OrdemServico.Lambda.Infrastructure.Security;

[assembly: Amazon.Lambda.Core.LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

// Função HTTP: API Gateway (HTTP API, payload 2.0) → ASP.NET Core minimal API.
// Handler no CloudFormation: "OrdemServico.Lambda" (nome do assembly).
var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddOrdemServicoLambda(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

// Mesma validação de JWT do monólito (HMAC-SHA256, issuer e audience). Tokens emitidos pela
// API core ou pela Lambda de autenticação continuam válidos aqui.
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<JwtBearerOptions>>(sp =>
    new Microsoft.Extensions.Options.ConfigureNamedOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        JwtSettings jwt = sp.GetRequiredService<JwtSettings>();
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ValidateIssuer = !string.IsNullOrWhiteSpace(jwt.Issuer),
            ValidIssuer = jwt.Issuer,
            ValidateAudience = !string.IsNullOrWhiteSpace(jwt.Audience),
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    }));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddAuthorization();
builder.Services.AddSwaggerConfiguration();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Ordem de Serviço API v1");
    c.RoutePrefix = "swagger";
});

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/v1/ordens-servico/health", () => Results.Ok(new { status = "Healthy" }))
    .WithTags("Health")
    .WithSummary("Health check do microsserviço de Ordem de Serviço")
    .Produces(StatusCodes.Status200OK);
app.MapOrdemServicoEndpoints();

app.Run();

public partial class Program;
