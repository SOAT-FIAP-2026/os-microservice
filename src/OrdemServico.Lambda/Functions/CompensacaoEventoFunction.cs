using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SNSEvents;
using Fiap.TechChallenge.Contracts.OsService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Configuration;

namespace OrdemServico.Lambda.Functions
{
    /// <summary>
    /// Compensação disparada por EVENTO (SNS). O filtro da assinatura do tópico encaminha, pelo
    /// atributo de mensagem tipoEvento:
    ///   - OrcamentoReprovado     (techchallenge.ordem-servico) → estorna o estoque das peças;
    ///   - OrdemServicoCancelada  (techchallenge.ordem-servico) → estorna o estoque das peças;
    ///   - ReservaEstoqueFalhou   (techchallenge.estoque)       → remove a peça da OS e recalcula o orçamento;
    ///   - PagamentoRecusado      (techchallenge.pagamento)     → cancela a OS (que gera OrdemServicoCancelada).
    /// Erros são relançados: o Lambda faz as retentativas assíncronas e, esgotadas, envia o evento para a DLQ.
    ///
    /// Handler: OrdemServico.Lambda::OrdemServico.Lambda.Functions.CompensacaoEventoFunction::Handle
    /// </summary>
    public class CompensacaoEventoFunction
    {
        private readonly IServiceProvider _serviceProvider;

        public CompensacaoEventoFunction() : this(DependencyInjection.BuildWorkerProvider())
        {
        }

        public CompensacaoEventoFunction(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task Handle(SNSEvent snsEvent, ILambdaContext context)
        {
            ILogger logger = _serviceProvider.GetRequiredService<ILogger<CompensacaoEventoFunction>>();

            foreach (SNSEvent.SNSRecord registro in snsEvent.Records)
            {
                SNSEvent.SNSMessage mensagem = registro.Sns;
                ComandoCompensacao? comando = Traduzir(mensagem);

                if (comando == null)
                {
                    logger.LogWarning("Mensagem {MessageId} de tipo {TipoEvento} não gera compensação. Ignorada.",
                        mensagem.MessageId, LerAtributo(mensagem, "tipoEvento"));
                    continue;
                }

                using IServiceScope scope = _serviceProvider.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CompensacaoService>()
                    .Executar(comando, $"sns:{LerAtributo(mensagem, "origem") ?? mensagem.TopicArn}");
            }
        }

        internal static ComandoCompensacao? Traduzir(SNSEvent.SNSMessage mensagem)
        {
            string? tipoEvento = LerAtributo(mensagem, "tipoEvento");

            if (tipoEvento is not ("OrcamentoReprovado" or "OrdemServicoCancelada" or "ReservaEstoqueFalhou" or "PagamentoRecusado"))
                return null;

            using JsonDocument documento = JsonDocument.Parse(mensagem.Message);
            JsonElement detail = documento.RootElement;

            // idEvento ou eventId é estável entre republicações do outbox; o MessageId do SNS muda a cada Publish.
            string? idEvento = LerTexto(detail, "idEvento") ?? LerTexto(detail, "eventId");
            string idCompensacao = $"evt-{idEvento ?? mensagem.MessageId}";

            return tipoEvento switch
            {
                "OrcamentoReprovado" => new ComandoCompensacao(
                    idCompensacao, TipoCompensacao.EstornarEstoque, LerGuid(detail, "idOrdemServico", "osId"),
                    LerGuids(detail, "pecasInsumosIds"), "Orçamento reprovado pelo cliente"),

                "OrdemServicoCancelada" => new ComandoCompensacao(
                    idCompensacao, TipoCompensacao.EstornarEstoque, LerGuid(detail, "idOrdemServico", "osId"),
                    LerGuids(detail, "pecasInsumosIds"), LerTexto(detail, "motivo") ?? "Ordem de serviço cancelada"),

                "ReservaEstoqueFalhou" => new ComandoCompensacao(
                    idCompensacao, TipoCompensacao.RemoverPecaInsumo, LerGuid(detail, "idOrdemServico", "osId"),
                    detail.TryGetProperty("idPecaInsumo", out _) ? [LerGuid(detail, "idPecaInsumo")] : LerGuids(detail, "pecasInsumosIds"),
                    LerTexto(detail, "motivo") ?? "Reserva de estoque falhou"),

                _ => new ComandoCompensacao(
                    idCompensacao, TipoCompensacao.CancelarOrdemServico, LerGuid(detail, "idOrdemServico", "osId"),
                    [], LerTexto(detail, "motivo") ?? "Pagamento recusado")
            };
        }

        private static string? LerAtributo(SNSEvent.SNSMessage mensagem, string nome)
        {
            return mensagem.MessageAttributes != null && mensagem.MessageAttributes.TryGetValue(nome, out SNSEvent.MessageAttribute? atributo)
                ? atributo.Value
                : null;
        }

        private static Guid LerGuid(JsonElement detail, params string[] propriedades)
        {
            foreach (var prop in propriedades)
            {
                if (detail.TryGetProperty(prop, out JsonElement valor))
                {
                    if (valor.ValueKind == JsonValueKind.String && valor.TryGetGuid(out Guid guid))
                        return guid;
                }
            }
            if (propriedades.Length > 0 && detail.TryGetProperty(propriedades[0], out JsonElement fallback))
                return fallback.GetGuid();
            throw new KeyNotFoundException($"Nenhuma das propriedades [{string.Join(", ", propriedades)}] foi encontrada.");
        }

        private static IReadOnlyCollection<Guid> LerGuids(JsonElement detail, string propriedade)
        {
            return detail.TryGetProperty(propriedade, out JsonElement lista) && lista.ValueKind == JsonValueKind.Array
                ? [.. lista.EnumerateArray().Select(item => item.GetGuid())]
                : [];
        }

        private static string? LerTexto(JsonElement detail, string propriedade)
        {
            return detail.TryGetProperty(propriedade, out JsonElement valor) && valor.ValueKind == JsonValueKind.String
                ? valor.GetString()
                : null;
        }
    }
}
