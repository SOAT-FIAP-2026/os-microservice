namespace OrdemServico.Lambda.Domain.Entities
{
    /// <summary>
    /// Catálogo de status da Ordem de Serviço.
    ///
    /// No monólito os status vivem na tabela StatusOrdensServico (seed). Aqui o catálogo é
    /// fixo em código, preservando os mesmos Ids do seed para que o campo idStatus do
    /// payload continue idêntico entre as duas implementações.
    /// </summary>
    public sealed record StatusOS
    {
        public Guid Id { get; }
        public string Codigo { get; }
        public string Descricao { get; }

        private StatusOS(Guid id, string codigo, string descricao)
        {
            Id = id;
            Codigo = codigo;
            Descricao = descricao;
        }

        public static readonly StatusOS Recebida = new(Guid.Parse("c3d4e5f6-a7b8-9012-cdef-123456789012"), "RECEBIDA", "Recebida");
        public static readonly StatusOS EmDiagnostico = new(Guid.Parse("d3e4f5a6-b7c8-9012-defa-234567890123"), "EM_DIAGNOSTICO", "Em Diagnóstico");
        public static readonly StatusOS AguardandoAprovacao = new(Guid.Parse("e3f4a5b6-c7d8-9012-efab-345678901234"), "AGUARDANDO_APROVACAO", "Aguardando aprovação");
        public static readonly StatusOS OrcamentoAprovado = new(Guid.Parse("a7b8c9d0-e1f2-3456-abcd-567890123456"), "ORCAMENTO_APROVADO", "Orçamento Aprovado");
        public static readonly StatusOS OrcamentoReprovado = new(Guid.Parse("b8c9d0e1-f2a3-4567-bcde-678901234567"), "ORCAMENTO_REPROVADO", "Orçamento Reprovado");
        public static readonly StatusOS EmExecucao = new(Guid.Parse("f4a5b6c7-d8e9-0123-efab-345678901234"), "EM_EXECUCAO", "Em Execução");
        public static readonly StatusOS Finalizada = new(Guid.Parse("d4e5f6a7-b8c9-0123-defa-234567890123"), "FINALIZADA", "Finalizada");
        public static readonly StatusOS Entregue = new(Guid.Parse("e5f6a7b8-c9d0-1234-efab-345678901234"), "ENTREGUE", "Entregue");
        public static readonly StatusOS Cancelada = new(Guid.Parse("f6a7b8c9-d0e1-2345-fabc-456789012345"), "CANCELADA", "Cancelada");

        public static IReadOnlyCollection<StatusOS> Todos { get; } =
        [
            Recebida, EmDiagnostico, AguardandoAprovacao, OrcamentoAprovado, OrcamentoReprovado,
            EmExecucao, Finalizada, Entregue, Cancelada
        ];

        public static StatusOS ObterPorCodigo(string codigo)
        {
            return Todos.FirstOrDefault(status => status.Codigo == codigo)
                ?? throw new Exceptions.StatusOrdemServicoNaoEncontradoException(codigo);
        }
    }
}
