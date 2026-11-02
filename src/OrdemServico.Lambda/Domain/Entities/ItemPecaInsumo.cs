namespace OrdemServico.Lambda.Domain.Entities
{
    /// <summary>
    /// Item de peça/insumo da OS com snapshot de descrição e valor do catálogo.
    /// </summary>
    public class ItemPecaInsumo
    {
        public Guid IdPecaInsumo { get; private set; }
        public string Descricao { get; private set; }
        public decimal ValorUnitario { get; private set; }

        public ItemPecaInsumo(Guid idPecaInsumo, string descricao, decimal valorUnitario)
        {
            IdPecaInsumo = idPecaInsumo;
            Descricao = descricao;
            ValorUnitario = valorUnitario;
        }

        public void AtualizarCatalogo(string descricao, decimal valorUnitario)
        {
            Descricao = descricao;
            ValorUnitario = valorUnitario;
        }
    }
}
