using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Fiap.TechChallenge.Contracts.Common;
using Fiap.TechChallenge.Contracts.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Configuration;
using OrdemServico.Lambda.Domain.Entities;

namespace OrdemServico.Lambda.Infrastructure.Messaging
{
    public sealed class OsSqsBackgroundWorker : BackgroundService
    {
        private readonly IAmazonSQS _sqs;
        private readonly IConfiguration _configuration;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<OsSqsBackgroundWorker> _logger;
        private readonly string _queueUrl;

        public OsSqsBackgroundWorker(
            IAmazonSQS sqs,
            IConfiguration configuration,
            IServiceScopeFactory scopeFactory,
            ILogger<OsSqsBackgroundWorker> logger)
        {
            _sqs = sqs ?? throw new ArgumentNullException(nameof(sqs));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _queueUrl = _configuration["OS_EVENTOS_QUEUE_URL"]
                ?? _configuration["OS_QUEUE_URL"]
                ?? _configuration["Aws:OsEventosQueueUrl"]
                ?? "http://floci:4566/000000000000/techchallenge-os-eventos-local";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME")))
            {
                _logger.LogInformation("Executando dentro do ambiente AWS Lambda. Desabilitando polling do OsSqsBackgroundWorker.");
                return;
            }

            _logger.LogInformation("Iniciando OsSqsBackgroundWorker na fila {QueueUrl}...", _queueUrl);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var receiveRequest = new ReceiveMessageRequest
                    {
                        QueueUrl = _queueUrl,
                        MaxNumberOfMessages = 10,
                        WaitTimeSeconds = 3,
                        MessageAttributeNames = new List<string> { "All" }
                    };

                    var response = await _sqs.ReceiveMessageAsync(receiveRequest, stoppingToken);

                    if (response.Messages == null || response.Messages.Count == 0)
                    {
                        continue;
                    }

                    foreach (var message in response.Messages)
                    {
                        try
                        {
                            await ProcessarMensagemAsync(message, stoppingToken);
                            await _sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Erro ao processar mensagem SQS {MessageId} na fila {QueueUrl}.", message.MessageId, _queueUrl);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro no polling da fila SQS {QueueUrl}. Nova tentativa em 3 segundos...", _queueUrl);
                    try
                    {
                        await Task.Delay(3000, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation("OsSqsBackgroundWorker finalizado.");
        }

        internal async Task ProcessarMensagemAsync(Message message, CancellationToken stoppingToken)
        {
            var (conteudoJson, tipoEvento) = ExtrairConteudoETipoEvento(message);

            _logger.LogInformation("Mensagem SQS recebida no os-microservice. Tipo: {TipoEvento}", tipoEvento);

            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOrdemServicoRepository>();
            var publisher = scope.ServiceProvider.GetService<SnsPublisher>();
            var compensacaoService = scope.ServiceProvider.GetRequiredService<CompensacaoService>();

            switch (tipoEvento)
            {
                case nameof(DiagnosticoIniciadoEvent):
                case "DiagnosticoIniciado":
                    await ProcessarDiagnosticoIniciadoAsync(conteudoJson, repo, stoppingToken);
                    break;

                case nameof(DiagnosticoTecnicoConcluidoEvent):
                case "DiagnosticoTecnicoConcluido":
                    await ProcessarDiagnosticoTecnicoConcluidoAsync(conteudoJson, repo, stoppingToken);
                    break;

                case nameof(OrcamentoGeradoEvent):
                case "OrcamentoGerado":
                    await ProcessarOrcamentoGeradoAsync(conteudoJson, repo, stoppingToken);
                    break;

                case nameof(PagamentoConfirmadoEvent):
                case "PagamentoConfirmado":
                    await ProcessarPagamentoConfirmadoAsync(conteudoJson, repo, publisher, stoppingToken);
                    break;

                case nameof(ProducaoIniciadaEvent):
                case "ProducaoIniciada":
                    await ProcessarProducaoIniciadaAsync(conteudoJson, repo, stoppingToken);
                    break;

                case nameof(ProducaoFinalizadaEvent):
                case "ProducaoFinalizada":
                    await ProcessarProducaoFinalizadaAsync(conteudoJson, repo, stoppingToken);
                    break;

                case nameof(PagamentoCanceladoCompensacaoEvent):
                case "PagamentoCanceladoCompensacao":
                case nameof(PagamentoRecusadoEvent):
                case "PagamentoRecusado":
                case nameof(ProducaoCanceladaEvent):
                case "ProducaoCancelada":
                case "OrcamentoReprovado":
                case "OrdemServicoCancelada":
                case nameof(OrdemServicoCanceladaEvent):
                    await ProcessarCompensacaoAsync(tipoEvento, conteudoJson, compensacaoService, stoppingToken);
                    break;

                default:
                    _logger.LogDebug("Evento {TipoEvento} não requer ação do os-microservice.", tipoEvento);
                    break;
            }
        }

        internal static (string ConteudoJson, string TipoEvento) ExtrairConteudoETipoEvento(Message message)
        {
            string payload = message.Body;
            string tipoEvento = string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(message.Body);
                var root = doc.RootElement;

                // Envelope SNS Notification
                if (root.TryGetProperty("Type", out var typeProp) && typeProp.GetString() == "Notification")
                {
                    if (root.TryGetProperty("Message", out var msgProp))
                        payload = msgProp.GetString() ?? message.Body;

                    if (root.TryGetProperty("MessageAttributes", out var msgAttrs))
                    {
                        if (msgAttrs.TryGetProperty(SnsMessageAttributes.TipoEvento, out var teProp) &&
                            teProp.TryGetProperty("Value", out var valProp))
                        {
                            tipoEvento = valProp.GetString() ?? string.Empty;
                        }
                        else if (msgAttrs.TryGetProperty("TipoEvento", out var teProp2) &&
                                 teProp2.TryGetProperty("Value", out var valProp2))
                        {
                            tipoEvento = valProp2.GetString() ?? string.Empty;
                        }
                    }
                }
            }
            catch
            {
                // Fallback para raw payload em caso de erro no parse do envelope
            }

            if (string.IsNullOrEmpty(tipoEvento) && message.MessageAttributes != null)
            {
                if (message.MessageAttributes.TryGetValue(SnsMessageAttributes.TipoEvento, out var attrVal) ||
                    message.MessageAttributes.TryGetValue("TipoEvento", out attrVal))
                {
                    tipoEvento = attrVal.StringValue;
                }
            }

            if (string.IsNullOrEmpty(tipoEvento))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("tipoEvento", out var tProp) ||
                        doc.RootElement.TryGetProperty("TipoEvento", out tProp))
                    {
                        tipoEvento = tProp.GetString() ?? string.Empty;
                    }
                }
                catch
                {
                }
            }

            return (payload, tipoEvento);
        }

        private async Task ProcessarDiagnosticoIniciadoAsync(string json, IOrdemServicoRepository repo, CancellationToken ct)
        {
            var evento = JsonSerializer.Deserialize<DiagnosticoIniciadoEvent>(json, JsonDefaults.Options);
            Guid osId = evento?.OsId ?? Guid.Empty;
            if (osId == Guid.Empty)
            {
                using var doc = JsonDocument.Parse(json);
                osId = ObterOsId(doc.RootElement);
            }

            if (osId == Guid.Empty)
            {
                _logger.LogWarning("DiagnosticoIniciadoEvent recebido sem OsId válido: {Json}", json);
                return;
            }

            var os = await repo.ObterPorId(osId);
            if (os == null)
            {
                _logger.LogWarning("OS {OsId} não encontrada para DiagnosticoIniciadoEvent.", osId);
                return;
            }

            if (os.Status == StatusOS.Recebida)
            {
                os.IniciarDiagnostico();
                os.AlterarStatus(StatusOS.EmDiagnostico);
                await repo.Atualizar(os);
                _logger.LogInformation("OS {OsId} avançada para EM_DIAGNOSTICO após DiagnosticoIniciadoEvent.", osId);
            }
            else
            {
                if (os.DataInicioDiagnostico == null)
                {
                    os.IniciarDiagnostico();
                    await repo.Atualizar(os);
                }
                _logger.LogInformation("OS {OsId} já está no status {Status}. Status mantido.", osId, os.Status.Codigo);
            }
        }

        private async Task ProcessarDiagnosticoTecnicoConcluidoAsync(string json, IOrdemServicoRepository repo, CancellationToken ct)
        {
            var evento = JsonSerializer.Deserialize<DiagnosticoTecnicoConcluidoEvent>(json, JsonDefaults.Options);
            Guid osId = evento?.OsId ?? Guid.Empty;
            if (osId == Guid.Empty)
            {
                using var doc = JsonDocument.Parse(json);
                osId = ObterOsId(doc.RootElement);
            }

            if (osId == Guid.Empty)
            {
                _logger.LogWarning("DiagnosticoTecnicoConcluidoEvent recebido sem OsId válido: {Json}", json);
                return;
            }

            var os = await repo.ObterPorId(osId);
            if (os == null)
            {
                _logger.LogWarning("OS {OsId} não encontrada para DiagnosticoTecnicoConcluidoEvent.", osId);
                return;
            }

            os.IniciarDiagnostico();

            if (evento?.Servicos != null && evento.Servicos.Count > 0 && os.ItensServico.Count == 0)
            {
                var servicos = evento.Servicos.Select(s => new ItemServico(
                    s.IdServico,
                    s.Descricao ?? "Serviço Técnico",
                    150m,
                    60
                )).ToList();

                var pecas = (evento.Pecas ?? []).Select(p => new ItemPecaInsumo(
                    p.IdPeca,
                    p.Descricao ?? "Peça/Insumo",
                    50m
                )).ToList();

                os.SincronizarItens(servicos, pecas);
            }

            if (os.Status == StatusOS.Recebida || os.Status == StatusOS.EmDiagnostico)
            {
                os.AlterarStatus(StatusOS.AguardandoAprovacao);
                await repo.Atualizar(os);
                _logger.LogInformation("OS {OsId} avançada para AGUARDANDO_APROVACAO após DiagnosticoTecnicoConcluidoEvent.", osId);
            }
            else
            {
                await repo.Atualizar(os);
                _logger.LogInformation("OS {OsId} no status {Status}. Atualizado diagnóstico.", osId, os.Status.Codigo);
            }
        }

        private async Task ProcessarOrcamentoGeradoAsync(string json, IOrdemServicoRepository repo, CancellationToken ct)
        {
            var evento = JsonSerializer.Deserialize<OrcamentoGeradoEvent>(json, JsonDefaults.Options);
            Guid osId = evento?.OsId ?? Guid.Empty;
            decimal valorTotal = evento?.ValorTotal ?? 0m;

            if (osId == Guid.Empty)
            {
                using var doc = JsonDocument.Parse(json);
                osId = ObterOsId(doc.RootElement);
                if (valorTotal == 0m && (doc.RootElement.TryGetProperty("valorTotal", out var vt) || doc.RootElement.TryGetProperty("ValorTotal", out vt)))
                {
                    vt.TryGetDecimal(out valorTotal);
                }
            }

            if (osId == Guid.Empty)
            {
                _logger.LogWarning("OrcamentoGeradoEvent recebido sem OsId válido: {Json}", json);
                return;
            }

            var os = await repo.ObterPorId(osId);
            if (os == null)
            {
                _logger.LogWarning("OS {OsId} não encontrada para OrcamentoGeradoEvent.", osId);
                return;
            }

            bool alterou = false;
            if (valorTotal > 0 && os.ValorOrcamento != valorTotal)
            {
                os.AtualizarValorOrcamento(valorTotal);
                alterou = true;
            }

            if (os.Status == StatusOS.Recebida || os.Status == StatusOS.EmDiagnostico)
            {
                os.AlterarStatus(StatusOS.AguardandoAprovacao);
                alterou = true;
            }

            if (alterou)
            {
                await repo.Atualizar(os);
                _logger.LogInformation("OS {OsId} atualizada com orçamento {Valor} e status {Status}.", osId, os.ValorOrcamento, os.Status.Codigo);
            }
        }

        private async Task ProcessarPagamentoConfirmadoAsync(
            string json,
            IOrdemServicoRepository repo,
            SnsPublisher? publisher,
            CancellationToken ct)
        {
            var evento = JsonSerializer.Deserialize<PagamentoConfirmadoEvent>(json, JsonDefaults.Options);
            Guid osId = evento?.OsId ?? Guid.Empty;
            if (osId == Guid.Empty)
            {
                using var doc = JsonDocument.Parse(json);
                osId = ObterOsId(doc.RootElement);
            }

            if (osId == Guid.Empty)
            {
                _logger.LogWarning("PagamentoConfirmadoEvent recebido sem OsId válido: {Json}", json);
                return;
            }

            var os = await repo.ObterPorId(osId);
            if (os == null)
            {
                _logger.LogWarning("OS {OsId} não encontrada para PagamentoConfirmadoEvent.", osId);
                return;
            }

            if (os.Status != StatusOS.OrcamentoAprovado &&
                os.Status != StatusOS.EmExecucao &&
                os.Status != StatusOS.Finalizada &&
                os.Status != StatusOS.Entregue &&
                os.Status != StatusOS.Cancelada)
            {
                os.AlterarStatus(StatusOS.OrcamentoAprovado);
                await repo.Atualizar(os);
                _logger.LogInformation("OS {OsId} avançada para ORCAMENTO_APROVADO após PagamentoConfirmadoEvent.", osId);
            }

            if (publisher != null)
            {
                try
                {
                    var tarefas = os.ItensServico.Count > 0
                        ? os.ItensServico.Select(item => new TarefaInfo(
                            item.IdServico,
                            item.Nome,
                            item.TempoEstimadoMinutos > 0 ? item.TempoEstimadoMinutos : 60)).ToList()
                        : new List<TarefaInfo> { new TarefaInfo(Guid.NewGuid(), "Execução de Serviços da OS", 60) };

                    var eventoLiberada = new OrdemServicoLiberadaParaExecucaoEvent(os.Id, tarefas);

                    await publisher.Publicar(new EventoOutbox(
                        Guid.NewGuid().ToString(),
                        "OrdemServicoLiberadaParaExecucaoEvent",
                        os.Id.ToString(),
                        JsonSerializer.Serialize(eventoLiberada, JsonDefaults.Options)));

                    _logger.LogInformation("OrdemServicoLiberadaParaExecucaoEvent publicado no SNS para OS {OsId} com {TotalTarefas} tarefa(s).",
                        os.Id, tarefas.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao publicar OrdemServicoLiberadaParaExecucaoEvent no SNS para OS {OsId}", os.Id);
                }
            }
        }

        private async Task ProcessarProducaoIniciadaAsync(string json, IOrdemServicoRepository repo, CancellationToken ct)
        {
            var evento = JsonSerializer.Deserialize<ProducaoIniciadaEvent>(json, JsonDefaults.Options);
            Guid osId = evento?.OsId ?? Guid.Empty;
            if (osId == Guid.Empty)
            {
                using var doc = JsonDocument.Parse(json);
                osId = ObterOsId(doc.RootElement);
            }

            if (osId == Guid.Empty)
            {
                _logger.LogWarning("ProducaoIniciadaEvent recebido sem OsId válido: {Json}", json);
                return;
            }

            var os = await repo.ObterPorId(osId);
            if (os == null)
            {
                _logger.LogWarning("OS {OsId} não encontrada para ProducaoIniciadaEvent.", osId);
                return;
            }

            foreach (var item in os.ItensServico)
            {
                if (item.DataHoraInicio == null)
                    item.IniciarServico();
            }

            if (os.Status != StatusOS.EmExecucao &&
                os.Status != StatusOS.Finalizada &&
                os.Status != StatusOS.Entregue &&
                os.Status != StatusOS.Cancelada)
            {
                os.AlterarStatus(StatusOS.EmExecucao);
            }

            await repo.Atualizar(os);
            _logger.LogInformation("OS {OsId} atualizada para EM_EXECUCAO após ProducaoIniciadaEvent.", osId);
        }

        private async Task ProcessarProducaoFinalizadaAsync(string json, IOrdemServicoRepository repo, CancellationToken ct)
        {
            var evento = JsonSerializer.Deserialize<ProducaoFinalizadaEvent>(json, JsonDefaults.Options);
            Guid osId = evento?.OsId ?? Guid.Empty;
            if (osId == Guid.Empty)
            {
                using var doc = JsonDocument.Parse(json);
                osId = ObterOsId(doc.RootElement);
            }

            if (osId == Guid.Empty)
            {
                _logger.LogWarning("ProducaoFinalizadaEvent recebido sem OsId válido: {Json}", json);
                return;
            }

            var os = await repo.ObterPorId(osId);
            if (os == null)
            {
                _logger.LogWarning("OS {OsId} não encontrada para ProducaoFinalizadaEvent.", osId);
                return;
            }

            foreach (var item in os.ItensServico)
            {
                if (item.DataHoraInicio == null)
                    item.IniciarServico();
                if (item.DataHoraFim == null)
                    item.FinalizarServico();
            }

            if (os.Status != StatusOS.Finalizada &&
                os.Status != StatusOS.Entregue &&
                os.Status != StatusOS.Cancelada)
            {
                os.AlterarStatus(StatusOS.Finalizada);
                os.Concluir();
            }

            await repo.Atualizar(os);
            _logger.LogInformation("OS {OsId} atualizada para FINALIZADA após ProducaoFinalizadaEvent.", osId);
        }

        private async Task ProcessarCompensacaoAsync(
            string tipoEvento,
            string json,
            CompensacaoService compensacaoService,
            CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Guid osId = ObterOsId(root);
            if (osId == Guid.Empty)
            {
                _logger.LogWarning("Evento de compensação {TipoEvento} sem OsId válido: {Json}", tipoEvento, json);
                return;
            }

            string? motivo = null;
            if (root.TryGetProperty("motivo", out var motProp) || root.TryGetProperty("Motivo", out motProp))
                motivo = motProp.GetString();

            string? eventId = null;
            if (root.TryGetProperty("eventId", out var evProp) || root.TryGetProperty("EventId", out evProp) ||
                root.TryGetProperty("idEvento", out evProp) || root.TryGetProperty("IdEvento", out evProp))
                eventId = evProp.GetString();

            string idCompensacao = $"sqs-{eventId ?? Guid.NewGuid().ToString()}";

            var comando = new ComandoCompensacao(
                IdCompensacao: idCompensacao,
                Tipo: TipoCompensacao.CancelarOrdemServico,
                IdOrdemServico: osId,
                PecasInsumosIds: [],
                Motivo: motivo ?? $"Compensação solicitada por {tipoEvento}");

            await compensacaoService.Executar(comando, "sqs:os-worker");
            _logger.LogInformation("Compensação {IdCompensacao} ({TipoEvento}) executada para OS {OsId}.", idCompensacao, tipoEvento, osId);
        }

        private static Guid ObterOsId(JsonElement root)
        {
            if (root.TryGetProperty("osId", out var prop) && prop.TryGetGuid(out var guid))
                return guid;
            if (root.TryGetProperty("OsId", out prop) && prop.TryGetGuid(out guid))
                return guid;
            if (root.TryGetProperty("idOrdemServico", out prop) && prop.TryGetGuid(out guid))
                return guid;
            if (root.TryGetProperty("IdOrdemServico", out prop) && prop.TryGetGuid(out guid))
                return guid;
            return Guid.Empty;
        }
    }
}
