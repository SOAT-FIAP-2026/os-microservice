namespace OrdemServico.Lambda.Application.Saga
{
    /// <summary>
    /// Estado de execução de uma compensação, usado para idempotência. Guarda as peças já
    /// estornadas para que uma reentrega da mesma mensagem não devolva a peça duas vezes.
    /// </summary>
    public sealed class RegistroCompensacao
    {
        public string IdCompensacao { get; init; } = string.Empty;
        public TipoCompensacao Tipo { get; init; }
        public Guid IdOrdemServico { get; init; }
        public string Origem { get; init; } = string.Empty;
        public List<Guid> PecasEstornadas { get; init; } = [];
        public bool Concluida { get; set; }
        public DateTime CriadoEm { get; init; } = DateTime.UtcNow;
        public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

        public void MarcarPecaEstornada(Guid idPecaInsumo)
        {
            PecasEstornadas.Add(idPecaInsumo);
            AtualizadoEm = DateTime.UtcNow;
        }

        public void Concluir()
        {
            Concluida = true;
            AtualizadoEm = DateTime.UtcNow;
        }
    }
}
