using Microsoft.Extensions.Logging;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Domain.Events;

namespace OrdemServico.Lambda.Application.Saga
{
    /// <summary>
    /// Executa as transações compensatórias da saga. É o ponto comum dos dois gatilhos:
    /// a fila SQS (CompensacaoFilaFunction) e os eventos do SNS (CompensacaoEventoFunction).
    /// Todas as operações são idempotentes por IdCompensacao.
    /// </summary>
    public class CompensacaoService(
        IOrdemServicoRepository ordemServicoRepository,
        IEstoqueGateway estoqueGateway,
        ISagaRepository sagaRepository,
        IRegistroCompensacaoRepository registroRepository,
        ILogger<CompensacaoService> logger)
    {
        private readonly IOrdemServicoRepository _ordemServicoRepository = ordemServicoRepository;
        private readonly IEstoqueGateway _estoqueGateway = estoqueGateway;
        private readonly ISagaRepository _sagaRepository = sagaRepository;
        private readonly IRegistroCompensacaoRepository _registroRepository = registroRepository;
        private readonly ILogger<CompensacaoService> _logger = logger;

        public async Task Executar(ComandoCompensacao comando, string origem)
        {
            RegistroCompensacao? registro = await _registroRepository.Obter(comando.IdCompensacao);

            if (registro?.Concluida == true)
            {
                _logger.LogInformation("Compensação {IdCompensacao} já executada. Mensagem duplicada ignorada.", comando.IdCompensacao);
                return;
            }

            registro ??= new RegistroCompensacao
            {
                IdCompensacao = comando.IdCompensacao,
                Tipo = comando.Tipo,
                IdOrdemServico = comando.IdOrdemServico,
                Origem = origem
            };

            switch (comando.Tipo)
            {
                case TipoCompensacao.EstornarEstoque:
                    await EstornarEstoque(comando, registro);
                    break;
                case TipoCompensacao.RemoverPecaInsumo:
                    await RemoverPecasDaOrdem(comando, origem);
                    break;
                case TipoCompensacao.CancelarOrdemServico:
                    await CancelarOrdem(comando, origem);
                    break;
                default:
                    throw new InvalidOperationException($"Tipo de compensação não suportado: {comando.Tipo}.");
            }

            registro.Concluir();
            await _registroRepository.Salvar(registro);

            _logger.LogInformation(
                "Compensação {IdCompensacao} ({Tipo}) da OS {IdOrdemServico} concluída. Origem: {Origem}",
                comando.IdCompensacao, comando.Tipo, comando.IdOrdemServico, origem);
        }

        private async Task EstornarEstoque(ComandoCompensacao comando, RegistroCompensacao registro)
        {
            foreach (Guid idPecaInsumo in comando.PecasInsumosIds.Distinct())
            {
                if (registro.PecasEstornadas.Contains(idPecaInsumo))
                    continue;

                await _estoqueGateway.EstornarEstoque(idPecaInsumo, 1);
                registro.MarcarPecaEstornada(idPecaInsumo);
                await _registroRepository.Salvar(registro);
            }

            if (comando.IdSaga is Guid idSaga)
            {
                SagaReservaEstoque? saga = await _sagaRepository.Obter(comando.IdOrdemServico, idSaga);

                if (saga != null)
                {
                    saga.MarcarCompensada();
                    await _sagaRepository.Salvar(saga);
                }
            }
        }

        private async Task RemoverPecasDaOrdem(ComandoCompensacao comando, string origem)
        {
            Domain.Entities.OrdemServico? ordemServico = await _ordemServicoRepository.ObterPorId(comando.IdOrdemServico);

            if (ordemServico == null)
            {
                _logger.LogWarning("OS {IdOrdemServico} não encontrada para compensação {IdCompensacao}.", comando.IdOrdemServico, comando.IdCompensacao);
                return;
            }

            bool alterou = false;
            foreach (Guid idPecaInsumo in comando.PecasInsumosIds)
                alterou |= ordemServico.RemoverItemPecaInsumo(idPecaInsumo);

            if (!alterou)
                return;

            ordemServico.RecalcularOrcamento();
            ordemServico.RegistrarEvento(new CompensacaoExecutada(ordemServico.Id, comando.IdCompensacao, comando.Tipo.ToString(), origem));
            await _ordemServicoRepository.Atualizar(ordemServico);
        }

        private async Task CancelarOrdem(ComandoCompensacao comando, string origem)
        {
            Domain.Entities.OrdemServico? ordemServico = await _ordemServicoRepository.ObterPorId(comando.IdOrdemServico);

            if (ordemServico == null)
            {
                _logger.LogWarning("OS {IdOrdemServico} não encontrada para compensação {IdCompensacao}.", comando.IdOrdemServico, comando.IdCompensacao);
                return;
            }

            if (!ordemServico.Cancelar(comando.Motivo))
            {
                _logger.LogInformation("OS {IdOrdemServico} no status {Status} não pode ser cancelada. Nada a compensar.",
                    ordemServico.Id, ordemServico.Status.Codigo);
                return;
            }

            ordemServico.RegistrarEvento(new CompensacaoExecutada(ordemServico.Id, comando.IdCompensacao, comando.Tipo.ToString(), origem));
            await _ordemServicoRepository.Atualizar(ordemServico);
        }
    }
}
