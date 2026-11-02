using FluentAssertions;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Domain.Entities;
using OrdemServico.Lambda.Infrastructure.Persistence;

namespace OrdemServico.Lambda.Tests.Infrastructure
{
    public class DynamoDbMappingTests
    {
        [Fact]
        public void OrdemServico_DeveSobreviverAoRoundTripDoDocumentoDynamo()
        {
            var ordem = new Lambda.Domain.Entities.OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "Joao", "joao@email.com", "obs");
            var servico = new ItemServico(Guid.NewGuid(), "Troca de Oleo", 99.90m, 45);
            ordem.SincronizarItens([servico], [new ItemPecaInsumo(Guid.NewGuid(), "Filtro", 25.5m)]);
            servico.IniciarServico();
            ordem.AlterarStatus(StatusOS.EmExecucao);

            var mapa = DynamoDbItem.ParaMapa(OrdemServicoDocumento.DeDominio(ordem));
            var restaurada = DynamoDbItem.DoMapa<OrdemServicoDocumento>(mapa).ParaDominio(versao: 3);

            mapa.M["valorOrcamento"].N.Should().Be("125.4");
            restaurada.Should().BeEquivalentTo(ordem, options => options
                .Excluding(o => o.Eventos)
                .Excluding(o => o.Versao)
                .Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, TimeSpan.FromMilliseconds(1)))
                .WhenTypeIs<DateTime>());
            restaurada.Versao.Should().Be(3);
            restaurada.Status.Should().Be(StatusOS.EmExecucao);
        }

        [Fact]
        public void Saga_DeveSobreviverAoRoundTripComEnumComoTexto()
        {
            var saga = SagaReservaEstoque.Iniciar(Guid.NewGuid(), [Guid.NewGuid()]);
            saga.RegistrarReserva(saga.PecasSolicitadas[0]);
            saga.IniciarCompensacao("falhou");

            var mapa = DynamoDbItem.ParaMapa(saga);
            var restaurada = DynamoDbItem.DoMapa<SagaReservaEstoque>(mapa);

            mapa.M["status"].S.Should().Be("Compensando");
            restaurada.PecasReservadas.Should().Equal(saga.PecasReservadas);
            restaurada.MotivoFalha.Should().Be("falhou");
        }
    }
}
