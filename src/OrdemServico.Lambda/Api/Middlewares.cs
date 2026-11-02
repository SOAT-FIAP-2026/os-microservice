using System.Diagnostics;
using System.Text.Json;
using OrdemServico.Lambda.Domain.Exceptions;

namespace OrdemServico.Lambda.Api
{
    /// <summary>Propaga/gera o X-Correlation-ID e abre um escopo de log com ele.</summary>
    public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        public const string HeaderName = "X-Correlation-ID";

        public async Task InvokeAsync(HttpContext context)
        {
            string? requestedId = context.Request.Headers[HeaderName].FirstOrDefault();
            string correlationId = !string.IsNullOrWhiteSpace(requestedId) && requestedId.Length <= 128
                ? requestedId
                : Guid.NewGuid().ToString();

            context.Request.Headers[HeaderName] = correlationId;
            context.Response.Headers[HeaderName] = correlationId;

            using (logger.BeginScope(new Dictionary<string, object> { ["correlation_id"] = correlationId }))
            {
                await next(context);
            }
        }
    }

    /// <summary>
    /// Mesmo contrato de erro do ExceptionHandlingMiddleware do monólito:
    /// { "statusCode": 400, "message": "...", "traceId": "..." }.
    /// </summary>
    public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                int statusCode = GetStatusCode(ex);
                string traceId = Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier;

                logger.LogError(ex, "Unhandled exception while processing request {Method} {Path} with status {StatusCode}",
                    context.Request.Method, context.Request.Path, statusCode);

                context.Response.Clear();
                context.Response.Headers[CorrelationIdMiddleware.HeaderName] = context.Request.Headers[CorrelationIdMiddleware.HeaderName];
                context.Response.ContentType = "application/json";
                context.Response.StatusCode = statusCode;

                await context.Response.WriteAsync(JsonSerializer.Serialize(new ErrorResponse(statusCode, GetMessage(ex), traceId), JsonOptions));
            }
        }

        private static int GetStatusCode(Exception exception) => exception switch
        {
            ConcorrenciaOrdemServicoException => StatusCodes.Status409Conflict,
            DomainException => StatusCodes.Status400BadRequest,
            ArgumentException => StatusCodes.Status400BadRequest,
            InvalidOperationException => StatusCodes.Status400BadRequest,
            UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
            BadHttpRequestException badRequest => badRequest.StatusCode,
            _ => StatusCodes.Status500InternalServerError
        };

        private static string GetMessage(Exception exception) => exception switch
        {
            DomainException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or BadHttpRequestException => exception.Message,
            _ => "An unexpected internal error occurred."
        };

        private sealed record ErrorResponse(int StatusCode, string Message, string TraceId);
    }
}
