using System.Text.Json.Serialization;

namespace OrdemServico.Lambda.Application.Saga
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum StatusSaga
    {
        Iniciada,
        Concluida,
        Compensando,
        Compensada
    }

    /// <summary>
    /// Log da saga de inclusão de peças/insumos. Cada baixa de estoque confirmada é registrada
    /// para que, em caso de falha, apenas as peças efetivamente baixadas sejam estornadas.
    /// </summary>
    public sealed class SagaReservaEstoque
    {
        public Guid IdSaga { get; init; } = Guid.NewGuid();
        public Guid IdOrdemServico { get; init; }
        public List<Guid> PecasSolicitadas { get; init; } = [];
        public List<Guid> PecasReservadas { get; init; } = [];
        public StatusSaga Status { get; set; } = StatusSaga.Iniciada;
        public string? MotivoFalha { get; set; }
        public DateTime CriadoEm { get; init; } = DateTime.UtcNow;
        public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

        public static SagaReservaEstoque Iniciar(Guid idOrdemServico, IEnumerable<Guid> pecas)
        {
            return new SagaReservaEstoque
            {
                IdOrdemServico = idOrdemServico,
                PecasSolicitadas = [.. pecas]
            };
        }

        public void RegistrarReserva(Guid idPecaInsumo)
        {
            PecasReservadas.Add(idPecaInsumo);
            AtualizadoEm = DateTime.UtcNow;
        }

        public void Concluir()
        {
            Status = StatusSaga.Concluida;
            AtualizadoEm = DateTime.UtcNow;
        }

        public void IniciarCompensacao(string motivo)
        {
            Status = StatusSaga.Compensando;
            MotivoFalha = motivo;
            AtualizadoEm = DateTime.UtcNow;
        }

        public void MarcarCompensada()
        {
            Status = StatusSaga.Compensada;
            AtualizadoEm = DateTime.UtcNow;
        }
    }
}
