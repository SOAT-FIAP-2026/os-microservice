using Microsoft.Extensions.Configuration;

namespace OrdemServico.Lambda.Configuration
{
    /// <summary>
    /// Configuração lida das variáveis de ambiente definidas no template CloudFormation.
    /// </summary>
    public sealed class LambdaSettings
    {
        public string TableName { get; init; } = string.Empty;
        public string EventosTopicArn { get; init; } = string.Empty;
        public string EventoOrigem { get; init; } = "techchallenge.ordem-servico";
        public string CompensacaoQueueUrl { get; init; } = string.Empty;
        public string CoreApiBaseUrl { get; init; } = string.Empty;
        public string? JwtIssuer { get; init; }
        public string? JwtAudience { get; init; }
        public string EmailRemetente { get; init; } = "osstatusupdate@resend.dev";
        public string? EmailDestinoOverride { get; init; }

        public static LambdaSettings FromConfiguration(IConfiguration configuration)
        {
            return new LambdaSettings
            {
                TableName = configuration["TABLE_NAME"] ?? string.Empty,
                EventosTopicArn = configuration["EVENTOS_TOPIC_ARN"] ?? string.Empty,
                EventoOrigem = configuration["EVENTO_ORIGEM"] ?? "techchallenge.ordem-servico",
                CompensacaoQueueUrl = configuration["COMPENSACAO_QUEUE_URL"] ?? string.Empty,
                CoreApiBaseUrl = configuration["CORE_API_BASE_URL"] ?? string.Empty,
                JwtIssuer = configuration["JWT_ISSUER"],
                JwtAudience = configuration["JWT_AUDIENCE"],
                EmailRemetente = configuration["EMAIL_REMETENTE"] ?? "osstatusupdate@resend.dev",
                EmailDestinoOverride = configuration["EMAIL_DESTINO_OVERRIDE"]
            };
        }
    }
}
