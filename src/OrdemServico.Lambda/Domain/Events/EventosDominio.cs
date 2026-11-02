namespace OrdemServico.Lambda.Domain.Events
{
    /// <summary>
    /// Evento de domínio publicado no SNS (via outbox no DynamoDB).
    /// O nome do tipo concreto vira o atributo de mensagem tipoEvento.
    /// </summary>
    public abstract record EventoDominio(Guid IdOrdemServico)
    {
        public Guid IdEvento { get; init; } = Guid.NewGuid();
        public DateTime OcorridoEm { get; init; } = DateTime.UtcNow;
        public string TipoEvento => GetType().Name;
    }

    public sealed record OrdemServicoCriada(Guid IdOrdemServico, Guid IdCliente, Guid IdVeiculo, string Status)
        : EventoDominio(IdOrdemServico);

    public sealed record StatusOrdemServicoAlterado(Guid IdOrdemServico, string StatusAnterior, string StatusAtual)
        : EventoDominio(IdOrdemServico);

    public sealed record PecasInsumoIncluidas(Guid IdOrdemServico, Guid IdSaga, IReadOnlyCollection<Guid> PecasInsumosIds)
        : EventoDominio(IdOrdemServico);

    /// <summary>Gatilho de compensação: as peças baixadas do estoque precisam ser devolvidas.</summary>
    public sealed record OrcamentoReprovado(Guid IdOrdemServico, IReadOnlyCollection<Guid> PecasInsumosIds)
        : EventoDominio(IdOrdemServico);

    /// <summary>Gatilho de compensação: a OS foi cancelada e as peças precisam ser devolvidas.</summary>
    public sealed record OrdemServicoCancelada(Guid IdOrdemServico, string Motivo, IReadOnlyCollection<Guid> PecasInsumosIds)
        : EventoDominio(IdOrdemServico);

    public sealed record CompensacaoExecutada(Guid IdOrdemServico, string IdCompensacao, string Tipo, string Origem)
        : EventoDominio(IdOrdemServico);
}
