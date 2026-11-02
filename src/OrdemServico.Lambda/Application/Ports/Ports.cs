using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Domain.Entities;

namespace OrdemServico.Lambda.Application.Ports
{
    public interface IOrdemServicoRepository
    {
        /// <summary>Grava a OS e os eventos pendentes (outbox) numa única transação.</summary>
        Task Adicionar(Domain.Entities.OrdemServico ordemServico);

        /// <summary>Atualiza a OS com concorrência otimista e grava os eventos pendentes (outbox).</summary>
        Task Atualizar(Domain.Entities.OrdemServico ordemServico);

        Task<Domain.Entities.OrdemServico?> ObterPorId(Guid id);

        /// <summary>OS que não estão Finalizadas nem Entregues (mesmo filtro do monólito).</summary>
        Task<IReadOnlyCollection<Domain.Entities.OrdemServico>> ObterAtivas();
    }

    public sealed record ClienteResumo(Guid Id, string Nome, string Email);

    /// <summary>
    /// Leitura dos dados de outros domínios (cliente, veículo, serviço e peça) que hoje
    /// continuam no monólito. Implementado via HTTP contra a API core.
    /// </summary>
    public interface ICatalogoGateway
    {
        Task<ClienteResumo?> ObterCliente(Guid idCliente);
        Task<bool> VeiculoExiste(Guid idVeiculo);
        Task<IReadOnlyCollection<ItemServico>> ObterServicos(IReadOnlyCollection<Guid> idsServicos);
        Task<IReadOnlyCollection<ItemPecaInsumo>> ObterPecasInsumo(IReadOnlyCollection<Guid> idsPecasInsumo);
    }

    /// <summary>Participante da saga: domínio de estoque (hoje na API core).</summary>
    public interface IEstoqueGateway
    {
        Task BaixarEstoque(Guid idPecaInsumo, int quantidade);
        Task EstornarEstoque(Guid idPecaInsumo, int quantidade);
    }

    /// <summary>Envia comandos de compensação para a fila SQS da saga.</summary>
    public interface IFilaCompensacao
    {
        Task Enviar(ComandoCompensacao comando);
    }

    public interface ISagaRepository
    {
        Task Salvar(SagaReservaEstoque saga);
        Task<SagaReservaEstoque?> Obter(Guid idOrdemServico, Guid idSaga);
    }

    /// <summary>Registro de idempotência das compensações (SQS e SNS entregam pelo menos uma vez).</summary>
    public interface IRegistroCompensacaoRepository
    {
        Task<RegistroCompensacao?> Obter(string idCompensacao);
        Task Salvar(RegistroCompensacao registro);
    }

    public interface IEmailService
    {
        Task<bool> EnviarEmailAsync(string para, string assunto, string corpoHtml);
    }
}
