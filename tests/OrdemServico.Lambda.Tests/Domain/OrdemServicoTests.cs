using FluentAssertions;
using OrdemServico.Lambda.Domain.Entities;
using OrdemServico.Lambda.Domain.Events;

namespace OrdemServico.Lambda.Tests.Domain
{
    public class OrdemServicoTests
    {
        private static Lambda.Domain.Entities.OrdemServico NovaOrdem() =>
            new(Guid.NewGuid(), Guid.NewGuid(), "Joao Silva", "joao@email.com", "Barulho no motor");

        [Fact]
        public void Criar_DeveIniciarRecebidaEPublicarOrdemServicoCriada()
        {
            var ordem = NovaOrdem();

            ordem.Status.Should().Be(StatusOS.Recebida);
            ordem.Eventos.Should().ContainSingle().Which.Should().BeOfType<OrdemServicoCriada>();
        }

        [Fact]
        public void SincronizarItens_DeveSomarOrcamentoSemDuplicarItens()
        {
            var ordem = NovaOrdem();
            var servico = new ItemServico(Guid.NewGuid(), "Troca de Oleo", 100m, 30);
            var peca = new ItemPecaInsumo(Guid.NewGuid(), "Filtro de Ar", 25.555m);

            ordem.SincronizarItens([servico], [peca]);
            ordem.SincronizarItens([new ItemServico(servico.IdServico, "Troca de Oleo", 120m, 30)], []);

            ordem.ItensServico.Should().ContainSingle();
            ordem.ValorOrcamento.Should().Be(145.56m);
        }

        [Fact]
        public void AlterarStatus_DevePublicarEventoSomenteQuandoMuda()
        {
            var ordem = NovaOrdem();
            ordem.LimparEventos();

            ordem.AlterarStatus(StatusOS.EmExecucao);
            ordem.AlterarStatus(StatusOS.EmExecucao);

            ordem.Eventos.Should().ContainSingle().Which.Should().BeEquivalentTo(
                new { StatusAnterior = "RECEBIDA", StatusAtual = "EM_EXECUCAO" });
        }

        [Fact]
        public void ReprovarOrcamento_DeveConcluirEPublicarGatilhoDeCompensacaoComAsPecas()
        {
            var ordem = NovaOrdem();
            var idPeca = Guid.NewGuid();
            ordem.SincronizarItens([], [new ItemPecaInsumo(idPeca, "Pastilha", 80m)]);
            ordem.LimparEventos();

            ordem.ReprovarOrcamento();

            ordem.Status.Should().Be(StatusOS.OrcamentoReprovado);
            ordem.DataConclusao.Should().NotBeNull();
            ordem.Eventos.OfType<OrcamentoReprovado>().Single().PecasInsumosIds.Should().Equal(idPeca);
        }

        [Theory]
        [InlineData("CANCELADA")]
        [InlineData("ORCAMENTO_REPROVADO")]
        [InlineData("ENTREGUE")]
        public void Cancelar_NaoDeveCancelarOrdemJaEncerradaOuCompensada(string codigoStatus)
        {
            var ordem = NovaOrdem();
            ordem.AlterarStatus(StatusOS.ObterPorCodigo(codigoStatus));
            ordem.LimparEventos();

            ordem.Cancelar("Pagamento recusado").Should().BeFalse();
            ordem.Eventos.Should().BeEmpty();
        }

        [Fact]
        public void Cancelar_DevePublicarOrdemServicoCancelada()
        {
            var ordem = NovaOrdem();
            ordem.LimparEventos();

            ordem.Cancelar("Pagamento recusado").Should().BeTrue();

            ordem.Status.Should().Be(StatusOS.Cancelada);
            ordem.Eventos.OfType<OrdemServicoCancelada>().Should().ContainSingle();
        }

        [Fact]
        public void FinalizarServico_SemIniciar_DeveLancarExcecao()
        {
            var item = new ItemServico(Guid.NewGuid(), "Alinhamento", 50m, 60);

            Action acao = item.FinalizarServico;

            acao.Should().Throw<InvalidOperationException>().WithMessage("O serviço precisa ser iniciado antes de ser finalizado.");
        }

        [Fact]
        public void StatusOS_DeveManterIdsDoSeedDoMonolito()
        {
            StatusOS.Recebida.Id.Should().Be(Guid.Parse("c3d4e5f6-a7b8-9012-cdef-123456789012"));
            StatusOS.Entregue.Id.Should().Be(Guid.Parse("e5f6a7b8-c9d0-1234-efab-345678901234"));
            StatusOS.Todos.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        }
    }
}
