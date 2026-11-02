using OrdemServico.Lambda.Domain.Events;

namespace OrdemServico.Lambda.Domain.Entities
{
    /// <summary>
    /// Agregado Ordem de Serviço migrado do monólito (Fiap.TechChallenge.Domain.Entities.OrdemServico).
    ///
    /// Diferenças em relação ao monólito, todas decorrentes da persistência em DynamoDB:
    /// - Status é o próprio <see cref="StatusOS"/> (catálogo em código) em vez de entidade relacional;
    /// - Cliente é guardado como snapshot (nome e e-mail) usado na resposta e na notificação;
    /// - Orçamento é o valor total desnormalizado no agregado;
    /// - Versao habilita controle de concorrência otimista no DynamoDB;
    /// - Mudanças relevantes geram eventos de domínio, gravados no outbox na mesma transação.
    /// </summary>
    public class OrdemServico
    {
        public Guid Id { get; private set; }
        public Guid IdCliente { get; private set; }
        public Guid IdVeiculo { get; private set; }
        public string ClienteNome { get; private set; } = string.Empty;
        public string? ClienteEmail { get; private set; }
        public StatusOS Status { get; private set; }
        public string? Observacao { get; private set; }
        public DateTime DataAbertura { get; private set; }
        public DateTime? DataInicioDiagnostico { get; private set; }
        public DateTime? DataConclusao { get; private set; }
        public decimal? ValorOrcamento { get; private set; }
        public DateTime CriadoEm { get; private set; }
        public DateTime AtualizadoEm { get; private set; }
        public long Versao { get; private set; }

        private readonly List<ItemServico> _itensServico = [];
        public IReadOnlyCollection<ItemServico> ItensServico => _itensServico.AsReadOnly();

        private readonly List<ItemPecaInsumo> _itensPecaInsumo = [];
        public IReadOnlyCollection<ItemPecaInsumo> ItensPecaInsumo => _itensPecaInsumo.AsReadOnly();

        private readonly List<EventoDominio> _eventos = [];
        public IReadOnlyCollection<EventoDominio> Eventos => _eventos.AsReadOnly();

        public OrdemServico(Guid idCliente, Guid idVeiculo, string clienteNome, string? clienteEmail, string? observacao = null)
        {
            Id = Guid.NewGuid();
            IdCliente = idCliente;
            IdVeiculo = idVeiculo;
            ClienteNome = clienteNome;
            ClienteEmail = clienteEmail;
            Observacao = observacao;
            Status = StatusOS.Recebida;
            DataAbertura = DateTime.UtcNow;
            CriadoEm = DateTime.UtcNow;
            AtualizadoEm = DateTime.UtcNow;

            _eventos.Add(new OrdemServicoCriada(Id, IdCliente, IdVeiculo, Status.Codigo));
        }

        private OrdemServico()
        {
            Status = StatusOS.Recebida;
        }

        internal static OrdemServico Restaurar(
            Guid id, Guid idCliente, Guid idVeiculo, string clienteNome, string? clienteEmail,
            StatusOS status, string? observacao, DateTime dataAbertura, DateTime? dataInicioDiagnostico,
            DateTime? dataConclusao, decimal? valorOrcamento, DateTime criadoEm, DateTime atualizadoEm, long versao,
            IEnumerable<ItemServico> itensServico, IEnumerable<ItemPecaInsumo> itensPecaInsumo)
        {
            var ordemServico = new OrdemServico
            {
                Id = id,
                IdCliente = idCliente,
                IdVeiculo = idVeiculo,
                ClienteNome = clienteNome,
                ClienteEmail = clienteEmail,
                Status = status,
                Observacao = observacao,
                DataAbertura = dataAbertura,
                DataInicioDiagnostico = dataInicioDiagnostico,
                DataConclusao = dataConclusao,
                ValorOrcamento = valorOrcamento,
                CriadoEm = criadoEm,
                AtualizadoEm = atualizadoEm,
                Versao = versao
            };

            ordemServico._itensServico.AddRange(itensServico);
            ordemServico._itensPecaInsumo.AddRange(itensPecaInsumo);
            return ordemServico;
        }

        public void IniciarDiagnostico()
        {
            DataInicioDiagnostico ??= DateTime.UtcNow;
        }

        public void AlterarStatus(StatusOS status)
        {
            StatusOS statusAnterior = Status;
            Status = status;
            AtualizarTimestamp();

            if (statusAnterior != status)
                _eventos.Add(new StatusOrdemServicoAlterado(Id, statusAnterior.Codigo, status.Codigo));
        }

        public void Concluir()
        {
            DataConclusao = DateTime.UtcNow;
            AtualizarTimestamp();
        }

        public void ReprovarOrcamento()
        {
            AlterarStatus(StatusOS.OrcamentoReprovado);
            Concluir();
            _eventos.Add(new OrcamentoReprovado(Id, [.. _itensPecaInsumo.Select(item => item.IdPecaInsumo)]));
        }

        /// <summary>
        /// Cancela a OS (transação compensatória). OS já canceladas, reprovadas ou entregues não
        /// são canceladas: nos dois primeiros casos o estoque já foi estornado, no último o
        /// veículo já saiu da oficina.
        /// </summary>
        public bool Cancelar(string motivo)
        {
            if (Status == StatusOS.Cancelada || Status == StatusOS.OrcamentoReprovado || Status == StatusOS.Entregue)
                return false;

            AlterarStatus(StatusOS.Cancelada);
            Concluir();
            _eventos.Add(new OrdemServicoCancelada(Id, motivo, [.. _itensPecaInsumo.Select(item => item.IdPecaInsumo)]));
            return true;
        }

        public bool RemoverItemServico(Guid idServico)
        {
            int itensRemovidos = _itensServico.RemoveAll(item => item.IdServico == idServico);

            if (itensRemovidos > 0)
                AtualizarTimestamp();

            return itensRemovidos > 0;
        }

        public bool RemoverItemPecaInsumo(Guid idPecaInsumo)
        {
            int itensRemovidos = _itensPecaInsumo.RemoveAll(item => item.IdPecaInsumo == idPecaInsumo);

            if (itensRemovidos > 0)
                AtualizarTimestamp();

            return itensRemovidos > 0;
        }

        /// <summary>
        /// Equivalente ao SincronizarItens do monólito: adiciona os itens que ainda não existem,
        /// atualiza o snapshot dos existentes e recalcula o orçamento.
        /// </summary>
        public void SincronizarItens(IReadOnlyCollection<ItemServico> servicos, IReadOnlyCollection<ItemPecaInsumo> pecasInsumo)
        {
            foreach (ItemServico servico in servicos)
            {
                ItemServico? existente = _itensServico.FirstOrDefault(item => item.IdServico == servico.IdServico);

                if (existente == null)
                    _itensServico.Add(servico);
                else
                    existente.AtualizarCatalogo(servico.Nome, servico.ValorUnitario, servico.TempoEstimadoMinutos);
            }

            foreach (ItemPecaInsumo peca in pecasInsumo)
            {
                ItemPecaInsumo? existente = _itensPecaInsumo.FirstOrDefault(item => item.IdPecaInsumo == peca.IdPecaInsumo);

                if (existente == null)
                    _itensPecaInsumo.Add(peca);
                else
                    existente.AtualizarCatalogo(peca.Descricao, peca.ValorUnitario);
            }

            RecalcularOrcamento();
        }

        public void RecalcularOrcamento()
        {
            ValorOrcamento = Math.Round(
                _itensServico.Sum(item => item.ValorUnitario) + _itensPecaInsumo.Sum(item => item.ValorUnitario), 2);
            AtualizarTimestamp();
        }

        public void RegistrarEvento(EventoDominio evento)
        {
            _eventos.Add(evento);
        }

        public void LimparEventos()
        {
            _eventos.Clear();
        }

        internal void DefinirVersao(long versao)
        {
            Versao = versao;
        }

        private void AtualizarTimestamp()
        {
            AtualizadoEm = DateTime.UtcNow;
        }
    }
}
