using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Configuration;

namespace OrdemServico.Lambda.Functions
{
    /// <summary>
    /// Compensação disparada por FILA (SQS: ordem-servico-compensacao).
    /// Recebe <see cref="ComandoCompensacao"/> enviados pela saga quando um passo falha (ou por
    /// outros serviços). Usa partial batch response: só as mensagens com erro voltam para a fila
    /// e, após o maxReceiveCount, vão para a DLQ.
    ///
    /// Handler: OrdemServico.Lambda::OrdemServico.Lambda.Functions.CompensacaoFilaFunction::Handle
    /// </summary>
    public class CompensacaoFilaFunction
    {
        private readonly IServiceProvider _serviceProvider;

        public CompensacaoFilaFunction() : this(DependencyInjection.BuildWorkerProvider())
        {
        }

        public CompensacaoFilaFunction(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task<SQSBatchResponse> Handle(SQSEvent sqsEvent, ILambdaContext context)
        {
            ILogger logger = _serviceProvider.GetRequiredService<ILogger<CompensacaoFilaFunction>>();
            List<SQSBatchResponse.BatchItemFailure> falhas = [];

            foreach (SQSEvent.SQSMessage mensagem in sqsEvent.Records)
            {
                try
                {
                    ComandoCompensacao comando = JsonSerializer.Deserialize<ComandoCompensacao>(mensagem.Body, JsonDefaults.Options)
                        ?? throw new JsonException("Mensagem de compensação vazia.");
                    Validar(comando, mensagem.Body);

                    using IServiceScope scope = _serviceProvider.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<CompensacaoService>().Executar(comando, "sqs");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Falha ao processar a mensagem de compensação {MessageId}", mensagem.MessageId);
                    falhas.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = mensagem.MessageId });
                }
            }

            return new SQSBatchResponse(falhas);
        }

        /// <summary>
        /// O desserializador não exige os campos do record. Sem idCompensacao todas as mensagens
        /// dividiriam o mesmo registro de idempotência; sem tipo, virariam estorno de estoque.
        /// </summary>
        private static void Validar(ComandoCompensacao comando, string corpo)
        {
            using JsonDocument documento = JsonDocument.Parse(corpo);

            if (string.IsNullOrWhiteSpace(comando.IdCompensacao)
                || comando.IdOrdemServico == Guid.Empty
                || !documento.RootElement.EnumerateObject().Any(campo => campo.Name.Equals("tipo", StringComparison.OrdinalIgnoreCase)))
            {
                throw new JsonException("idCompensacao, tipo e idOrdemServico são obrigatórios.");
            }

            if (comando.Tipo != TipoCompensacao.CancelarOrdemServico && comando.PecasInsumosIds is not { Count: > 0 })
                throw new JsonException($"pecasInsumosIds é obrigatório para {comando.Tipo}.");
        }
    }
}
