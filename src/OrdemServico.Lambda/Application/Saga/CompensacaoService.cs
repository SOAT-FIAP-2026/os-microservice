using System.Text.Json;
using Fiap.TechChallenge.Contracts.Events;
using Microsoft.Extensions.Logging;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Configuration;
using OrdemServico.Lambda.Domain.Events;
using OrdemServico.Lambda.Infrastructure.Messaging;

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
        ILogger<CompensacaoService> logger,
        SnsPublisher? snsPublisher = null)
    {
        private readonly IOrdemServicoRepository _ordemServicoRepository = ordemServicoRepository;
        private readonly IEstoqueGateway _estoqueGateway = estoqueGateway;
        private readonly ISagaRepository _sagaRepository = sagaRepository;
        private readonly IRegistroCompensacaoRepository _registroRepository = registroRepository;
        private readonly ILogger<CompensacaoService> _logger = logger;
        private readonly SnsPublisher? _snsPublisher = snsPublisher;

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
                    await CancelarOrdem(comando, registro, origem);
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

        private Task CancelarOrdem(ComandoCompensacao comando, string origem) => CancelarOrdem(comando, null, origem);

        private async Task CancelarOrdem(ComandoCompensacao comando, RegistroCompensacao? registro, string origem)
        {
            Domain.Entities.OrdemServico? ordemServico = await _ordemServicoRepository.ObterPorId(comando.IdOrdemServico);

            if (ordemServico == null)
            {
                _logger.LogWarning("OS {IdOrdemServico} não encontrada para compensação {IdCompensacao}.", comando.IdOrdemServico, comando.IdCompensacao);
                return;
            }

            var pecas = ordemServico.ItensPecaInsumo.Select(p => p.IdPecaInsumo).Distinct().ToList();

            if (!ordemServico.Cancelar(comando.Motivo))
            {
                _logger.LogInformation("OS {IdOrdemServico} no status {Status} não pode ser cancelada. Nada a compensar.",
                    ordemServico.Id, ordemServico.Status.Codigo);
                return;
            }

            ordemServico.RegistrarEvento(new CompensacaoExecutada(ordemServico.Id, comando.IdCompensacao, comando.Tipo.ToString(), origem));
            await _ordemServicoRepository.Atualizar(ordemServico);

            foreach (Guid idPeca in pecas)
            {
                if (registro != null && registro.PecasEstornadas.Contains(idPeca))
                    continue;

                await _estoqueGateway.EstornarEstoque(idPeca, 1);

                if (registro != null)
                {
                    registro.MarcarPecaEstornada(idPeca);
                    await _registroRepository.Salvar(registro);
                }
            }

            if (_snsPublisher != null)
            {
                try
                {
                    var eventoCancelada = new OrdemServicoCanceladaEvent(
                        ordemServico.Id,
                        comando.Motivo,
                        pecas);

                    await _snsPublisher.Publicar(new EventoOutbox(
                        Guid.NewGuid().ToString(),
                        "OrdemServicoCanceladaEvent",
                        ordemServico.Id.ToString(),
                        JsonSerializer.Serialize(eventoCancelada, JsonDefaults.Options)));

                    _logger.LogInformation("OrdemServicoCanceladaEvent publicado no SNS para OS {OsId}.", ordemServico.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao publicar OrdemServicoCanceladaEvent no SNS para OS {OsId}", ordemServico.Id);
                }
            }
        }
    }
}
