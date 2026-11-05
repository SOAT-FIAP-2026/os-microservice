using System.Net;
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
    public class OsSqsBackgroundWorkerStressTests
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

        public OsSqsBackgroundWorkerStressTests()
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

        #region 1. Malformed Messages Stress Tests

        [Fact]
        public async Task Stress_Malformed_NullBody_ExtrairRetornaVazioENaoLanca()
        {
            var msg = new Message { Body = null };

            var (conteudoJson, tipoEvento) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg);

            conteudoJson.Should().BeNull();
            tipoEvento.Should().BeEmpty();

            var worker = CriarWorker();
            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);
            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task Stress_Malformed_EmptyOrWhitespaceBody_NaoLanca()
        {
            var msg1 = new Message { Body = "" };
            var msg2 = new Message { Body = "   \n\t  " };

            var worker = CriarWorker();
            var act1 = () => worker.ProcessarMensagemAsync(msg1, CancellationToken.None);
            var act2 = () => worker.ProcessarMensagemAsync(msg2, CancellationToken.None);

            await act1.Should().NotThrowAsync();
            await act2.Should().NotThrowAsync();
        }

        [Fact]
        public async Task Stress_Malformed_NonJsonBody_NaoLancaEIdentificaTipoVazio()
        {
            var msg = new Message { Body = "<<<MALFORMED NOT A JSON>>>" };

            var (conteudoJson, tipoEvento) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg);
            conteudoJson.Should().Be("<<<MALFORMED NOT A JSON>>>");
            tipoEvento.Should().BeEmpty();

            var worker = CriarWorker();
            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);
            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task Stress_Malformed_NonNotificationJson_NaoLancaEIdentificaTipoVazio()
        {
            var msg = new Message { Body = "{\"foo\": 123, \"bar\": \"text\"}" };

            var (conteudoJson, tipoEvento) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg);
            tipoEvento.Should().BeEmpty();

            var worker = CriarWorker();
            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);
            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task Stress_Malformed_MissingTipoEvento_NaoLancaEIgnora()
        {
            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = "{\"osId\":\"" + Guid.NewGuid() + "\"}"
                })
            };

            var (conteudoJson, tipoEvento) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg);
            tipoEvento.Should().BeEmpty();

            var worker = CriarWorker();
            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);
            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task Stress_Malformed_UnknownEventType_NaoLancaEIgnora()
        {
            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = "{\"osId\":\"" + Guid.NewGuid() + "\"}",
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "CustomUnknownIntegrationEvent" }
                    }
                })
            };

            var (conteudoJson, tipoEvento) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg);
            tipoEvento.Should().Be("CustomUnknownIntegrationEvent");

            var worker = CriarWorker();
            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);
            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task Stress_Malformed_KnownEventTypeWithCorruptJsonMessage_CapturadoNoLoopSemCrasharWorker()
        {
            var corruptMessage = new Message
            {
                MessageId = "msg-corrupt-001",
                ReceiptHandle = "rh-corrupt-001",
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = "CORRUPT JSON CONTENT {{@@",
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "OrcamentoGeradoEvent" }
                    }
                })
            };

            var validOs = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            _repoMock.Setup(r => r.ObterPorId(validOs.Id)).ReturnsAsync(validOs);

            var validMessage = new Message
            {
                MessageId = "msg-valid-002",
                ReceiptHandle = "rh-valid-002",
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(new OrcamentoGeradoEvent
                    {
                        OsId = validOs.Id,
                        OrcamentoId = Guid.NewGuid(),
                        ValorTotal = 500m
                    }),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "OrcamentoGeradoEvent" }
                    }
                })
            };

            using var cts = new CancellationTokenSource();

            int callCount = 0;
            _sqsMock.Setup(s => s.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ReceiveMessageRequest req, CancellationToken ct) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        return new ReceiveMessageResponse
                        {
                            Messages = new List<Message> { corruptMessage, validMessage }
                        };
                    }

                    cts.Cancel();
                    return new ReceiveMessageResponse { Messages = new List<Message>() };
                });

            var worker = CriarWorker();
            await worker.StartAsync(cts.Token);

            // Wait a brief period for execution
            await Task.Delay(300);
            await worker.StopAsync(CancellationToken.None);

            // A mensagem válida foi processada
            validOs.ValorOrcamento.Should().Be(500m);

            // A mensagem válida foi deletada da fila SQS
            _sqsMock.Verify(s => s.DeleteMessageAsync(
                It.IsAny<string>(),
                "rh-valid-002",
                It.IsAny<CancellationToken>()), Times.Once);

            // A mensagem corrompida NÃO foi deletada (para DLQ retry do SQS)
            _sqsMock.Verify(s => s.DeleteMessageAsync(
                It.IsAny<string>(),
                "rh-corrupt-001",
                It.IsAny<CancellationToken>()), Times.Never);
        }

        #endregion

        #region 2. Envelope Unwrapping & Case-Sensitivity Stress Tests

        [Fact]
        public void Stress_Envelope_SnsNotificationComTipoEventoMinusculo_DesembrulhaCorretamente()
        {
            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = "{\"osId\":\"" + Guid.NewGuid() + "\"}",
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "DiagnosticoIniciadoEvent" }
                    }
                })
            };

            var (conteudo, tipo) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg);
            tipo.Should().Be("DiagnosticoIniciadoEvent");
            conteudo.Should().Contain("osId");
        }

        [Fact]
        public void Stress_Envelope_SnsNotificationComTipoEventoMaiusculo_DesembrulhaCorretamente()
        {
            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = "{\"osId\":\"" + Guid.NewGuid() + "\"}",
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["TipoEvento"] = new { Type = "String", Value = "DiagnosticoIniciadoEvent" }
                    }
                })
            };

            var (conteudo, tipo) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg);
            tipo.Should().Be("DiagnosticoIniciadoEvent");
            conteudo.Should().Contain("osId");
        }

        [Fact]
        public void Stress_Envelope_SqsAttributes_CaseInsensitive_DesembrulhaCorretamente()
        {
            var msg1 = new Message
            {
                Body = "{\"osId\":\"" + Guid.NewGuid() + "\"}",
                MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                {
                    ["tipoEvento"] = new() { DataType = "String", StringValue = "ProducaoIniciadaEvent" }
                }
            };

            var (conteudo1, tipo1) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg1);
            tipo1.Should().Be("ProducaoIniciadaEvent");

            var msg2 = new Message
            {
                Body = "{\"osId\":\"" + Guid.NewGuid() + "\"}",
                MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                {
                    ["TipoEvento"] = new() { DataType = "String", StringValue = "ProducaoFinalizadaEvent" }
                }
            };

            var (conteudo2, tipo2) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg2);
            tipo2.Should().Be("ProducaoFinalizadaEvent");
        }

        [Fact]
        public void Stress_Envelope_RawJsonPayload_CaseInsensitive_ExtraiTipoDoPayload()
        {
            var msg1 = new Message { Body = "{\"tipoEvento\":\"OrcamentoGeradoEvent\",\"osId\":\"11111111-1111-1111-1111-111111111111\"}" };
            var msg2 = new Message { Body = "{\"TipoEvento\":\"PagamentoConfirmadoEvent\",\"OsId\":\"22222222-2222-2222-2222-222222222222\"}" };

            var (_, tipo1) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg1);
            tipo1.Should().Be("OrcamentoGeradoEvent");

            var (_, tipo2) = OsSqsBackgroundWorker.ExtrairConteudoETipoEvento(msg2);
            tipo2.Should().Be("PagamentoConfirmadoEvent");
        }

        [Theory]
        [InlineData("osId")]
        [InlineData("OsId")]
        public async Task Stress_Envelope_CaseInsensitive_OsId_ProcessaCorretamente(string nomePropriedade)
        {
            var worker = CriarWorker();
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            var payloadDict = new Dictionary<string, object>
            {
                [nomePropriedade] = os.Id.ToString(),
                ["orcamentoId"] = Guid.NewGuid().ToString(),
                ["valorTotal"] = 750m
            };

            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(payloadDict),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "OrcamentoGeradoEvent" }
                    }
                })
            };

            await worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            os.ValorOrcamento.Should().Be(750m);
            _repoMock.Verify(r => r.Atualizar(os), Times.Once);
        }

        [Theory]
        [InlineData("idOrdemServico")]
        [InlineData("IdOrdemServico")]
        public async Task Stress_Envelope_Compensacao_VariacoesIdOrdemServico_ProcessaCorretamente(string nomePropriedade)
        {
            var worker = CriarWorker();
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            os.AlterarStatus(StatusOS.AguardandoAprovacao);
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            var payloadDict = new Dictionary<string, object>
            {
                [nomePropriedade] = os.Id.ToString(),
                ["motivo"] = "Cancelamento via idOrdemServico",
                ["eventId"] = Guid.NewGuid().ToString()
            };

            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(payloadDict),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "PagamentoCanceladoCompensacaoEvent" }
                    }
                })
            };

            await worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            os.Status.Should().Be(StatusOS.Cancelada);
            _repoMock.Verify(r => r.Atualizar(os), Times.Once);
        }

        [Fact]
        public async Task Stress_Envelope_PayloadComPropriedadesAdicionaisDesconhecidas_IgnoraCamposExtrasComSucesso()
        {
            var worker = CriarWorker();
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cliente@email.com");
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            var payloadComExtras = new
            {
                OsId = os.Id,
                OrcamentoId = Guid.NewGuid(),
                ValorTotal = 420.00m,
                CampoExtra1 = "Desconhecido",
                CampoExtraObjeto = new { Nested = true, Code = 99 },
                CampoExtraLista = new[] { "A", "B" }
            };

            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(payloadComExtras),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "OrcamentoGeradoEvent" }
                    }
                })
            };

            await worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            os.ValorOrcamento.Should().Be(420m);
            _repoMock.Verify(r => r.Atualizar(os), Times.Once);
        }

        [Fact]
        public async Task Stress_Envelope_PayloadComApenasIdOrdemServico_LancaJsonExceptionEExecuteAsyncNaoCrash()
        {
            var msg = new Message
            {
                MessageId = "msg-legacy-001",
                ReceiptHandle = "rh-legacy-001",
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(new
                    {
                        idOrdemServico = Guid.NewGuid().ToString(),
                        orcamentoId = Guid.NewGuid().ToString(),
                        valorTotal = 150.00m
                    }),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "OrcamentoGeradoEvent" }
                    }
                })
            };

            var worker = CriarWorker();

            // ProcessarMensagemAsync diretamente propaga JsonException devido ao modificador 'required OsId'
            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);
            await act.Should().ThrowAsync<JsonException>();

            // Porém no loop do worker (ExecuteAsync), a exceção é capturada, logada, e o worker NÃO crasha
            using var cts = new CancellationTokenSource();
            int calls = 0;
            _sqsMock.Setup(s => s.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ReceiveMessageRequest req, CancellationToken ct) =>
                {
                    calls++;
                    if (calls == 1)
                        return new ReceiveMessageResponse { Messages = new List<Message> { msg } };

                    cts.Cancel();
                    return new ReceiveMessageResponse { Messages = new List<Message>() };
                });

            await worker.StartAsync(cts.Token);
            await Task.Delay(200);
            await worker.StopAsync(CancellationToken.None);

            // A mensagem não é deletada para permitir retry/DLQ pelo SQS
            _sqsMock.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), "rh-legacy-001", It.IsAny<CancellationToken>()), Times.Never);
        }

        #endregion

        #region 3. Missing OS Scenarios Stress Tests

        [Fact]
        public async Task Stress_MissingOS_DiagnosticoIniciadoEvent_NaoLancaENaoAtualiza()
        {
            var worker = CriarWorker();
            var nonExistentId = Guid.NewGuid();
            _repoMock.Setup(r => r.ObterPorId(nonExistentId)).ReturnsAsync((OrdemServicoEntity?)null);

            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(new DiagnosticoIniciadoEvent
                    {
                        OsId = nonExistentId,
                        IdMecanico = Guid.NewGuid(),
                        DataHora = DateTime.UtcNow
                    }),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "DiagnosticoIniciadoEvent" }
                    }
                })
            };

            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            await act.Should().NotThrowAsync();
            _repoMock.Verify(r => r.Atualizar(It.IsAny<OrdemServicoEntity>()), Times.Never);
        }

        [Fact]
        public async Task Stress_MissingOS_DiagnosticoTecnicoConcluidoEvent_NaoLancaENaoAtualiza()
        {
            var worker = CriarWorker();
            var nonExistentId = Guid.NewGuid();
            _repoMock.Setup(r => r.ObterPorId(nonExistentId)).ReturnsAsync((OrdemServicoEntity?)null);

            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(new DiagnosticoTecnicoConcluidoEvent
                    {
                        OsId = nonExistentId,
                        Laudo = "Laudo",
                        Servicos = [],
                        Pecas = []
                    }),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "DiagnosticoTecnicoConcluidoEvent" }
                    }
                })
            };

            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            await act.Should().NotThrowAsync();
            _repoMock.Verify(r => r.Atualizar(It.IsAny<OrdemServicoEntity>()), Times.Never);
        }

        [Fact]
        public async Task Stress_MissingOS_OrcamentoGeradoEvent_NaoLancaENaoAtualiza()
        {
            var worker = CriarWorker();
            var nonExistentId = Guid.NewGuid();
            _repoMock.Setup(r => r.ObterPorId(nonExistentId)).ReturnsAsync((OrdemServicoEntity?)null);

            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(new OrcamentoGeradoEvent
                    {
                        OsId = nonExistentId,
                        OrcamentoId = Guid.NewGuid(),
                        ValorTotal = 100m
                    }),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "OrcamentoGeradoEvent" }
                    }
                })
            };

            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            await act.Should().NotThrowAsync();
            _repoMock.Verify(r => r.Atualizar(It.IsAny<OrdemServicoEntity>()), Times.Never);
        }

        [Fact]
        public async Task Stress_MissingOS_PagamentoConfirmadoEvent_NaoLancaENaoPublicaSns()
        {
            var worker = CriarWorker();
            var nonExistentId = Guid.NewGuid();
            _repoMock.Setup(r => r.ObterPorId(nonExistentId)).ReturnsAsync((OrdemServicoEntity?)null);

            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(new PagamentoConfirmadoEvent
                    {
                        OsId = nonExistentId,
                        PagamentoId = Guid.NewGuid(),
                        Valor = 100m
                    }),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "PagamentoConfirmadoEvent" }
                    }
                })
            };

            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            await act.Should().NotThrowAsync();
            _repoMock.Verify(r => r.Atualizar(It.IsAny<OrdemServicoEntity>()), Times.Never);
            _snsMock.Verify(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Stress_MissingOS_ProducaoIniciadaEvent_NaoLancaENaoAtualiza()
        {
            var worker = CriarWorker();
            var nonExistentId = Guid.NewGuid();
            _repoMock.Setup(r => r.ObterPorId(nonExistentId)).ReturnsAsync((OrdemServicoEntity?)null);

            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(new ProducaoIniciadaEvent
                    {
                        OsId = nonExistentId,
                        DataHoraInicio = DateTime.UtcNow
                    }),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "ProducaoIniciadaEvent" }
                    }
                })
            };

            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            await act.Should().NotThrowAsync();
            _repoMock.Verify(r => r.Atualizar(It.IsAny<OrdemServicoEntity>()), Times.Never);
        }

        [Fact]
        public async Task Stress_MissingOS_ProducaoFinalizadaEvent_NaoLancaENaoAtualiza()
        {
            var worker = CriarWorker();
            var nonExistentId = Guid.NewGuid();
            _repoMock.Setup(r => r.ObterPorId(nonExistentId)).ReturnsAsync((OrdemServicoEntity?)null);

            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(new ProducaoFinalizadaEvent
                    {
                        OsId = nonExistentId,
                        DataHoraFim = DateTime.UtcNow,
                        DuracaoTotalMinutos = 30
                    }),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "ProducaoFinalizadaEvent" }
                    }
                })
            };

            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            await act.Should().NotThrowAsync();
            _repoMock.Verify(r => r.Atualizar(It.IsAny<OrdemServicoEntity>()), Times.Never);
        }

        [Fact]
        public async Task Stress_MissingOS_CompensacaoCancelamento_NaoLancaENaoPublicaCancelamento()
        {
            var worker = CriarWorker();
            var nonExistentId = Guid.NewGuid();
            _repoMock.Setup(r => r.ObterPorId(nonExistentId)).ReturnsAsync((OrdemServicoEntity?)null);

            var msg = new Message
            {
                Body = JsonSerializer.Serialize(new
                {
                    Type = "Notification",
                    Message = JsonSerializer.Serialize(new PagamentoCanceladoCompensacaoEvent
                    {
                        OsId = nonExistentId,
                        Motivo = "Teste OS inexistente"
                    }),
                    MessageAttributes = new Dictionary<string, object>
                    {
                        ["tipoEvento"] = new { Type = "String", Value = "PagamentoCanceladoCompensacaoEvent" }
                    }
                })
            };

            var act = () => worker.ProcessarMensagemAsync(msg, CancellationToken.None);

            await act.Should().NotThrowAsync();
            _repoMock.Verify(r => r.Atualizar(It.IsAny<OrdemServicoEntity>()), Times.Never);
            _snsMock.Verify(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        #endregion

        #region 4. Worker Resilience & Batch Stress Tests

        [Fact]
        public async Task Stress_WorkerResilience_BatchComMensagensMistas_ProcessaTodosSemFalhas()
        {
            var osExistente = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Ok", "ok@email.com");
            _repoMock.Setup(r => r.ObterPorId(osExistente.Id)).ReturnsAsync(osExistente);

            var messages = new List<Message>
            {
                // 1. Mensagem corpo nulo
                new Message { MessageId = "m1", ReceiptHandle = "rh1", Body = null },
                // 2. Mensagem texto não-json
                new Message { MessageId = "m2", ReceiptHandle = "rh2", Body = "NAO_JSON" },
                // 3. Mensagem tipo evento desconhecido
                new Message
                {
                    MessageId = "m3",
                    ReceiptHandle = "rh3",
                    Body = JsonSerializer.Serialize(new
                    {
                        Type = "Notification",
                        Message = "{}",
                        MessageAttributes = new Dictionary<string, object>
                        {
                            ["tipoEvento"] = new { Type = "String", Value = "EventoDesconhecido" }
                        }
                    })
                },
                // 4. Mensagem OS inexistente
                new Message
                {
                    MessageId = "m4",
                    ReceiptHandle = "rh4",
                    Body = JsonSerializer.Serialize(new
                    {
                        Type = "Notification",
                        Message = JsonSerializer.Serialize(new DiagnosticoIniciadoEvent
                        {
                            OsId = Guid.NewGuid(),
                            IdMecanico = Guid.NewGuid(),
                            DataHora = DateTime.UtcNow
                        }),
                        MessageAttributes = new Dictionary<string, object>
                        {
                            ["tipoEvento"] = new { Type = "String", Value = "DiagnosticoIniciadoEvent" }
                        }
                    })
                },
                // 5. Mensagem válida para OS existente
                new Message
                {
                    MessageId = "m5",
                    ReceiptHandle = "rh5",
                    Body = JsonSerializer.Serialize(new
                    {
                        Type = "Notification",
                        Message = JsonSerializer.Serialize(new OrcamentoGeradoEvent
                        {
                            OsId = osExistente.Id,
                            OrcamentoId = Guid.NewGuid(),
                            ValorTotal = 999m
                        }),
                        MessageAttributes = new Dictionary<string, object>
                        {
                            ["tipoEvento"] = new { Type = "String", Value = "OrcamentoGeradoEvent" }
                        }
                    })
                }
            };

            using var cts = new CancellationTokenSource();
            int calls = 0;
            _sqsMock.Setup(s => s.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ReceiveMessageRequest req, CancellationToken ct) =>
                {
                    calls++;
                    if (calls == 1)
                        return new ReceiveMessageResponse { Messages = messages };

                    cts.Cancel();
                    return new ReceiveMessageResponse { Messages = new List<Message>() };
                });

            var worker = CriarWorker();
            await worker.StartAsync(cts.Token);

            await Task.Delay(300);
            await worker.StopAsync(CancellationToken.None);

            // OS foi atualizada com sucesso
            osExistente.ValorOrcamento.Should().Be(999m);

            // Todas as 5 mensagens foram deletadas com sucesso (sem crash do worker)
            _sqsMock.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), "rh1", It.IsAny<CancellationToken>()), Times.Once);
            _sqsMock.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), "rh2", It.IsAny<CancellationToken>()), Times.Once);
            _sqsMock.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), "rh3", It.IsAny<CancellationToken>()), Times.Once);
            _sqsMock.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), "rh4", It.IsAny<CancellationToken>()), Times.Once);
            _sqsMock.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), "rh5", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Stress_WorkerResilience_FalhaDeRedeSQS_NaoDerrubaWorkerERecupera()
        {
            var os = new OrdemServicoEntity(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "teste@email.com");
            _repoMock.Setup(r => r.ObterPorId(os.Id)).ReturnsAsync(os);

            using var cts = new CancellationTokenSource();
            int attempts = 0;

            _sqsMock.Setup(s => s.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
                .Returns((ReceiveMessageRequest req, CancellationToken ct) =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        // Primeira tentativa falha com exceção de rede
                        throw new AmazonSQSException("Falha de conexão temporária simulada");
                    }

                    // Segunda tentativa obtém mensagem
                    cts.Cancel();
                    return Task.FromResult(new ReceiveMessageResponse
                    {
                        Messages = new List<Message>
                        {
                            new Message
                            {
                                MessageId = "rec-msg-1",
                                ReceiptHandle = "rec-rh-1",
                                Body = JsonSerializer.Serialize(new
                                {
                                    Type = "Notification",
                                    Message = JsonSerializer.Serialize(new DiagnosticoIniciadoEvent
                                    {
                                        OsId = os.Id,
                                        IdMecanico = Guid.NewGuid(),
                                        DataHora = DateTime.UtcNow
                                    }),
                                    MessageAttributes = new Dictionary<string, object>
                                    {
                                        ["tipoEvento"] = new { Type = "String", Value = "DiagnosticoIniciadoEvent" }
                                    }
                                })
                            }
                        }
                    });
                });

            var worker = CriarWorker();
            await worker.StartAsync(cts.Token);

            // Aguarda o retry (o código tem Task.Delay(3000) no catch de polling, então vamos esperar)
            await Task.Delay(3500);
            await worker.StopAsync(CancellationToken.None);

            attempts.Should().BeGreaterThanOrEqualTo(2);
            os.Status.Should().Be(StatusOS.EmDiagnostico);
        }

        #endregion
    }
}
