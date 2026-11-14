using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fiap.TechChallenge.Contracts.CoreService;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Configuration;
using OrdemServico.Lambda.Domain.Entities;
using OrdemServico.Lambda.Domain.Exceptions;

namespace OrdemServico.Lambda.Infrastructure.Http
{
    /// <summary>
    /// Cliente da API core (monólito em EKS). Enquanto Cliente, Veículo, Serviço, Peça e
    /// Estoque não forem extraídos, a Lambda consulta e altera esses domínios por HTTP.
    /// </summary>
    public class CoreApiClient(HttpClient httpClient, ICoreApiAuthorization authorization) : ICatalogoGateway, IEstoqueGateway
    {
        private readonly HttpClient _httpClient = httpClient;
        private readonly ICoreApiAuthorization _authorization = authorization;

        public async Task<ClienteResumo?> ObterCliente(Guid idCliente)
        {
            ClienteCatalogoDto? cliente = await Obter<ClienteCatalogoDto>(string.Format(CoreApiRoutes.ObterClientePorId, idCliente));
            return cliente == null ? null : new ClienteResumo(cliente.Id, cliente.Nome, cliente.Email, cliente.Celular);
        }

        public async Task<bool> VeiculoExiste(Guid idVeiculo)
        {
            return await Obter<VeiculoCatalogoDto>(string.Format(CoreApiRoutes.ObterVeiculoPorId, idVeiculo)) != null;
        }

        public async Task<VeiculoResumo?> ObterVeiculo(Guid idVeiculo)
        {
            VeiculoCatalogoDto? veiculo = await Obter<VeiculoCatalogoDto>(string.Format(CoreApiRoutes.ObterVeiculoPorId, idVeiculo));
            return veiculo == null ? null : new VeiculoResumo(veiculo.Id, veiculo.Placa ?? "SEM-PLACA", veiculo.Modelo ?? "Modelo Indefinido", veiculo.Ano > 0 ? veiculo.Ano : 2022);
        }

        public async Task<IReadOnlyCollection<ItemServico>> ObterServicos(IReadOnlyCollection<Guid> idsServicos)
        {
            ServicoCatalogoDto?[] servicos = await Task.WhenAll(idsServicos.Distinct().Select(id => Obter<ServicoCatalogoDto>(string.Format(CoreApiRoutes.ObterServicoPorId, id))));

            return [.. servicos
                .Where(servico => servico != null)
                .Select(servico => new ItemServico(servico!.Id, servico.Nome, servico.ValorUnitario, servico.TempoEstimadoMinutos))];
        }

        public async Task<IReadOnlyCollection<ItemPecaInsumo>> ObterPecasInsumo(IReadOnlyCollection<Guid> idsPecasInsumo)
        {
            PecaInsumoCatalogoDto?[] pecas = await Task.WhenAll(idsPecasInsumo.Distinct().Select(id => Obter<PecaInsumoCatalogoDto>(string.Format(CoreApiRoutes.ObterPecaInsumoPorId, id))));

            return [.. pecas
                .Where(peca => peca != null)
                .Select(peca => new ItemPecaInsumo(peca!.Id, peca.Descricao, peca.ValorUnitario))];
        }

        public Task BaixarEstoque(Guid idPecaInsumo, int quantidade)
        {
            return AlterarQuantidade(string.Format(CoreApiRoutes.RemoverQuantidadeEstoque, idPecaInsumo), quantidade);
        }

        public Task EstornarEstoque(Guid idPecaInsumo, int quantidade)
        {
            return AlterarQuantidade(string.Format(CoreApiRoutes.AdicionarQuantidadeEstoque, idPecaInsumo), quantidade);
        }

        /// <summary>
        /// GET por id. A API core responde 400 (DomainException "não encontrado") ou 404
        /// quando o recurso não existe; nos dois casos retorna null e o service lança a
        /// mesma exceção de domínio que o monólito lançaria.
        /// </summary>
        private async Task<T?> Obter<T>(string path) where T : class
        {
            using HttpRequestMessage request = CriarRequest(HttpMethod.Get, path);
            using HttpResponseMessage response = await _httpClient.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                return null;

            GarantirAutorizado(response);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options);
        }

        private async Task AlterarQuantidade(string path, int quantidade)
        {
            using HttpRequestMessage request = CriarRequest(HttpMethod.Put, path);
            request.Content = JsonContent.Create(quantidade);

            using HttpResponseMessage response = await _httpClient.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                ErroDto? erro = await LerErro(response);
                throw new IntegracaoNegocioException(erro?.Message ?? "Estoque insuficiente.");
            }

            GarantirAutorizado(response);
            response.EnsureSuccessStatusCode();
        }

        private HttpRequestMessage CriarRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(_authorization.ObterAuthorizationHeader());

            string? correlationId = _authorization.ObterCorrelationId();
            if (!string.IsNullOrWhiteSpace(correlationId))
                request.Headers.TryAddWithoutValidation(CoreApiAuthorization.CorrelationHeader, correlationId);

            return request;
        }

        private static void GarantirAutorizado(HttpResponseMessage response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException($"A API core recusou a credencial ({(int)response.StatusCode}).");
        }

        private static async Task<ErroDto?> LerErro(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadFromJsonAsync<ErroDto>(JsonDefaults.Options);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private sealed record ErroDto(int StatusCode, string Message);
    }
}
