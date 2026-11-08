using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using Amazon.Lambda.SNSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
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
using OrdemServico.Lambda.Domain.Exceptions;
using OrdemServico.Lambda.Functions;
using OrdemServico.Lambda.Infrastructure.Email;
using OrdemServico.Lambda.Infrastructure.Http;
using OrdemServico.Lambda.Infrastructure.Messaging;
using OrdemServico.Lambda.Infrastructure.Persistence;
using OrdemServico.Lambda.Infrastructure.Security;
using Xunit;

namespace OrdemServico.Lambda.Tests.Infrastructure;

public class InfrastructurePersistenceAndGatewaysTests
{
    private static readonly LambdaSettings DefaultSettings = new()
    {
        TableName = "test-os-table",
        CompensacaoQueueUrl = "http://localhost:4566/000000000000/comp-queue",
        EventosTopicArn = "arn:aws:sns:sa-east-1:000000000000:eventos",
        EventoOrigem = "techchallenge.ordem-servico",
        EmailRemetente = "noreply@techchallenge.com",
        EmailDestinoOverride = "override@techchallenge.com"
    };

    [Fact]
    public async Task OrdemServicoRepository_Adicionar_Atualizar_ObterPorId_E_ObterAtivas_DevemFuncionarCorretamente()
    {
        var dynamoMock = new Mock<IAmazonDynamoDB>();
        Dictionary<string, AttributeValue>? savedItem = null;

        dynamoMock
            .Setup(d => d.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((req, _) =>
            {
                savedItem = req.TransactItems[0].Put.Item;
            })
            .ReturnsAsync(new TransactWriteItemsResponse());

        var repo = new OrdemServicoRepository(dynamoMock.Object, DefaultSettings);
        var os = new OrdemServico.Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cli@teste.com", "Troca de oleo");

        await repo.Adicionar(os);
        os.Versao.Should().Be(1);
        os.Eventos.Should().BeEmpty();
        savedItem.Should().NotBeNull();

        os.IniciarDiagnostico();
        await repo.Atualizar(os);
        os.Versao.Should().Be(2);

        dynamoMock
            .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { IsItemSet = true, Item = savedItem! });

        var recuperada = await repo.ObterPorId(os.Id);
        recuperada.Should().NotBeNull();
        recuperada!.Id.Should().Be(os.Id);

        dynamoMock
            .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { IsItemSet = false });

        var naoEncontrada = await repo.ObterPorId(Guid.NewGuid());
        naoEncontrada.Should().BeNull();

        dynamoMock
            .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse
            {
                Items = [savedItem!],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        var ativas = await repo.ObterAtivas();
        ativas.Should().HaveCount(1);
    }

    [Fact]
    public async Task OrdemServicoRepository_QuandoConflitoDeVersao_DeveLancarConcorrenciaOrdemServicoException()
    {
        var dynamoMock = new Mock<IAmazonDynamoDB>();
        var cancelEx = new TransactionCanceledException("Conflito")
        {
            CancellationReasons = [new CancellationReason { Code = "ConditionalCheckFailed" }]
        };

        dynamoMock
            .Setup(d => d.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(cancelEx);

        var repo = new OrdemServicoRepository(dynamoMock.Object, DefaultSettings);
        var os = new OrdemServico.Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "Cliente Teste", "cli@teste.com", "Conflito");

        Func<Task> act = () => repo.Atualizar(os);
        await act.Should().ThrowAsync<ConcorrenciaOrdemServicoException>();
    }

    [Fact]
    public async Task SagaRepository_E_RegistroCompensacaoRepository_DevemSalvarEObterCorretamente()
    {
        var dynamoMock = new Mock<IAmazonDynamoDB>();
        Dictionary<string, AttributeValue>? sagaItem = null;
        Dictionary<string, AttributeValue>? compItem = null;

        dynamoMock
            .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutItemRequest, CancellationToken>((req, _) =>
            {
                if (req.Item["tipoEntidade"].S == "SAGA")
                    sagaItem = req.Item;
                else
                    compItem = req.Item;
            })
            .ReturnsAsync(new PutItemResponse());

        var sagaRepo = new SagaRepository(dynamoMock.Object, DefaultSettings);
        var saga = SagaReservaEstoque.Iniciar(Guid.NewGuid(), [Guid.NewGuid()]);
        saga.RegistrarReserva(Guid.NewGuid());
        saga.Concluir();

        await sagaRepo.Salvar(saga);
        sagaItem.Should().NotBeNull();

        dynamoMock
            .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetItemResponse { IsItemSet = true, Item = sagaItem! });

        var sagaObtida = await sagaRepo.Obter(saga.IdOrdemServico, saga.IdSaga);
        sagaObtida.Should().NotBeNull();
        sagaObtida!.IdSaga.Should().Be(saga.IdSaga);

        var compRepo = new RegistroCompensacaoRepository(dynamoMock.Object, DefaultSettings);
        var reg = new RegistroCompensacao
        {
            IdCompensacao = "comp-123",
            IdOrdemServico = saga.IdOrdemServico,
            Tipo = TipoCompensacao.EstornarEstoque,
            Origem = "sqs"
        };
        await compRepo.Salvar(reg);
        compItem.Should().NotBeNull();

        dynamoMock
            .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetItemResponse { IsItemSet = true, Item = compItem! });

        var compObtida = await compRepo.Obter("comp-123");
        compObtida.Should().NotBeNull();
        compObtida!.IdCompensacao.Should().Be("comp-123");

        dynamoMock
            .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { IsItemSet = false });

        (await sagaRepo.Obter(Guid.NewGuid(), Guid.NewGuid())).Should().BeNull();
        (await compRepo.Obter("inexistente")).Should().BeNull();
    }

    [Fact]
    public async Task CoreApiClient_DeveConsultarCatalogo_MovimentarEstoque_E_TratarErrosHttp()
    {
        var authMock = new Mock<ICoreApiAuthorization>();
        authMock.Setup(a => a.ObterAuthorizationHeader()).Returns("Bearer fake-jwt-token");
        authMock.Setup(a => a.ObterCorrelationId()).Returns("corr-abc-123");

        var idCliente = Guid.NewGuid();
        var idVeiculo = Guid.NewGuid();
        var idServico = Guid.NewGuid();
        var idPeca = Guid.NewGuid();

        var handler = new FakeHttpMessageHandler(req =>
        {
            req.Headers.Contains(CoreApiAuthorization.CorrelationHeader).Should().BeTrue();
            var path = req.RequestUri!.AbsolutePath;

            if (path.Contains($"/clientes/{idCliente}"))
                return JsonResponse(HttpStatusCode.OK, new { id = idCliente, nome = "Maria", email = "maria@teste.com", celular = "11999999999" });

            if (path.Contains("/clientes/"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (path.Contains($"/veiculos/{idVeiculo}"))
                return JsonResponse(HttpStatusCode.OK, new { id = idVeiculo, placa = "ABC1D23", modelo = "Civic", ano = 2024 });

            if (path.Contains("/veiculos/"))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            if (path.Contains($"/servicos/{idServico}"))
                return JsonResponse(HttpStatusCode.OK, new { id = idServico, nome = "Alinhamento", valorUnitario = 120m, tempoEstimadoMinutos = 45 });

            if (path.Contains($"/pecas-insumos/{idPeca}"))
                return JsonResponse(HttpStatusCode.OK, new { id = idPeca, descricao = "Filtro de Oleo", valorUnitario = 50m });

            if (path.Contains("remover-quantidade"))
                return JsonResponse(HttpStatusCode.BadRequest, new { statusCode = 400, message = "Saldo insuficiente" });

            if (path.Contains("adicionar-quantidade"))
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://core-service:8080") };
        var client = new CoreApiClient(httpClient, authMock.Object);

        var cliente = await client.ObterCliente(idCliente);
        cliente.Should().NotBeNull();
        cliente!.Nome.Should().Be("Maria");

        (await client.ObterCliente(Guid.NewGuid())).Should().BeNull();

        (await client.VeiculoExiste(idVeiculo)).Should().BeTrue();
        var veiculo = await client.ObterVeiculo(idVeiculo);
        veiculo!.Placa.Should().Be("ABC1D23");

        Func<Task> unauthAct = () => client.ObterVeiculo(Guid.NewGuid());
        await unauthAct.Should().ThrowAsync<UnauthorizedAccessException>();

        var servicos = await client.ObterServicos([idServico]);
        servicos.Should().ContainSingle(s => s.Nome == "Alinhamento");

        var pecas = await client.ObterPecasInsumo([idPeca]);
        pecas.Should().ContainSingle(p => p.Descricao == "Filtro de Oleo");

        await client.EstornarEstoque(idPeca, 1);

        Func<Task> estInsufAct = () => client.BaixarEstoque(idPeca, 5);
        await estInsufAct.Should().ThrowAsync<IntegracaoNegocioException>()
            .WithMessage("*Saldo insuficiente*");
    }

    [Fact]
    public async Task SqsFilaCompensacao_ServiceTokenFactory_E_ResendEmailService_DevemFuncionar()
    {
        var sqsMock = new Mock<IAmazonSQS>();
        SendMessageRequest? capturedReq = null;
        sqsMock
            .Setup(s => s.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendMessageRequest, CancellationToken>((r, _) => capturedReq = r)
            .ReturnsAsync(new SendMessageResponse());

        var fila = new SqsFilaCompensacao(sqsMock.Object, DefaultSettings);
        var cmd = new ComandoCompensacao("comp-1", TipoCompensacao.EstornarEstoque, Guid.NewGuid(), [Guid.NewGuid()], "Falha");
        await fila.Enviar(cmd);

        capturedReq.Should().NotBeNull();
        capturedReq!.MessageAttributes.Should().ContainKey(Fiap.TechChallenge.Contracts.Common.SnsMessageAttributes.CorrelationId);

        var configSsm = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JWT_SECRET_SSM_PARAMETER"] = "/tc/jwt-secret",
                ["JWT_ISSUER"] = "TechChallenge",
                ["JWT_AUDIENCE"] = "techchallenge.com.br"
            })
            .Build();
        var ssmMock = new Mock<IAmazonSimpleSystemsManagement>();
        ssmMock
            .Setup(s => s.GetParameterAsync(It.IsAny<GetParameterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetParameterResponse
            {
                Parameter = new Parameter { Value = "super-secret-jwt-key-with-at-least-32-bytes!" }
            });

        var jwtSettings = JwtSettings.Resolve(configSsm, ssmMock.Object);
        var tokenFactory = new ServiceTokenFactory(jwtSettings);
        string token = tokenFactory.Gerar();
        token.Should().NotBeNullOrWhiteSpace();

        // Resend sem API key retorna false
        var emailSemKey = new ResendEmailService(new HttpClient(), DefaultSettings, null, NullLogger<ResendEmailService>.Instance);
        (await emailSemKey.EnviarEmailAsync("cli@teste.com", "Assunto", "<p>Oi</p>")).Should().BeFalse();

        // Resend com API key e 200 OK retorna true
        var emailOkHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var emailComKey = new ResendEmailService(new HttpClient(emailOkHandler), DefaultSettings, "re_123", NullLogger<ResendEmailService>.Instance);
        (await emailComKey.EnviarEmailAsync("cli@teste.com", "Assunto", "<p>Oi</p>")).Should().BeTrue();
    }

    [Fact]
    public async Task OutboxPublisherFunction_Handle_DevePublicarNoSns_E_ReportarBatchFailureEmErro()
    {
        var snsMock = new Mock<IAmazonSimpleNotificationService>();
        snsMock
            .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse { MessageId = "msg-1" });

        var services = new ServiceCollection();
        services.AddSingleton<ILogger<OutboxPublisherFunction>>(NullLogger<OutboxPublisherFunction>.Instance);
        services.AddSingleton(new SnsPublisher(snsMock.Object, DefaultSettings));
        var sp = services.BuildServiceProvider();

        var fn = new OutboxPublisherFunction(sp);
        var osId = Guid.NewGuid().ToString();

        var ddbEvent = new DynamoDBEvent
        {
            Records =
            [
                new DynamoDBEvent.DynamodbStreamRecord
                {
                    EventName = "MODIFY",
                    Dynamodb = new DynamoDBEvent.StreamRecord()
                },
                new DynamoDBEvent.DynamodbStreamRecord
                {
                    EventName = "INSERT",
                    Dynamodb = new DynamoDBEvent.StreamRecord
                    {
                        SequenceNumber = "seq-100",
                        NewImage = new Dictionary<string, DynamoDBEvent.AttributeValue>
                        {
                            ["PK"] = new() { S = "OUTBOX#evt-1" },
                            ["tipoEvento"] = new() { S = "OrdemServicoAbertaEvent" },
                            ["idOrdemServico"] = new() { S = osId },
                            ["correlationId"] = new() { S = "corr-outbox-999" },
                            ["payload"] = new() { S = "{\"osId\":\"" + osId + "\"}" }
                        }
                    }
                }
            ]
        };

        var resp = await fn.Handle(ddbEvent, Mock.Of<ILambdaContext>());
        resp.BatchItemFailures.Should().BeEmpty();

        // Simula falha no SNS -> deve retornar SequenceNumber no BatchItemFailures
        snsMock
            .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SNS indisponivel"));

        var respFalha = await fn.Handle(ddbEvent, Mock.Of<ILambdaContext>());
        respFalha.BatchItemFailures.Should().ContainSingle(f => f.ItemIdentifier == "seq-100");
    }

    [Fact]
    public async Task CompensacaoEventoFunction_Handle_DeveProcessarEventoValido_E_IgnorarDesconhecido()
    {
        var compRepoMock = new Mock<IRegistroCompensacaoRepository>();
        compRepoMock.Setup(r => r.Obter(It.IsAny<string>())).ReturnsAsync((RegistroCompensacao?)null);

        var osRepoMock = new Mock<IOrdemServicoRepository>();
        var estoqueMock = new Mock<IEstoqueGateway>();
        var sagaRepoMock = new Mock<ISagaRepository>();

        var services = new ServiceCollection();
        services.AddSingleton<ILogger<CompensacaoEventoFunction>>(NullLogger<CompensacaoEventoFunction>.Instance);
        services.AddSingleton<ILogger<CompensacaoService>>(NullLogger<CompensacaoService>.Instance);
        services.AddScoped(_ => new CompensacaoService(
            osRepoMock.Object,
            estoqueMock.Object,
            sagaRepoMock.Object,
            compRepoMock.Object,
            NullLogger<CompensacaoService>.Instance));

        var fn = new CompensacaoEventoFunction(services.BuildServiceProvider());
        var osId = Guid.NewGuid();
        var pecaId = Guid.NewGuid();

        var snsEvent = new SNSEvent
        {
            Records =
            [
                new SNSEvent.SNSRecord
                {
                    Sns = new SNSEvent.SNSMessage
                    {
                        MessageId = "msg-ignorado",
                        Message = "{}",
                        MessageAttributes = new Dictionary<string, SNSEvent.MessageAttribute>
                        {
                            ["tipoEvento"] = new() { Value = "EventoDesconhecido" }
                        }
                    }
                },
                new SNSEvent.SNSRecord
                {
                    Sns = new SNSEvent.SNSMessage
                    {
                        MessageId = "msg-orc-reprovado",
                        TopicArn = "arn:aws:sns:sa-east-1:000000000000:eventos",
                        Message = JsonSerializer.Serialize(new
                        {
                            idEvento = "evt-comp-1",
                            idOrdemServico = osId,
                            pecasInsumosIds = new[] { pecaId }
                        }),
                        MessageAttributes = new Dictionary<string, SNSEvent.MessageAttribute>
                        {
                            ["tipoEvento"] = new() { Value = "OrcamentoReprovado" },
                            ["X-Correlation-ID"] = new() { Value = "corr-sns-1" }
                        }
                    }
                }
            ]
        };

        await fn.Handle(snsEvent, Mock.Of<ILambdaContext>());

        estoqueMock.Verify(e => e.EstornarEstoque(pecaId, 1), Times.Once);
        compRepoMock.Verify(r => r.Salvar(It.Is<RegistroCompensacao>(c => c.IdCompensacao == "evt-evt-comp-1")), Times.Exactly(2));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, object body) =>
        new(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
