using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Configuration;
using OrdemServico.Lambda.Domain.Entities;
using OrdemServico.Lambda.Domain.Events;
using OrdemServico.Lambda.Infrastructure.Messaging;

namespace OrdemServico.Lambda.Tests.Application
{
    public class CompensacaoServiceTests
    {
        private readonly Mock<IOrdemServicoRepository> _repository = new();
        private readonly Mock<IEstoqueGateway> _estoque = new();
        private readonly Mock<ISagaRepository> _sagaRepository = new();
        private readonly Mock<IRegistroCompensacaoRepository> _registros = new();
        private readonly CompensacaoService _service;

        public CompensacaoServiceTests()
        {
            _service = new CompensacaoService(_repository.Object, _estoque.Object, _sagaRepository.Object, _registros.Object,
                NullLogger<CompensacaoService>.Instance);
        }

        [Fact]
        public async Task EstornarEstoque_DeveDevolverCadaPecaEMarcarSagaCompensada()
        {
            var saga = SagaReservaEstoque.Iniciar(Guid.NewGuid(), [Guid.NewGuid()]);
            _sagaRepository.Setup(s => s.Obter(saga.IdOrdemServico, saga.IdSaga)).ReturnsAsync(saga);
            Guid peca1 = Guid.NewGuid(), peca2 = Guid.NewGuid();

            await _service.Executar(new ComandoCompensacao("c1", TipoCompensacao.EstornarEstoque, saga.IdOrdemServico, [peca1, peca2], "falha", saga.IdSaga), "sqs");

            _estoque.Verify(e => e.EstornarEstoque(peca1, 1), Times.Once);
            _estoque.Verify(e => e.EstornarEstoque(peca2, 1), Times.Once);
            saga.Status.Should().Be(StatusSaga.Compensada);
            _registros.Verify(r => r.Salvar(It.Is<RegistroCompensacao>(reg => reg.Concluida)), Times.AtLeastOnce);
        }

        [Fact]
        public async Task Executar_QuandoJaConcluida_DeveIgnorarMensagemDuplicada()
        {
            _registros.Setup(r => r.Obter("c1")).ReturnsAsync(new RegistroCompensacao { IdCompensacao = "c1", Concluida = true });

            await _service.Executar(new ComandoCompensacao("c1", TipoCompensacao.EstornarEstoque, Guid.NewGuid(), [Guid.NewGuid()], "x"), "sqs");

            _estoque.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task EstornarEstoque_QuandoReprocessada_NaoDeveDevolverPecaJaEstornada()
        {
            Guid peca1 = Guid.NewGuid(), peca2 = Guid.NewGuid();
            var registroParcial = new RegistroCompensacao { IdCompensacao = "c1", PecasEstornadas = [peca1] };
            _registros.Setup(r => r.Obter("c1")).ReturnsAsync(registroParcial);

            await _service.Executar(new ComandoCompensacao("c1", TipoCompensacao.EstornarEstoque, Guid.NewGuid(), [peca1, peca2], "x"), "sqs");

            _estoque.Verify(e => e.EstornarEstoque(peca1, It.IsAny<int>()), Times.Never);
            _estoque.Verify(e => e.EstornarEstoque(peca2, 1), Times.Once);
        }

        [Fact]
        public async Task RemoverPecaInsumo_DeveRemoverItemERecalcularOrcamento()
        {
            var ordem = new Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "Joao", null);
            var pecaFalhou = new ItemPecaInsumo(Guid.NewGuid(), "Vela", 20m);
            ordem.SincronizarItens([new ItemServico(Guid.NewGuid(), "S", 100m, 10)], [pecaFalhou]);
            ordem.LimparEventos();
            _repository.Setup(r => r.ObterPorId(ordem.Id)).ReturnsAsync(ordem);

            await _service.Executar(new ComandoCompensacao("evt-1", TipoCompensacao.RemoverPecaInsumo, ordem.Id, [pecaFalhou.IdPecaInsumo], "sem estoque"), "sns");

            ordem.ItensPecaInsumo.Should().BeEmpty();
            ordem.ValorOrcamento.Should().Be(100m);
            _repository.Verify(r => r.Atualizar(It.Is<Lambda.Domain.Entities.OrdemServico>(o =>
                o.Eventos.OfType<CompensacaoExecutada>().Any())), Times.Once);
        }

        [Fact]
        public async Task CancelarOrdemServico_DeveCancelarEPublicarOrdemServicoCancelada()
        {
            var ordem = new Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "Joao", null);
            ordem.LimparEventos();
            _repository.Setup(r => r.ObterPorId(ordem.Id)).ReturnsAsync(ordem);

            await _service.Executar(new ComandoCompensacao("evt-2", TipoCompensacao.CancelarOrdemServico, ordem.Id, [], "Pagamento recusado"), "sns");

            ordem.Status.Should().Be(StatusOS.Cancelada);
            _repository.Verify(r => r.Atualizar(It.Is<Lambda.Domain.Entities.OrdemServico>(o =>
                o.Eventos.OfType<OrdemServicoCancelada>().Any())), Times.Once);
        }

        [Fact]
        public async Task Executar_QuandoEstornoFalha_DevePropagarErroParaRetentativa()
        {
            _estoque.Setup(e => e.EstornarEstoque(It.IsAny<Guid>(), It.IsAny<int>())).ThrowsAsync(new HttpRequestException("core fora"));

            Func<Task> acao = () => _service.Executar(new ComandoCompensacao("c9", TipoCompensacao.EstornarEstoque, Guid.NewGuid(), [Guid.NewGuid()], "x"), "sqs");

            await acao.Should().ThrowAsync<HttpRequestException>();
            _registros.Verify(r => r.Salvar(It.Is<RegistroCompensacao>(reg => reg.Concluida)), Times.Never);
        }

        [Fact]
        public async Task CancelarOrdemServico_ComPecasEPublisher_DeveEstornarEstoqueEPublicarSns()
        {
            var snsMock = new Mock<Amazon.SimpleNotificationService.IAmazonSimpleNotificationService>();
            var settings = new LambdaSettings
            {
                EventosTopicArn = "arn:aws:sns:sa-east-1:000000000000:techchallenge-eventos-local",
                EventoOrigem = "techchallenge.ordem-servico"
            };
            var publisher = new SnsPublisher(snsMock.Object, settings);
            var serviceComPublisher = new CompensacaoService(
                _repository.Object, _estoque.Object, _sagaRepository.Object, _registros.Object,
                NullLogger<CompensacaoService>.Instance, publisher);

            var ordem = new Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "Joao", null);
            Guid peca1 = Guid.NewGuid(), peca2 = Guid.NewGuid();
            ordem.SincronizarItens([], [new ItemPecaInsumo(peca1, "Peca 1", 50m), new ItemPecaInsumo(peca2, "Peca 2", 100m)]);
            ordem.LimparEventos();
            _repository.Setup(r => r.ObterPorId(ordem.Id)).ReturnsAsync(ordem);

            await serviceComPublisher.Executar(new ComandoCompensacao("evt-cancelar", TipoCompensacao.CancelarOrdemServico, ordem.Id, [], "Cancelado"), "sns");

            ordem.Status.Should().Be(StatusOS.Cancelada);
            _estoque.Verify(e => e.EstornarEstoque(peca1, 1), Times.Once);
            _estoque.Verify(e => e.EstornarEstoque(peca2, 1), Times.Once);
            snsMock.Verify(s => s.PublishAsync(It.Is<Amazon.SimpleNotificationService.Model.PublishRequest>(req =>
                req.MessageAttributes["tipoEvento"].StringValue == "OrdemServicoCanceladaEvent" &&
                req.MessageAttributes["idOrdemServico"].StringValue == ordem.Id.ToString()), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task CancelarOrdemServico_ComPecaJaEstornadaNoRegistro_NaoDeveEstornarNovamente()
        {
            var ordem = new Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "Joao", null);
            Guid peca1 = Guid.NewGuid(), peca2 = Guid.NewGuid();
            ordem.SincronizarItens([], [new ItemPecaInsumo(peca1, "Peca 1", 50m), new ItemPecaInsumo(peca2, "Peca 2", 100m)]);
            ordem.LimparEventos();
            _repository.Setup(r => r.ObterPorId(ordem.Id)).ReturnsAsync(ordem);

            var registro = new RegistroCompensacao
            {
                IdCompensacao = "evt-parcial",
                PecasEstornadas = [peca1]
            };
            _registros.Setup(r => r.Obter("evt-parcial")).ReturnsAsync(registro);

            await _service.Executar(new ComandoCompensacao("evt-parcial", TipoCompensacao.CancelarOrdemServico, ordem.Id, [], "Cancelado"), "sns");

            _estoque.Verify(e => e.EstornarEstoque(peca1, It.IsAny<int>()), Times.Never);
            _estoque.Verify(e => e.EstornarEstoque(peca2, 1), Times.Once);
        }
    }
}
