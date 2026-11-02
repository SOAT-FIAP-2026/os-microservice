using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrdemServico.Lambda.Application.DTOs.Requests;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Application.Services;
using OrdemServico.Lambda.Domain.Entities;
using OrdemServico.Lambda.Domain.Events;
using OrdemServico.Lambda.Domain.Exceptions;

namespace OrdemServico.Lambda.Tests.Application
{
    public class OrdemServicoServiceTests
    {
        private readonly Mock<IOrdemServicoRepository> _repository = new();
        private readonly Mock<ICatalogoGateway> _catalogo = new();
        private readonly Mock<IEstoqueGateway> _estoque = new();
        private readonly Mock<ISagaRepository> _sagaRepository = new();
        private readonly Mock<IFilaCompensacao> _fila = new();
        private readonly Mock<IEmailService> _email = new();
        private readonly OrdemServicoService _service;

        public OrdemServicoServiceTests()
        {
            var saga = new ReservaEstoqueSaga(_estoque.Object, _sagaRepository.Object, _fila.Object, NullLogger<ReservaEstoqueSaga>.Instance);
            _service = new OrdemServicoService(_repository.Object, _catalogo.Object, saga, _email.Object, NullLogger<OrdemServicoService>.Instance);
        }

        private Lambda.Domain.Entities.OrdemServico OrdemExistente(StatusOS? status = null)
        {
            var ordem = new Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "Joao", "joao@email.com");
            if (status != null)
                ordem.AlterarStatus(status);
            ordem.LimparEventos();
            _repository.Setup(r => r.ObterPorId(ordem.Id)).ReturnsAsync(ordem);
            return ordem;
        }

        [Fact]
        public async Task IncluirPecaInsumo_EmOsCancelada_NaoDeveBaixarEstoque()
        {
            var ordem = OrdemExistente(StatusOS.Cancelada);

            Func<Task> acao = () => _service.IncluirPecaInsumo(ordem.Id, new OrdemServicoPecaInsumoRequest { PecasInsumosIds = [Guid.NewGuid()] });

            await acao.Should().ThrowAsync<InvalidOperationException>();
            _estoque.Verify(e => e.BaixarEstoque(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public async Task IncluirPecaInsumo_QuandoSoOLogDaSagaFalha_NaoDeveEstornar()
        {
            var ordem = OrdemExistente();
            var peca = new ItemPecaInsumo(Guid.NewGuid(), "Filtro", 10m);
            _catalogo.Setup(c => c.ObterPecasInsumo(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([peca]);
            _sagaRepository.Setup(s => s.Salvar(It.Is<SagaReservaEstoque>(saga => saga.Status == StatusSaga.Concluida)))
                .ThrowsAsync(new InvalidOperationException("dynamo fora"));

            await _service.IncluirPecaInsumo(ordem.Id, new OrdemServicoPecaInsumoRequest { PecasInsumosIds = [peca.IdPecaInsumo] });

            _fila.Verify(f => f.Enviar(It.IsAny<ComandoCompensacao>()), Times.Never);
        }

        [Fact]
        public async Task Criar_QuandoClienteNaoExiste_DeveLancarMesmaExcecaoDoMonolito()
        {
            var request = new OrdemServicoRequest { ClienteId = Guid.NewGuid(), VeiculoId = Guid.NewGuid() };

            Func<Task> acao = () => _service.Criar(request);

            await acao.Should().ThrowAsync<ClienteNaoEncontradoException>()
                .WithMessage($"Cliente com id '{request.ClienteId}' não encontrado.");
        }

        [Fact]
        public async Task Criar_QuandoDadosValidos_DeveGravarENotificar()
        {
            var request = new OrdemServicoRequest { ClienteId = Guid.NewGuid(), VeiculoId = Guid.NewGuid(), Observacao = "obs" };
            _catalogo.Setup(c => c.ObterCliente(request.ClienteId)).ReturnsAsync(new ClienteResumo(request.ClienteId, "Joao", "joao@email.com"));
            _catalogo.Setup(c => c.VeiculoExiste(request.VeiculoId)).ReturnsAsync(true);
            _email.Setup(e => e.EnviarEmailAsync("joao@email.com", It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

            var (id, notificado) = await _service.Criar(request);

            id.Should().NotBeEmpty();
            notificado.Should().BeTrue();
            _repository.Verify(r => r.Adicionar(It.Is<Lambda.Domain.Entities.OrdemServico>(o =>
                o.Id == id && o.Status == StatusOS.Recebida && o.ClienteNome == "Joao")), Times.Once);
        }

        [Fact]
        public async Task IniciarDiagnostico_ForaDeRecebida_DeveLancarExcecao()
        {
            var ordem = OrdemExistente(StatusOS.EmExecucao);

            Func<Task> acao = () => _service.IniciarDiagnostico(ordem.Id);

            await acao.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("A ordem de serviço precisa estar no status Recebida para iniciar o diagnóstico.");
        }

        [Fact]
        public async Task IncluirServico_QuandoServicoNaoExiste_DeveLancarServicoNaoEncontrado()
        {
            var ordem = OrdemExistente();
            var idServico = Guid.NewGuid();
            _catalogo.Setup(c => c.ObterServicos(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([]);

            Func<Task> acao = () => _service.IncluirServico(ordem.Id, new OrdemServicoServicosRequest { ServicosIds = [idServico] });

            await acao.Should().ThrowAsync<ServicoNaoEncontradoException>();
        }

        [Fact]
        public async Task IncluirPecaInsumo_QuandoTudoOk_DeveBaixarEstoqueGravarEConcluirSaga()
        {
            var ordem = OrdemExistente();
            var peca = new ItemPecaInsumo(Guid.NewGuid(), "Filtro", 30m);
            _catalogo.Setup(c => c.ObterPecasInsumo(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([peca]);

            var response = await _service.IncluirPecaInsumo(ordem.Id, new OrdemServicoPecaInsumoRequest { PecasInsumosIds = [peca.IdPecaInsumo] });

            response.ValorTotal.Should().Be(30m);
            response.ItensPecaInsumo.Should().ContainSingle(i => i.IdPecaInsumo == peca.IdPecaInsumo && i.Descricao == "Filtro");
            _estoque.Verify(e => e.BaixarEstoque(peca.IdPecaInsumo, 1), Times.Once);
            _repository.Verify(r => r.Atualizar(It.Is<Lambda.Domain.Entities.OrdemServico>(o =>
                o.Eventos.OfType<PecasInsumoIncluidas>().Any())), Times.Once);
            _sagaRepository.Verify(s => s.Salvar(It.Is<SagaReservaEstoque>(saga => saga.Status == StatusSaga.Concluida)), Times.AtLeastOnce);
            _fila.Verify(f => f.Enviar(It.IsAny<ComandoCompensacao>()), Times.Never);
        }

        [Fact]
        public async Task IncluirPecaInsumo_QuandoSegundaBaixaFalha_DeveCompensarSomenteAPrimeiraERepassarErro()
        {
            var ordem = OrdemExistente();
            var peca1 = new ItemPecaInsumo(Guid.NewGuid(), "Filtro", 30m);
            var peca2 = new ItemPecaInsumo(Guid.NewGuid(), "Vela", 20m);
            _catalogo.Setup(c => c.ObterPecasInsumo(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([peca1, peca2]);
            _estoque.Setup(e => e.BaixarEstoque(peca2.IdPecaInsumo, 1)).ThrowsAsync(new IntegracaoNegocioException("Estoque insuficiente."));

            Func<Task> acao = () => _service.IncluirPecaInsumo(ordem.Id,
                new OrdemServicoPecaInsumoRequest { PecasInsumosIds = [peca1.IdPecaInsumo, peca2.IdPecaInsumo] });

            await acao.Should().ThrowAsync<IntegracaoNegocioException>().WithMessage("Estoque insuficiente.");
            _repository.Verify(r => r.Atualizar(It.IsAny<Lambda.Domain.Entities.OrdemServico>()), Times.Never);
            _fila.Verify(f => f.Enviar(It.Is<ComandoCompensacao>(c =>
                c.Tipo == TipoCompensacao.EstornarEstoque &&
                c.IdOrdemServico == ordem.Id &&
                c.PecasInsumosIds.SequenceEqual(new[] { peca1.IdPecaInsumo }))), Times.Once);
        }

        [Fact]
        public async Task IncluirPecaInsumo_QuandoGravacaoDaOsFalha_DeveCompensarTodasAsBaixas()
        {
            var ordem = OrdemExistente();
            var peca = new ItemPecaInsumo(Guid.NewGuid(), "Filtro", 30m);
            _catalogo.Setup(c => c.ObterPecasInsumo(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([peca]);
            _repository.Setup(r => r.Atualizar(It.IsAny<Lambda.Domain.Entities.OrdemServico>()))
                .ThrowsAsync(new ConcorrenciaOrdemServicoException(ordem.Id));

            Func<Task> acao = () => _service.IncluirPecaInsumo(ordem.Id, new OrdemServicoPecaInsumoRequest { PecasInsumosIds = [peca.IdPecaInsumo] });

            await acao.Should().ThrowAsync<ConcorrenciaOrdemServicoException>();
            _fila.Verify(f => f.Enviar(It.Is<ComandoCompensacao>(c => c.PecasInsumosIds.Single() == peca.IdPecaInsumo && c.IdSaga != null)), Times.Once);
        }

        [Fact]
        public async Task AprovarOrcamento_Reprovado_DeveConcluirEGerarEventoDeCompensacao()
        {
            var ordem = OrdemExistente(StatusOS.AguardandoAprovacao);

            var response = await _service.AprovarOrcamento(ordem.Id, aprovado: false);

            response.StatusDescricao.Should().Be("Orçamento Reprovado");
            response.DataConclusao.Should().NotBeNull();
            _repository.Verify(r => r.Atualizar(It.Is<Lambda.Domain.Entities.OrdemServico>(o =>
                o.Eventos.OfType<OrcamentoReprovado>().Any())), Times.Once);
        }

        [Fact]
        public async Task ConfirmarEntrega_SemFinalizar_DeveLancarExcecao()
        {
            var ordem = OrdemExistente(StatusOS.EmExecucao);

            Func<Task> acao = () => _service.ConfirmarEntrega(ordem.Id);

            await acao.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("A ordem de serviço não pode ser entregue, pois não foi finalizada.");
        }

        [Fact]
        public async Task FinalizarServico_QuandoUltimoServico_DeveFinalizarOrdem()
        {
            var ordem = OrdemExistente();
            var servico = new ItemServico(Guid.NewGuid(), "Alinhamento", 50m, 60);
            ordem.SincronizarItens([servico], []);

            await _service.IniciarServico(ordem.Id, servico.IdServico);
            await _service.FinalizarServico(ordem.Id, servico.IdServico);

            ordem.Status.Should().Be(StatusOS.Finalizada);
            ordem.DataConclusao.Should().NotBeNull();
        }

        [Fact]
        public async Task ObterTodos_DeveOrdenarPorPrioridadeDoStatusEDataDeAbertura()
        {
            var recebida = new Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "A", null);
            var emExecucao = new Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "B", null);
            emExecucao.AlterarStatus(StatusOS.EmExecucao);
            var aguardando = new Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "C", null);
            aguardando.AlterarStatus(StatusOS.AguardandoAprovacao);
            _repository.Setup(r => r.ObterAtivas()).ReturnsAsync([recebida, aguardando, emExecucao]);

            var result = await _service.ObterTodos();

            result.Select(o => o.ClienteNome).Should().Equal("B", "C", "A");
        }

        [Fact]
        public async Task ObterProgresso_DeveCalcularPercentualPeloTempoEstimado()
        {
            var ordem = OrdemExistente();
            var servico1 = new ItemServico(Guid.NewGuid(), "S1", 10m, 30);
            var servico2 = new ItemServico(Guid.NewGuid(), "S2", 10m, 30);
            ordem.SincronizarItens([servico1, servico2], []);
            servico1.IniciarServico();
            servico1.FinalizarServico();

            var progresso = await _service.ObterProgresso(ordem.Id);

            progresso.PercentualConcluido.Should().Be(50);
            progresso.Servicos.Select(s => s.Status).Should().Equal("Finalizado", "Pendente");
        }
    }
}
