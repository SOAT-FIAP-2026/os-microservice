using System.Text.Json.Serialization;

namespace OrdemServico.Lambda.Application.Saga
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TipoCompensacao
    {
        /// <summary>Devolve ao estoque as peças baixadas (saga falhou, orçamento reprovado ou OS cancelada).</summary>
        EstornarEstoque,

        /// <summary>Remove da OS peças cuja reserva de estoque falhou e recalcula o orçamento.</summary>
        RemoverPecaInsumo,

        /// <summary>Cancela a OS (ex.: pagamento recusado). Dispara OrdemServicoCancelada, que estorna o estoque.</summary>
        CancelarOrdemServico
    }

    /// <summary>
    /// Comando de compensação. É o corpo das mensagens da fila SQS e também o formato para o
    /// qual os eventos do SNS são traduzidos antes de executar a compensação.
    /// </summary>
    public sealed record ComandoCompensacao(
        string IdCompensacao,
        TipoCompensacao Tipo,
        Guid IdOrdemServico,
        IReadOnlyCollection<Guid> PecasInsumosIds,
        string Motivo,
        Guid? IdSaga = null);
}
