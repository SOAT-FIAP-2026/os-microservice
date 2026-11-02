using Microsoft.Extensions.Logging;
using OrdemServico.Lambda.Application.Ports;

namespace OrdemServico.Lambda.Application.Saga
{
    /// <summary>
    /// Orquestrador da saga de inclusão de peças/insumos na OS.
    ///
    /// Passos:
    ///   1. baixa de estoque de cada peça nova (participante: domínio de estoque);
    ///   2. gravação da OS no DynamoDB (passo final, recebido como delegate).
    ///
    /// Se qualquer passo falhar, as baixas já confirmadas são compensadas de forma assíncrona:
    /// um comando EstornarEstoque é enviado à fila SQS de compensação, consumida pela
    /// CompensacaoFilaFunction (com retentativas e DLQ). A exceção original é relançada para
    /// que a API responda exatamente como o monólito responderia.
    /// </summary>
    public class ReservaEstoqueSaga(
        IEstoqueGateway estoqueGateway,
        ISagaRepository sagaRepository,
        IFilaCompensacao filaCompensacao,
        ILogger<ReservaEstoqueSaga> logger)
    {
        private readonly IEstoqueGateway _estoqueGateway = estoqueGateway;
        private readonly ISagaRepository _sagaRepository = sagaRepository;
        private readonly IFilaCompensacao _filaCompensacao = filaCompensacao;
        private readonly ILogger<ReservaEstoqueSaga> _logger = logger;

        public async Task<SagaReservaEstoque> Executar(
            Guid idOrdemServico,
            IReadOnlyCollection<Guid> pecasNovas,
            Func<SagaReservaEstoque, Task> passoFinal)
        {
            SagaReservaEstoque saga = SagaReservaEstoque.Iniciar(idOrdemServico, pecasNovas);
            await _sagaRepository.Salvar(saga);

            try
            {
                foreach (Guid idPecaInsumo in pecasNovas)
                {
                    await _estoqueGateway.BaixarEstoque(idPecaInsumo, 1);
                    saga.RegistrarReserva(idPecaInsumo);
                    await _sagaRepository.Salvar(saga);
                }

                await passoFinal(saga);
            }
            catch (Exception ex)
            {
                await Compensar(saga, ex);
                throw;
            }

            // A OS já foi gravada com as peças: uma falha aqui só afeta o log da saga e não pode gerar estorno.
            try
            {
                saga.Concluir();
                await _sagaRepository.Salvar(saga);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saga {IdSaga} da OS {IdOrdemServico} concluída, mas o log não foi atualizado.", saga.IdSaga, saga.IdOrdemServico);
            }

            return saga;
        }

        private async Task Compensar(SagaReservaEstoque saga, Exception causa)
        {
            _logger.LogWarning(causa,
                "Saga {IdSaga} da OS {IdOrdemServico} falhou após {Reservadas} baixa(s) de estoque. Iniciando compensação.",
                saga.IdSaga, saga.IdOrdemServico, saga.PecasReservadas.Count);

            try
            {
                if (saga.PecasReservadas.Count == 0)
                {
                    saga.IniciarCompensacao(causa.Message);
                    saga.MarcarCompensada();
                    await _sagaRepository.Salvar(saga);
                    return;
                }

                saga.IniciarCompensacao(causa.Message);
                await _sagaRepository.Salvar(saga);

                await _filaCompensacao.Enviar(new ComandoCompensacao(
                    IdCompensacao: $"saga-{saga.IdSaga}",
                    Tipo: TipoCompensacao.EstornarEstoque,
                    IdOrdemServico: saga.IdOrdemServico,
                    PecasInsumosIds: [.. saga.PecasReservadas],
                    Motivo: $"Falha na saga de inclusão de peças: {causa.Message}",
                    IdSaga: saga.IdSaga));
            }
            catch (Exception erroCompensacao)
            {
                // A saga fica registrada como Compensando no DynamoDB com as peças reservadas,
                // permitindo reprocessamento manual. A falha original continua sendo a resposta da API.
                _logger.LogError(erroCompensacao,
                    "Não foi possível agendar a compensação da saga {IdSaga} da OS {IdOrdemServico}. Peças pendentes de estorno: {Pecas}",
                    saga.IdSaga, saga.IdOrdemServico, string.Join(",", saga.PecasReservadas));
            }
        }
    }
}
