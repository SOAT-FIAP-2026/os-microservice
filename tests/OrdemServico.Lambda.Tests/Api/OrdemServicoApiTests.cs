using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Fiap.TechChallenge.Contracts.OsService;
using OrdemServico.Lambda.Application.Services;
using OrdemServico.Lambda.Domain.Exceptions;
using OrdemServico.Lambda.Infrastructure.Security;

namespace OrdemServico.Lambda.Tests.Api
{
    /// <summary>Contrato HTTP: rotas, autorização, status e formato JSON iguais aos do monólito.</summary>
    public class OrdemServicoApiTests : IClassFixture<OrdemServicoApiTests.Factory>
    {
        private const string Secret = "chave-de-teste-com-pelo-menos-32-caracteres!!";
        private readonly Factory _factory;

        public OrdemServicoApiTests(Factory factory)
        {
            _factory = factory;
            _factory.Service.Reset();
        }

        public class Factory : WebApplicationFactory<Program>
        {
            public Mock<IOrdemServicoService> Service { get; } = new();

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<JwtSettings>();
                    services.AddSingleton(new JwtSettings(Secret, "teste", "teste"));
                    services.RemoveAll<IOrdemServicoService>();
                    services.AddScoped(_ => Service.Object);
                });
            }
        }

        private HttpClient Client(string? role = "Administrador")
        {
            HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            if (role == null)
                return client;

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([new Claim(ClaimTypes.Role, role)]),
                Expires = DateTime.UtcNow.AddMinutes(5),
                Issuer = "teste",
                Audience = "teste",
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256Signature)
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", handler.WriteToken(token));
            return client;
        }

        [Fact]
        public async Task Post_DeveRetornar201ComIdEClienteNotificado()
        {
            var id = Guid.NewGuid();
            _factory.Service.Setup(s => s.Criar(It.IsAny<OrdemServicoRequest>())).ReturnsAsync((id, true));

            HttpResponseMessage response = await Client().PostAsJsonAsync("/api/v1/ordens-servico",
                new { clienteId = Guid.NewGuid(), veiculoId = Guid.NewGuid(), observacao = "x" });

            response.StatusCode.Should().Be(HttpStatusCode.Created);
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("id").GetGuid().Should().Be(id);
            body.GetProperty("clienteNotificado").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task Post_SemToken_DeveRetornar401()
        {
            HttpResponseMessage response = await Client(role: null).PostAsJsonAsync("/api/v1/ordens-servico", new { });

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Get_ComRoleDiferente_DeveRetornar403()
        {
            HttpResponseMessage response = await Client(role: "Cliente").GetAsync("/api/v1/ordens-servico");

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task AprovarOrcamento_DeveSerPublicoELerAprovadoDaQuery()
        {
            var id = Guid.NewGuid();
            _factory.Service.Setup(s => s.AprovarOrcamento(id, true)).ReturnsAsync(
                new OrdemServicoResponse(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Joao", "Orçamento Aprovado", null,
                    DateTime.UtcNow, null, 10m, [], []) { ClienteNotificado = true });

            HttpResponseMessage response = await Client(role: null).PatchAsync($"/api/v1/ordens-servico/{id}/aprovar-orcamento?aprovado=true", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("statusDescricao").GetString().Should().Be("Orçamento Aprovado");
            body.GetProperty("valorTotal").GetDecimal().Should().Be(10m);
            body.GetProperty("itensServico").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task ErroDeDominio_DeveRetornar400NoFormatoDoMonolito()
        {
            var id = Guid.NewGuid();
            _factory.Service.Setup(s => s.ObterPorId(id)).ThrowsAsync(new OrdemServicoNaoEncontradaException(id));

            HttpResponseMessage response = await Client().GetAsync($"/api/v1/ordens-servico/{id}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("statusCode").GetInt32().Should().Be(400);
            body.GetProperty("message").GetString().Should().Be($"Ordem de serviço com id '{id}' não encontrada.");
            body.TryGetProperty("traceId", out _).Should().BeTrue();
            response.Headers.Contains("X-Correlation-ID").Should().BeTrue();
        }

        [Fact]
        public async Task DeleteServico_DeveRetornar204()
        {
            HttpResponseMessage response = await Client().DeleteAsync($"/api/v1/ordens-servico/{Guid.NewGuid()}/servicos/{Guid.NewGuid()}");

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        [Fact]
        public async Task SwaggerEndpoint_DeveRetornar200EConterEspecificacaoOpenApi()
        {
            HttpResponseMessage response = await Client(role: null).GetAsync("/swagger/v1/swagger.json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            string json = await response.Content.ReadAsStringAsync();
            json.Should().Contain("Ordem de Serviço Microservice");
            json.Should().Contain("/api/v1/ordens-servico");
        }

        [Fact]
        public async Task SwaggerUI_DeveRetornar200OK()
        {
            HttpResponseMessage response = await Client(role: null).GetAsync("/swagger/index.html");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            string html = await response.Content.ReadAsStringAsync();
            html.Should().Contain("swagger-ui");
        }
    }
}
