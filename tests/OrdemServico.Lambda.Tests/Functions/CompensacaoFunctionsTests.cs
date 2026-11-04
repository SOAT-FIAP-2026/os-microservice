using System.Text.Json;
using Amazon.Lambda.SNSEvents;
using Amazon.Lambda.SQSEvents;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Functions;

namespace OrdemServico.Lambda.Tests.Functions
{
    public class CompensacaoFunctionsTests
    {
        private static SNSEvent.SNSMessage Evento(string tipoEvento, string origem, object detail) => new()
        {
            MessageId = "abc-123",
            Message = JsonSerializer.Serialize(detail),
            MessageAttributes = new Dictionary<string, SNSEvent.MessageAttribute>
            {
                ["tipoEvento"] = new() { Type = "String", Value = tipoEvento },
                ["origem"] = new() { Type = "String", Value = origem }
            }
        };

        [Fact]
        public void Traduzir_OrcamentoReprovado_DeveGerarEstornoDeEstoque()
        {
            Guid idOs = Guid.NewGuid(), peca = Guid.NewGuid();

            ComandoCompensacao? comando = CompensacaoEventoFunction.Traduzir(
                Evento("OrcamentoReprovado", "techchallenge.ordem-servico", new { idOrdemServico = idOs, pecasInsumosIds = new[] { peca } }));

            comando.Should().BeEquivalentTo(new
            {
                IdCompensacao = "evt-abc-123",
                Tipo = TipoCompensacao.EstornarEstoque,
                IdOrdemServico = idOs,
                PecasInsumosIds = new[] { peca }
            });
        }

        [Fact]
        public void Traduzir_EventoComIdEvento_DeveUsarIdEventoParaIdempotencia()
        {
            Guid idEvento = Guid.NewGuid();

            ComandoCompensacao? comando = CompensacaoEventoFunction.Traduzir(
                Evento("OrcamentoReprovado", "techchallenge.ordem-servico", new { idEvento, idOrdemServico = Guid.NewGuid() }));

            comando!.IdCompensacao.Should().Be($"evt-{idEvento}");
        }

        [Fact]
        public void Traduzir_EventoComEventId_DeveUsarEventIdParaIdempotencia()
        {
            Guid eventId = Guid.NewGuid();

            ComandoCompensacao? comando = CompensacaoEventoFunction.Traduzir(
                Evento("OrcamentoReprovado", "techchallenge.ordem-servico", new { eventId, osId = Guid.NewGuid() }));

            comando!.IdCompensacao.Should().Be($"evt-{eventId}");
        }

        [Fact]
        public void Traduzir_EventoComOsId_DeveLerIdentificadorCorretamente()
        {
            Guid osId = Guid.NewGuid();
            Guid peca = Guid.NewGuid();

            ComandoCompensacao? comando = CompensacaoEventoFunction.Traduzir(
                Evento("OrcamentoReprovado", "techchallenge.ordem-servico", new { osId, pecasInsumosIds = new[] { peca } }));

            comando!.IdOrdemServico.Should().Be(osId);
        }

        [Fact]
        public void Traduzir_ReservaEstoqueFalhou_DeveRemoverPecaDaOrdem()
        {
            Guid idOs = Guid.NewGuid(), peca = Guid.NewGuid();

            ComandoCompensacao? comando = CompensacaoEventoFunction.Traduzir(
                Evento("ReservaEstoqueFalhou", "techchallenge.estoque", new { idOrdemServico = idOs, idPecaInsumo = peca, motivo = "sem saldo" }));

            comando!.Tipo.Should().Be(TipoCompensacao.RemoverPecaInsumo);
            comando.PecasInsumosIds.Should().Equal(peca);
            comando.Motivo.Should().Be("sem saldo");
        }

        [Fact]
        public void Traduzir_PagamentoRecusado_DeveCancelarOrdem()
        {
            ComandoCompensacao? comando = CompensacaoEventoFunction.Traduzir(
                Evento("PagamentoRecusado", "techchallenge.pagamento", new { idOrdemServico = Guid.NewGuid() }));

            comando!.Tipo.Should().Be(TipoCompensacao.CancelarOrdemServico);
        }

        [Fact]
        public void Traduzir_EventoDesconhecido_DeveRetornarNull()
        {
            CompensacaoEventoFunction.Traduzir(Evento("Outro", "x", new { })).Should().BeNull();
        }

        [Theory]
        [InlineData("{\"tipo\":\"EstornarEstoque\",\"idOrdemServico\":\"11111111-1111-1111-1111-111111111111\",\"pecasInsumosIds\":[\"22222222-2222-2222-2222-222222222222\"]}")]
        [InlineData("{\"idCompensacao\":\"x\",\"idOrdemServico\":\"11111111-1111-1111-1111-111111111111\",\"pecasInsumosIds\":[\"22222222-2222-2222-2222-222222222222\"]}")]
        [InlineData("{\"idCompensacao\":\"x\",\"tipo\":\"EstornarEstoque\",\"idOrdemServico\":\"11111111-1111-1111-1111-111111111111\"}")]
        public async Task FilaFunction_ComandoIncompleto_DeveFalharSemExecutar(string corpo)
        {
            var estoque = new Mock<IEstoqueGateway>();
            var registros = new Mock<IRegistroCompensacaoRepository>();
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton(Mock.Of<IOrdemServicoRepository>());
            services.AddSingleton(estoque.Object);
            services.AddSingleton(Mock.Of<ISagaRepository>());
            services.AddSingleton(registros.Object);
            services.AddScoped<CompensacaoService>();
            var function = new CompensacaoFilaFunction(services.BuildServiceProvider());

            SQSBatchResponse response = await function.Handle(
                new SQSEvent { Records = [new SQSEvent.SQSMessage { MessageId = "m", Body = corpo }] },
                Mock.Of<Amazon.Lambda.Core.ILambdaContext>());

            response.BatchItemFailures.Should().ContainSingle();
            registros.Verify(r => r.Salvar(It.IsAny<RegistroCompensacao>()), Times.Never);
        }

        [Fact]
        public async Task FilaFunction_DeveReportarSomenteMensagensComFalha()
        {
            var estoque = new Mock<IEstoqueGateway>();
            Guid pecaRuim = Guid.NewGuid();
            estoque.Setup(e => e.EstornarEstoque(pecaRuim, 1)).ThrowsAsync(new HttpRequestException("fora"));

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton(Mock.Of<IOrdemServicoRepository>());
            services.AddSingleton(estoque.Object);
            services.AddSingleton(Mock.Of<ISagaRepository>());
            services.AddSingleton(Mock.Of<IRegistroCompensacaoRepository>());
            services.AddScoped<CompensacaoService>();
            var function = new CompensacaoFilaFunction(services.BuildServiceProvider());

            string Corpo(Guid peca) => JsonSerializer.Serialize(new
            {
                idCompensacao = Guid.NewGuid().ToString(),
                tipo = "EstornarEstoque",
                idOrdemServico = Guid.NewGuid(),
                pecasInsumosIds = new[] { peca },
                motivo = "teste"
            });

            var sqsEvent = new SQSEvent
            {
                Records =
                [
                    new SQSEvent.SQSMessage { MessageId = "ok", Body = Corpo(Guid.NewGuid()) },
                    new SQSEvent.SQSMessage { MessageId = "falha", Body = Corpo(pecaRuim) },
                    new SQSEvent.SQSMessage { MessageId = "invalida", Body = "{nao-e-json" }
                ]
            };

            SQSBatchResponse response = await function.Handle(sqsEvent, Mock.Of<Amazon.Lambda.Core.ILambdaContext>());

            response.BatchItemFailures.Select(f => f.ItemIdentifier).Should().BeEquivalentTo("falha", "invalida");
        }
    }
}
