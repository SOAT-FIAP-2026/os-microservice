using Fiap.TechChallenge.Contracts.Common;
using Microsoft.AspNetCore.Http;
using OrdemServico.Lambda.Infrastructure.Security;

namespace OrdemServico.Lambda.Infrastructure.Http
{
    public interface ICoreApiAuthorization
    {
        string ObterAuthorizationHeader();
        string? ObterCorrelationId();
    }

    /// <summary>
    /// Em uma requisição HTTP repassa o Bearer token do usuário (mantém a autorização e a
    /// auditoria da API core). Fora dela (funções de compensação) usa um token de serviço.
    /// </summary>
    public class CoreApiAuthorization(IHttpContextAccessor httpContextAccessor, Lazy<ServiceTokenFactory> serviceTokenFactory) : ICoreApiAuthorization
    {
        public const string CorrelationHeader = CorrelationContext.HeaderName;

        private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
        private readonly Lazy<ServiceTokenFactory> _serviceTokenFactory = serviceTokenFactory;

        public string ObterAuthorizationHeader()
        {
            string? header = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.FirstOrDefault();

            return string.IsNullOrWhiteSpace(header)
                ? $"Bearer {_serviceTokenFactory.Value.Gerar()}"
                : header;
        }

        public string? ObterCorrelationId()
        {
            string? fromHttp = _httpContextAccessor.HttpContext?.Request.Headers[CorrelationHeader].FirstOrDefault();
            return !string.IsNullOrWhiteSpace(fromHttp) ? fromHttp : CorrelationContext.Current;
        }
    }
}
