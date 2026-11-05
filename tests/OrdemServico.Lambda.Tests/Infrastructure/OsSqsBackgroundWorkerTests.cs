using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Fiap.TechChallenge.Contracts.Common;
using Fiap.TechChallenge.Contracts.Events;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Configuration;
using OrdemServico.Lambda.Domain.Entities;
using OrdemServico.Lambda.Infrastructure.Messaging;
using OrdemServicoEntity = OrdemServico.Lambda.Domain.Entities.OrdemServico;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace OrdemServico.Lambda.Tests.Infrastructure
{
    public class OsSqsBackgroundWorkerTests
    {
        private readonly Mock<IAmazonSQS> _sqsMock = new();
        private readonly Mock<IOrdemServicoRepository> _repoMock = new();
        private readonly Mock<IAmazonSimpleNotificationService> _snsMock = new();
        private readonly Mock<IEstoqueGateway> _estoqueMock = new();
        private readonly Mock<ISagaRepository> _sagaRepoMock = new();
        private readonly Mock<IRegistroCompensacaoRepository> _registroRepoMock = new();
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SnsPublisher _publisher;
        private readonly CompensacaoService _compensacaoService;
        private readonly IConfiguration _configuration;

        public OsSqsBackgroundWorkerTests()
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                ["OS_EVENTOS_QUEUE_URL"] = "http://floci:4566/000000000000/techchallenge-os-eventos-local",
                ["EVENTOS_TOPIC_ARN"] = "arn:aws:sns:sa-east-1:000000000000:techchallenge-eventos-local",
                ["EVENTO_ORIGEM"] = "techchallenge.ordem-servico"
            };

            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var lambdaSettings = LambdaSettings.FromConfiguration(_configuration);
            _publisher = new SnsPublisher(_snsMock.Object, lambdaSettings);

            _compensacaoService = new CompensacaoService(
                _repoMock.Object,
                _estoqueMock.Object,
                _sagaRepoMock.Object,
                _registroRepoMock.Object,
                NullLogger<CompensacaoService>.Instance,
                _publisher);

            var services = new ServiceCollection();
            services.AddSingleton(_repoMock.Object);
            services.AddSingleton(_publisher);
            services.AddSingleton(_compensacaoService);

            var serviceProvider = services.BuildServiceProvider();
            _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        }

        private OsSqsBackgroundWorker CriarWorker()
        {
            return new OsSqsBackgroundWorker(
                _sqsMock.Object,
                _configuration,
                _scopeFactory,
                NullLogger<OsSqsBackgroundWorker>.Instance);
        }

        private static Message CriarMensagemSqs(string tipoEvento, object payloadObj)
        {
            string payloadJson = JsonSerializer.Serialize(payloadObj);

            // Envelope SNS
            var envelope = new
            {
                Type = "Notification",
                MessageId = Guid.NewGuid().ToString(),
                Message = payloadJson,
                MessageAttributes = new Dictionary<string, object>
                {
                    ["tipoEvento"] = new { Type = "String", Value = tipoEvento },
                    ["origem"] = new { Type = "String", Value = "techchallenge.test" }
                }
            };

            return new Message
            {
                MessageId = Guid.NewGuid().ToString(),
                ReceiptHandle = "receipt-" + Guid.NewGuid().ToString(),
                Body = JsonSerializer.Serialize(envelope),
                MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                {
                    [SnsMessageAttributes.TipoEvento] = new() { DataType = "String", StringValue = tipoEvento }
                }
            };
        }

        [Fact]
        public void ExtrairConteudoETipoEvento_ComEnvelopeSns_DeveDesembrulharCorretamente()
        {
            var msg = CriarMensagemSqs("DiagnosticoIniciadoEvent", new { OsId = Guid.NewGuid() });

            var (conteudoJson, tipoEvento) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg);

            tipoEvento.Should().Be("DiagnosticoIniciadoEvent");
            conteudoJson.Should().Contain("OsId");
        }

        [Fact]
        public void ExtrairConteudoETipoEvento_ComRawJson_DeveExtrairTipoDoPayload()
        {
            var msg = new Message
            {
                Body = "{\"tipoEvento\":\"ProducaoIniciadaEvent\",\"osId\":\"11111111-1111-1111-1111-111111111111\"}"
            };

            var (conteudoJson, tipoEvento) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg);

            tipoEvento.Should().Be("ProducaoIniciadaEvent");
            conteudoJson.Should().Be(msg.Body);
        }

        [Fact]
        public async Task ProcessarMensagem_DiagnosticoIniciadoEvent_DeveAvancarStatusParaEmDiagnostico()
        {
            var worker = CriarWorker();
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            var msg = CriarMensagemSqs("DiagnosticoIniciadoEvent", new DiagnosticoIniciadoEvent
            {
                OsId = os.Id,
                IdMecanico = Guid.NewGuid(),
                NomeMecanico = "Mecanico A",
                DataHora = DateTime.UtcNow
            });

            await worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            os.Status.Should().Be(StatusOS.EmDiagnostico);
            os.DataInicioDiagnostico.Should().NotBeNull();
            _repoMock.Verify(r => r.Atualizar(os), Times.Once);
        }

        [Fact]
        public async Task ProcessarMensagem_DiagnosticoTecnicoConcluidoEvent_DeveAvancarParaAguardandoAprovacaoESincronizarItens()
        {
            var worker = CriarWorker();
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            Guid servicoId = Guid.NewGuid();
            Guid pecaId = Guid.NewGuid();

            var evento = new DiagnosticoTecnicoConcluidoEvent
            {
                OsId = os.Id,
                Laudo = "Laudo detalhado",
                Servicos = [new ServicoSugeridoInfo(servicoId, "Alinhamento")],
                Pecas = [new PecaSugeridaInfo(pecaId, 1, "Pneu")]
            };

            var msg = CriarMensagemSqs("DiagnosticoTecnicoConcluidoEvent", evento);

            await worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            os.Status.Should().Be(StatusOS.AguardandoAprovacao);
            os.ItensServico.Should().ContainSingle(s => s.IdServico == servicoId);
            os.ItensPecaInsumo.Should().ContainSingle(p => p.IdPecaInsumo == pecaId);
            _repoMock.Verify(r => r.Atualizar(os), Times.Once);
        }

        [Fact]
        public async Task ProcessarMensagem_OrcamentoGeradoEvent_DeveAtualizarValorEStatus()
        {
            var worker = CriarWorker();
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            var evento = new OrcamentoGeradoEvent
            {
                OsId = os.Id,
                OrcamentoId = Guid.NewGuid(),
                ValorTotal = 350.50m
            };

            var msg = CriarMensagemSqs("OrcamentoGeradoEvent", evento);

            await worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            os.ValorOrcamento.Should().Be(350.50m);
            os.Status.Should().Be(StatusOS.AguardandoAprovacao);
            _repoMock.Verify(r => r.Atualizar(os), Times.Once);
        }

        [Fact]
        public async Task ProcessarMensagem_PagamentoConfirmadoEvent_DeveAvancarParaOrcamentoAprovadoEPublicarLiberadaParaExecucao()
        {
            var worker = CriarWorker();
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            os.SincronizarItens([new ItemServico(Guid.NewGuid(), "Revisão", 200m, 90)], []);
            os.AlterarStatus(StatusOS.AguardandoAprovacao);
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            _snsMock.Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            var evento = new PagamentoConfirmadoEvent
            {
                OsId = os.Id,
                PagamentoId = Guid.NewGuid(),
                Valor = 200m
            };

            var msg = CriarMensagemSqs("PagamentoConfirmadoEvent", evento);

            await worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            os.Status.Should().Be(StatusOS.OrcamentoAprovado);
            _repoMock.Verify(r => r.Atualizar(os), Times.Once);

            _snsMock.Verify(s => s.PublishAsync(It.Is<PublishRequest>(req =>
                req.MessageAttributes["tipoEvento"].StringValue == "OrdemServicoLiberadaParaExecucaoEvent" &&
                req.MessageAttributes["idOrdemServico"].StringValue == os.Id.ToString() &&
                req.Message.Contains(os.Id.ToString())), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ProcessarMensagem_ProducaoIniciadaEvent_DeveAvancarParaEmExecucaoEIniciarItens()
        {
            var worker = CriarWorker();
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            var item = new ItemServico(Guid.NewGuid(), "Troca de Pastilha", 80m, 40);
            os.SincronizarItens([item], []);
            os.AlterarStatus(StatusOS.OrcamentoAprovado);
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            var evento = new ProducaoIniciadaEvent
            {
                OsId = os.Id,
                DataHoraInicio = DateTime.UtcNow
            };

            var msg = CriarMensagemSqs("ProducaoIniciadaEvent", evento);

            await worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            os.Status.Should().Be(StatusOS.EmExecucao);
            os.ItensServico.First().DataHoraInicio.Should().NotBeNull();
            _repoMock.Verify(r => r.Atualizar(os), Times.Once);
        }

        [Fact]
        public async Task ProcessarMensagem_ProducaoFinalizadaEvent_DeveAvancarParaFinalizadaEConcluirItens()
        {
            var worker = CriarWorker();
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            var item = new ItemServico(Guid.NewGuid(), "Troca de Pastilha", 80m, 40);
            os.SincronizarItens([item], []);
            os.AlterarStatus(StatusOS.EmExecucao);
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            var evento = new ProducaoFinalizadaEvent
            {
                OsId = os.Id,
                DataHoraFim = DateTime.UtcNow,
                DuracaoTotalMinutos = 40
            };

            var msg = CriarMensagemSqs("ProducaoFinalizadaEvent", evento);

            await worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            os.Status.Should().Be(StatusOS.Finalizada);
            os.DataConclusao.Should().NotBeNull();
            os.ItensServico.First().DataHoraFim.Should().NotBeNull();
            _repoMock.Verify(r => r.Atualizar(os), Times.Once);
        }

        [Fact]
        public async Task ProcessarMensagem_PagamentoCanceladoCompensacaoEvent_DeveExecutarCompensacaoECancelarOS()
        {
            var worker = CriarWorker();
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            Guid idPeca = Guid.NewGuid();
            os.SincronizarItens([], [new ItemPecaInsumo(idPeca, "Amortecedor", 300m)]);
            os.AlterarStatus(StatusOS.AguardandoAprovacao);
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            var evento = new PagamentoCanceladoCompensacaoEvent
            {
                OsId = os.Id,
                Motivo = "Pagamento rejeitado pelo gateway"
            };

            var msg = CriarMensagemSqs("PagamentoCanceladoCompensacaoEvent", evento);

            await worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            os.Status.Should().Be(StatusOS.Cancelada);
            _estoqueMock.Verify(e => e.EstornarEstoque(idPeca, 1), Times.Once);
            _repoMock.Verify(r => r.Atualizar(os), Times.Once);
        }
    }
}
