namespace OrdemServico.Lambda.Domain.Exceptions
{
    /// <summary>
    /// Exceção de domínio base para todas as exceptions de negócio.
    /// Mesmas mensagens do monólito para manter o payload de erro idêntico.
    /// </summary>
    public abstract class DomainException(string message) : Exception(message)
    {
    }

    public class ServicoNaoEncontradoException(Guid id) : DomainException($"Serviço com id '{id}' não encontrado.")
    {
    }

    public class ItemServicoNaoEncontradoException(Guid idServico) : DomainException($"Item de serviço com id '{idServico}' não encontrado na ordem de serviço.")
    {
    }

    public class ClienteNaoEncontradoException(Guid id) : DomainException($"Cliente com id '{id}' não encontrado.")
    {
    }

    public class VeiculoNaoEncontradoException(Guid id) : DomainException($"Veículo com id '{id}' não encontrado.")
    {
    }

    public class StatusOrdemServicoNaoEncontradoException(string descricao) : DomainException($"Status de ordem de serviço '{descricao}' não encontrado.")
    {
    }

    public class PecaInsumoNaoEncontradaException(Guid id) : DomainException($"Peça/Insumo com id '{id}' não encontrado.")
    {
    }

    public class EstoqueNaoEncontradoException(Guid idPecaInsumo) : DomainException($"Estoque da peça/insumo com id '{idPecaInsumo}' não encontrado.")
    {
    }

    public class ItemPecaInsumoNaoEncontradoException(Guid idPecaInsumo) : DomainException($"Item de peça/insumo com id '{idPecaInsumo}' não encontrado na ordem de serviço.")
    {
    }

    public class OrdemServicoNaoEncontradaException(Guid id) : DomainException($"Ordem de serviço com id '{id}' não encontrada.")
    {
    }

    /// <summary>
    /// Outra requisição alterou a OS entre a leitura e a gravação (concorrência otimista).
    /// </summary>
    public class ConcorrenciaOrdemServicoException(Guid id) : DomainException($"A ordem de serviço com id '{id}' foi alterada por outra operação. Tente novamente.")
    {
    }

    /// <summary>
    /// Regra de negócio rejeitada por um serviço externo (ex.: API core respondeu 400).
    /// </summary>
    public class IntegracaoNegocioException(string message) : DomainException(message)
    {
    }
}
