namespace OrdemServico.Lambda.Domain.Entities
{
    /// <summary>
    /// Item de serviço da OS. Guarda um snapshot (nome, valor e tempo estimado) do serviço
    /// no momento da inclusão: no DynamoDB o agregado é um único documento e não há join
    /// com o catálogo de serviços, que pertence a outro domínio.
    /// </summary>
    public class ItemServico
    {
        public Guid IdServico { get; private set; }
        public string Nome { get; private set; }
        public decimal ValorUnitario { get; private set; }
        public int TempoEstimadoMinutos { get; private set; }
        public DateTime? DataHoraInicio { get; private set; }
        public DateTime? DataHoraFim { get; private set; }

        public ItemServico(Guid idServico, string nome, decimal valorUnitario, int tempoEstimadoMinutos)
        {
            IdServico = idServico;
            Nome = nome;
            ValorUnitario = valorUnitario;
            TempoEstimadoMinutos = tempoEstimadoMinutos;
        }

        internal static ItemServico Restaurar(
            Guid idServico, string nome, decimal valorUnitario, int tempoEstimadoMinutos,
            DateTime? dataHoraInicio, DateTime? dataHoraFim)
        {
            return new ItemServico(idServico, nome, valorUnitario, tempoEstimadoMinutos)
            {
                DataHoraInicio = dataHoraInicio,
                DataHoraFim = dataHoraFim
            };
        }

        public void AtualizarCatalogo(string nome, decimal valorUnitario, int tempoEstimadoMinutos)
        {
            Nome = nome;
            ValorUnitario = valorUnitario;
            TempoEstimadoMinutos = tempoEstimadoMinutos;
        }

        public void IniciarServico()
        {
            if (DataHoraInicio != null)
                return;

            DataHoraInicio = DateTime.UtcNow;
        }

        public void FinalizarServico()
        {
            if (DataHoraInicio == null)
                throw new InvalidOperationException("O serviço precisa ser iniciado antes de ser finalizado.");

            DataHoraFim = DateTime.UtcNow;
        }

        public int? ObterTempoExecutadoMinutos()
        {
            if (DataHoraInicio == null)
                return null;

            DateTime dataFim = DataHoraFim ?? DateTime.UtcNow;
            return Math.Max(0, (int)Math.Round((dataFim - DataHoraInicio.Value).TotalMinutes));
        }
    }
}
