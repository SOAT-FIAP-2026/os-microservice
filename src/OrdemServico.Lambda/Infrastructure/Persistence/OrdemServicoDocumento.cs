using OrdemServico.Lambda.Domain.Entities;

namespace OrdemServico.Lambda.Infrastructure.Persistence
{
    internal sealed class OrdemServicoDocumento
    {
        public Guid Id { get; set; }
        public Guid IdCliente { get; set; }
        public Guid IdVeiculo { get; set; }
        public string ClienteNome { get; set; } = string.Empty;
        public string? ClienteEmail { get; set; }
        public string StatusCodigo { get; set; } = string.Empty;
        public string? Observacao { get; set; }
        public DateTime DataAbertura { get; set; }
        public DateTime? DataInicioDiagnostico { get; set; }
        public DateTime? DataConclusao { get; set; }
        public decimal? ValorOrcamento { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime AtualizadoEm { get; set; }
        public List<ItemServicoDocumento> ItensServico { get; set; } = [];
        public List<ItemPecaInsumoDocumento> ItensPecaInsumo { get; set; } = [];

        public static OrdemServicoDocumento DeDominio(Domain.Entities.OrdemServico ordemServico)
        {
            return new OrdemServicoDocumento
            {
                Id = ordemServico.Id,
                IdCliente = ordemServico.IdCliente,
                IdVeiculo = ordemServico.IdVeiculo,
                ClienteNome = ordemServico.ClienteNome,
                ClienteEmail = ordemServico.ClienteEmail,
                StatusCodigo = ordemServico.Status.Codigo,
                Observacao = ordemServico.Observacao,
                DataAbertura = ordemServico.DataAbertura,
                DataInicioDiagnostico = ordemServico.DataInicioDiagnostico,
                DataConclusao = ordemServico.DataConclusao,
                ValorOrcamento = ordemServico.ValorOrcamento,
                CriadoEm = ordemServico.CriadoEm,
                AtualizadoEm = ordemServico.AtualizadoEm,
                ItensServico = [.. ordemServico.ItensServico.Select(item => new ItemServicoDocumento
                {
                    IdServico = item.IdServico,
                    Nome = item.Nome,
                    ValorUnitario = item.ValorUnitario,
                    TempoEstimadoMinutos = item.TempoEstimadoMinutos,
                    DataHoraInicio = item.DataHoraInicio,
                    DataHoraFim = item.DataHoraFim
                })],
                ItensPecaInsumo = [.. ordemServico.ItensPecaInsumo.Select(item => new ItemPecaInsumoDocumento
                {
                    IdPecaInsumo = item.IdPecaInsumo,
                    Descricao = item.Descricao,
                    ValorUnitario = item.ValorUnitario
                })]
            };
        }

        public Domain.Entities.OrdemServico ParaDominio(long versao)
        {
            return Domain.Entities.OrdemServico.Restaurar(
                Id, IdCliente, IdVeiculo, ClienteNome, ClienteEmail, StatusOS.ObterPorCodigo(StatusCodigo),
                Observacao, DataAbertura, DataInicioDiagnostico, DataConclusao, ValorOrcamento, CriadoEm, AtualizadoEm, versao,
                ItensServico.Select(item => ItemServico.Restaurar(
                    item.IdServico, item.Nome, item.ValorUnitario, item.TempoEstimadoMinutos, item.DataHoraInicio, item.DataHoraFim)),
                ItensPecaInsumo.Select(item => new ItemPecaInsumo(item.IdPecaInsumo, item.Descricao, item.ValorUnitario)));
        }
    }

    internal sealed class ItemServicoDocumento
    {
        public Guid IdServico { get; set; }
        public string Nome { get; set; } = string.Empty;
        public decimal ValorUnitario { get; set; }
        public int TempoEstimadoMinutos { get; set; }
        public DateTime? DataHoraInicio { get; set; }
        public DateTime? DataHoraFim { get; set; }
    }

    internal sealed class ItemPecaInsumoDocumento
    {
        public Guid IdPecaInsumo { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public decimal ValorUnitario { get; set; }
    }
}
