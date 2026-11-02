using OrdemServico.Lambda.Application.DTOs.Requests;
using OrdemServico.Lambda.Application.Services;

namespace OrdemServico.Lambda.Api
{
    /// <summary>
    /// Mesmas rotas, verbos, payloads, status HTTP e regras de autorização do
    /// OrdemServicoController do monólito. Cada rota tem uma Route correspondente no
    /// API Gateway (infra/template.yaml).
    /// </summary>
    public static class OrdemServicoEndpoints
    {
        private const string Administrador = "Administrador";
        private const string Mecanico = "Mecanico";

        public static IEndpointRouteBuilder MapOrdemServicoEndpoints(this IEndpointRouteBuilder app)
        {
            RouteGroupBuilder group = app.MapGroup("/api/v1/ordens-servico");

            group.MapPost("", async (OrdemServicoRequest request, IOrdemServicoService service) =>
            {
                var result = await service.Criar(request);
                return Results.Json(new { id = result.Id, clienteNotificado = result.ClienteNotificado }, statusCode: StatusCodes.Status201Created);
            }).RequireAuthorization(Admin);

            group.MapPost("{id:guid}/pecas-insumos", async (Guid id, OrdemServicoPecaInsumoRequest request, IOrdemServicoService service) =>
                Results.Ok(await service.IncluirPecaInsumo(id, request))).RequireAuthorization(AdminOuMecanico);

            group.MapPost("{id:guid}/servicos", async (Guid id, OrdemServicoServicosRequest request, IOrdemServicoService service) =>
                Results.Ok(await service.IncluirServico(id, request))).RequireAuthorization(AdminOuMecanico);

            group.MapPatch("{id:guid}/iniciar-diagnostico", async (Guid id, IOrdemServicoService service) =>
                Results.Ok(await service.IniciarDiagnostico(id))).RequireAuthorization(AdminOuMecanico);

            group.MapPatch("{id:guid}/finalizar-diagnostico", async (Guid id, IOrdemServicoService service) =>
                Results.Ok(await service.FinalizarDiagnostico(id))).RequireAuthorization(AdminOuMecanico);

            // Sem [Authorize] no monólito: o cliente aprova o orçamento. "aprovado" vem da query string.
            group.MapPatch("{id:guid}/aprovar-orcamento", async (Guid id, bool? aprovado, IOrdemServicoService service) =>
                Results.Ok(await service.AprovarOrcamento(id, aprovado ?? false)));

            group.MapPatch("{id:guid}/confirmar-entrega", async (Guid id, IOrdemServicoService service) =>
                Results.Ok(await service.ConfirmarEntrega(id)));

            group.MapDelete("{id:guid}/servicos/{idServico:guid}", async (Guid id, Guid idServico, IOrdemServicoService service) =>
            {
                await service.RemoverItemServico(id, idServico);
                return Results.NoContent();
            }).RequireAuthorization(AdminOuMecanico);

            group.MapDelete("{id:guid}/pecas-insumos/{idPecaInsumo:guid}", async (Guid id, Guid idPecaInsumo, IOrdemServicoService service) =>
            {
                await service.RemoverItemPecaInsumo(id, idPecaInsumo);
                return Results.NoContent();
            }).RequireAuthorization(AdminOuMecanico);

            group.MapPatch("{id:guid}/servicos/{idServico:guid}/iniciar", async (Guid id, Guid idServico, IOrdemServicoService service) =>
            {
                await service.IniciarServico(id, idServico);
                return Results.NoContent();
            }).RequireAuthorization(AdminOuMecanico);

            group.MapPatch("{id:guid}/servicos/{idServico:guid}/finalizar", async (Guid id, Guid idServico, IOrdemServicoService service) =>
            {
                await service.FinalizarServico(id, idServico);
                return Results.NoContent();
            }).RequireAuthorization(AdminOuMecanico);

            group.MapGet("{id:guid}", async (Guid id, IOrdemServicoService service) =>
                Results.Ok(await service.ObterPorId(id))).RequireAuthorization(AdminOuMecanico);

            // Sem [Authorize] no monólito: acompanhamento pelo cliente.
            group.MapGet("{id:guid}/progresso", async (Guid id, IOrdemServicoService service) =>
                Results.Ok(await service.ObterProgresso(id)));

            group.MapGet("", async (IOrdemServicoService service) =>
                Results.Ok(await service.ObterTodos())).RequireAuthorization(AdminOuMecanico);

            return app;
        }

        private static void Admin(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy)
        {
            policy.RequireRole(Administrador);
        }

        private static void AdminOuMecanico(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy)
        {
            policy.RequireRole(Administrador, Mecanico);
        }
    }
}
