using Fiap.TechChallenge.Contracts.OsService;
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
            RouteGroupBuilder group = app.MapGroup($"/{OrdemServicoApiRoutes.Base}")
                .WithTags("Ordens de Serviço");

            group.MapPost("", async (OrdemServicoRequest request, IOrdemServicoService service) =>
            {
                var result = await service.Criar(request);
                return Results.Json(new { id = result.Id, clienteNotificado = result.ClienteNotificado }, statusCode: StatusCodes.Status201Created);
            })
            .WithName("CriarOrdemServico")
            .WithSummary("Cria uma nova ordem de serviço")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Admin);

            group.MapPost("{id:guid}/pecas-insumos", async (Guid id, OrdemServicoPecaInsumoRequest request, IOrdemServicoService service) =>
                Results.Ok(await service.IncluirPecaInsumo(id, request)))
            .WithName("IncluirPecaInsumo")
            .WithSummary("Inclui peça ou insumo na ordem de serviço")
            .Produces<OrdemServicoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AdminOuMecanico);

            group.MapPost("{id:guid}/servicos", async (Guid id, OrdemServicoServicosRequest request, IOrdemServicoService service) =>
                Results.Ok(await service.IncluirServico(id, request)))
            .WithName("IncluirServico")
            .WithSummary("Inclui serviço na ordem de serviço")
            .Produces<OrdemServicoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AdminOuMecanico);

            group.MapPatch("{id:guid}/iniciar-diagnostico", async (Guid id, IOrdemServicoService service) =>
                Results.Ok(await service.IniciarDiagnostico(id)))
            .WithName("IniciarDiagnostico")
            .WithSummary("Inicia o diagnóstico técnico da ordem de serviço")
            .Produces<OrdemServicoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AdminOuMecanico);

            group.MapPatch("{id:guid}/finalizar-diagnostico", async (Guid id, IOrdemServicoService service) =>
                Results.Ok(await service.FinalizarDiagnostico(id)))
            .WithName("FinalizarDiagnostico")
            .WithSummary("Finaliza o diagnóstico técnico e consolida os valores")
            .Produces<OrdemServicoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AdminOuMecanico);

            // Sem [Authorize] no monólito: o cliente aprova o orçamento. "aprovado" vem da query string.
            group.MapPatch("{id:guid}/aprovar-orcamento", async (Guid id, bool? aprovado, IOrdemServicoService service) =>
                Results.Ok(await service.AprovarOrcamento(id, aprovado ?? false)))
            .WithName("AprovarOrcamento")
            .WithSummary("Cliente aprova ou recusa o orçamento")
            .Produces<OrdemServicoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

            group.MapPatch("{id:guid}/confirmar-entrega", async (Guid id, IOrdemServicoService service) =>
                Results.Ok(await service.ConfirmarEntrega(id)))
            .WithName("ConfirmarEntrega")
            .WithSummary("Confirma a entrega do veículo ao cliente")
            .Produces<OrdemServicoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

            group.MapDelete("{id:guid}/servicos/{idServico:guid}", async (Guid id, Guid idServico, IOrdemServicoService service) =>
            {
                await service.RemoverItemServico(id, idServico);
                return Results.NoContent();
            })
            .WithName("RemoverItemServico")
            .WithSummary("Remove um serviço da ordem de serviço")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AdminOuMecanico);

            group.MapDelete("{id:guid}/pecas-insumos/{idPecaInsumo:guid}", async (Guid id, Guid idPecaInsumo, IOrdemServicoService service) =>
            {
                await service.RemoverItemPecaInsumo(id, idPecaInsumo);
                return Results.NoContent();
            })
            .WithName("RemoverItemPecaInsumo")
            .WithSummary("Remove uma peça/insumo da ordem de serviço")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AdminOuMecanico);

            group.MapPatch("{id:guid}/servicos/{idServico:guid}/iniciar", async (Guid id, Guid idServico, IOrdemServicoService service) =>
            {
                await service.IniciarServico(id, idServico);
                return Results.NoContent();
            })
            .WithName("IniciarServico")
            .WithSummary("Inicia a execução de um serviço específico")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AdminOuMecanico);

            group.MapPatch("{id:guid}/servicos/{idServico:guid}/finalizar", async (Guid id, Guid idServico, IOrdemServicoService service) =>
            {
                await service.FinalizarServico(id, idServico);
                return Results.NoContent();
            })
            .WithName("FinalizarServico")
            .WithSummary("Finaliza a execução de um serviço específico")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AdminOuMecanico);

            group.MapGet("{id:guid}", async (Guid id, IOrdemServicoService service) =>
                Results.Ok(await service.ObterPorId(id)))
            .WithName("ObterOrdemServicoPorId")
            .WithSummary("Obtém os detalhes da ordem de serviço por ID")
            .Produces<OrdemServicoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AdminOuMecanico);

            // Sem [Authorize] no monólito: acompanhamento pelo cliente.
            group.MapGet("{id:guid}/progresso", async (Guid id, IOrdemServicoService service) =>
                Results.Ok(await service.ObterProgresso(id)))
            .WithName("ObterProgressoOrdemServico")
            .WithSummary("Consulta pública de progresso da ordem de serviço pelo cliente")
            .Produces<OrdemServicoProgressoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

            group.MapGet("", async (IOrdemServicoService service) =>
                Results.Ok(await service.ObterTodos()))
            .WithName("ObterTodasOrdensServico")
            .WithSummary("Lista todas as ordens de serviço cadastradas")
            .Produces<IReadOnlyCollection<OrdemServicoResponse>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization(AdminOuMecanico);

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
