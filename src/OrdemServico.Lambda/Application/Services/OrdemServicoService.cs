using System.IO;
using Microsoft.Extensions.Logging;
using OrdemServico.Lambda.Application.DTOs.Requests;
using OrdemServico.Lambda.Application.DTOs.Responses;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Domain.Entities;
using OrdemServico.Lambda.Domain.Events;
using OrdemServico.Lambda.Domain.Exceptions;

namespace OrdemServico.Lambda.Application.Services
{
    public interface IOrdemServicoService
    {
        Task<(Guid Id, bool ClienteNotificado)> Criar(OrdemServicoRequest request);
        Task<OrdemServicoResponse> IniciarDiagnostico(Guid id);
        Task<OrdemServicoResponse> FinalizarDiagnostico(Guid id);
        Task<OrdemServicoResponse> IncluirServico(Guid id, OrdemServicoServicosRequest request);
        Task<OrdemServicoResponse> IncluirPecaInsumo(Guid id, OrdemServicoPecaInsumoRequest request);
        Task RemoverItemServico(Guid id, Guid idServico);
        Task RemoverItemPecaInsumo(Guid id, Guid idPecaInsumo);
        Task IniciarServico(Guid id, Guid idServico);
        Task FinalizarServico(Guid id, Guid idServico);
        Task<OrdemServicoResponse> AprovarOrcamento(Guid id, bool aprovado);
        Task<OrdemServicoResponse> ConfirmarEntrega(Guid id);
        Task<OrdemServicoResponse> ObterPorId(Guid id);
        Task<OrdemServicoProgressoResponse> ObterProgresso(Guid id);
        Task<IReadOnlyCollection<OrdemServicoResponse>> ObterTodos();
    }

    /// <summary>
    /// Migração do Fiap.TechChallenge.Application.Services.OrdemServicoService para Lambda.
    /// As regras e mensagens são as mesmas; mudam as dependências: a OS fica no DynamoDB,
    /// cliente/veículo/serviço/peça são lidos da API core e a baixa de estoque passa a ser
    /// um passo da <see cref="ReservaEstoqueSaga"/>, com compensação.
    /// </summary>
    public class OrdemServicoService(
        IOrdemServicoRepository ordemServicoRepository,
        ICatalogoGateway catalogoGateway,
        ReservaEstoqueSaga reservaEstoqueSaga,
        IEmailService emailService,
        ILogger<OrdemServicoService> logger) : IOrdemServicoService
    {
        private readonly IOrdemServicoRepository _ordemServicoRepository = ordemServicoRepository;
        private readonly ICatalogoGateway _catalogoGateway = catalogoGateway;
        private readonly ReservaEstoqueSaga _reservaEstoqueSaga = reservaEstoqueSaga;
        private readonly IEmailService _emailService = emailService;
        private readonly ILogger<OrdemServicoService> _logger = logger;

        public async Task<(Guid Id, bool ClienteNotificado)> Criar(OrdemServicoRequest request)
        {
            ClienteResumo cliente = await _catalogoGateway.ObterCliente(request.ClienteId)
                ?? throw new ClienteNaoEncontradoException(request.ClienteId);

            if (!await _catalogoGateway.VeiculoExiste(request.VeiculoId))
                throw new VeiculoNaoEncontradoException(request.VeiculoId);

            Domain.Entities.OrdemServico ordemServico = new(request.ClienteId, request.VeiculoId, cliente.Nome, cliente.Email, request.Observacao);

            await _ordemServicoRepository.Adicionar(ordemServico);
            _logger.LogInformation("OS {IdOrdemServico} criada no status {Status}", ordemServico.Id, ordemServico.Status.Codigo);
            bool notificado = await NotificarMudancaStatusAsync(ordemServico);

            return (ordemServico.Id, notificado);
        }

        public async Task<OrdemServicoResponse> IniciarDiagnostico(Guid id)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);

            if (ordemServico.Status != StatusOS.Recebida)
                throw new InvalidOperationException("A ordem de serviço precisa estar no status Recebida para iniciar o diagnóstico.");

            ordemServico.IniciarDiagnostico();
            ordemServico.AlterarStatus(StatusOS.EmDiagnostico);

            await _ordemServicoRepository.Atualizar(ordemServico);
            bool notificado = await NotificarMudancaStatusAsync(ordemServico);

            var response = ToResponse(ordemServico);
            response.ClienteNotificado = notificado;
            return response;
        }

        public async Task<OrdemServicoResponse> FinalizarDiagnostico(Guid id)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);

            if (ordemServico.Status != StatusOS.EmDiagnostico)
                throw new InvalidOperationException("A ordem de serviço precisa estar no status Em Diagnóstico para finalizar o diagnóstico.");

            TimeSpan? diagnosticoDuration = ordemServico.DataInicioDiagnostico.HasValue
                ? DateTime.UtcNow - ordemServico.DataInicioDiagnostico.Value
                : null;

            ordemServico.AlterarStatus(StatusOS.AguardandoAprovacao);

            await _ordemServicoRepository.Atualizar(ordemServico);
            if (diagnosticoDuration.HasValue)
                RegistrarDuracaoEtapa(ordemServico.Id, "diagnostico", diagnosticoDuration.Value);
            bool notificado = await NotificarMudancaStatusAsync(ordemServico);

            var response = ToResponse(ordemServico);
            response.ClienteNotificado = notificado;
            return response;
        }

        public async Task<OrdemServicoResponse> IncluirServico(Guid id, OrdemServicoServicosRequest request)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);
            GarantirNaoEncerradaPorCompensacao(ordemServico);

            IReadOnlyCollection<Guid> servicosIds = [.. ordemServico.ItensServico.Select(item => item.IdServico).Concat(request.ServicosIds).Distinct()];

            IReadOnlyCollection<ItemServico> servicos = await _catalogoGateway.ObterServicos(servicosIds);
            ValidarEntidadesEncontradas(servicosIds, servicos.Select(s => s.IdServico), idServico => new ServicoNaoEncontradoException(idServico));

            ordemServico.SincronizarItens(servicos, []);

            await _ordemServicoRepository.Atualizar(ordemServico);

            return ToResponse(ordemServico);
        }

        public async Task<OrdemServicoResponse> IncluirPecaInsumo(Guid id, OrdemServicoPecaInsumoRequest request)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);
            GarantirNaoEncerradaPorCompensacao(ordemServico);

            HashSet<Guid> pecasExistentesIds = ordemServico.ItensPecaInsumo.Select(item => item.IdPecaInsumo).ToHashSet();
            IReadOnlyCollection<Guid> pecasNovasIds = [.. request.PecasInsumosIds.Distinct().Where(idPeca => !pecasExistentesIds.Contains(idPeca))];
            IReadOnlyCollection<Guid> pecasIds = [.. pecasExistentesIds.Concat(request.PecasInsumosIds).Distinct()];
            IReadOnlyCollection<ItemPecaInsumo> pecas = await _catalogoGateway.ObterPecasInsumo(pecasIds);
            ValidarEntidadesEncontradas(pecasIds, pecas.Select(p => p.IdPecaInsumo), idPeca => new PecaInsumoNaoEncontradaException(idPeca));

            if (pecasNovasIds.Count == 0)
            {
                ordemServico.SincronizarItens([], pecas);
                await _ordemServicoRepository.Atualizar(ordemServico);
                return ToResponse(ordemServico);
            }

            // Saga: baixa o estoque de cada peça nova e só então grava a OS.
            // Qualquer falha agenda o estorno das baixas já confirmadas.
            await _reservaEstoqueSaga.Executar(ordemServico.Id, pecasNovasIds, async saga =>
            {
                ordemServico.SincronizarItens([], pecas);
                ordemServico.RegistrarEvento(new PecasInsumoIncluidas(ordemServico.Id, saga.IdSaga, pecasNovasIds));
                await _ordemServicoRepository.Atualizar(ordemServico);
            });

            return ToResponse(ordemServico);
        }

        public async Task RemoverItemServico(Guid id, Guid idServico)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);
            GarantirNaoEncerradaPorCompensacao(ordemServico);

            if (!ordemServico.RemoverItemServico(idServico))
                throw new ItemServicoNaoEncontradoException(idServico);

            ordemServico.RecalcularOrcamento();
            await _ordemServicoRepository.Atualizar(ordemServico);
        }

        public async Task RemoverItemPecaInsumo(Guid id, Guid idPecaInsumo)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);
            GarantirNaoEncerradaPorCompensacao(ordemServico);

            if (!ordemServico.RemoverItemPecaInsumo(idPecaInsumo))
                throw new ItemPecaInsumoNaoEncontradoException(idPecaInsumo);

            ordemServico.RecalcularOrcamento();
            await _ordemServicoRepository.Atualizar(ordemServico);
        }

        public async Task IniciarServico(Guid id, Guid idServico)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);
            GarantirNaoEncerradaPorCompensacao(ordemServico);
            ItemServico item = ObterItemServico(ordemServico, idServico);

            item.IniciarServico();
            ordemServico.AlterarStatus(StatusOS.EmExecucao);

            await _ordemServicoRepository.Atualizar(ordemServico);
            await NotificarMudancaStatusAsync(ordemServico);
        }

        public async Task FinalizarServico(Guid id, Guid idServico)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);
            GarantirNaoEncerradaPorCompensacao(ordemServico);
            ItemServico item = ObterItemServico(ordemServico, idServico);

            item.FinalizarServico();

            TimeSpan? execucaoDuration = null;
            if (ordemServico.ItensServico.All(servico => servico.DataHoraFim != null))
            {
                DateTime? inicioExecucao = ordemServico.ItensServico
                    .Where(servico => servico.DataHoraInicio.HasValue)
                    .Select(servico => servico.DataHoraInicio)
                    .Min();
                DateTime? fimExecucao = ordemServico.ItensServico
                    .Where(servico => servico.DataHoraFim.HasValue)
                    .Select(servico => servico.DataHoraFim)
                    .Max();

                if (inicioExecucao.HasValue && fimExecucao.HasValue)
                    execucaoDuration = fimExecucao.Value - inicioExecucao.Value;

                ordemServico.AlterarStatus(StatusOS.Finalizada);
                ordemServico.Concluir();
            }

            await _ordemServicoRepository.Atualizar(ordemServico);
            if (execucaoDuration.HasValue)
                RegistrarDuracaoEtapa(ordemServico.Id, "execucao", execucaoDuration.Value);
            await NotificarMudancaStatusAsync(ordemServico);
        }

        public async Task<OrdemServicoResponse> AprovarOrcamento(Guid id, bool aprovado)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);

            if (ordemServico.Status != StatusOS.AguardandoAprovacao)
                throw new InvalidOperationException("A ordem de serviço precisa estar no status Aguardando aprovação para ser aprovada.");

            if (aprovado)
                ordemServico.AlterarStatus(StatusOS.OrcamentoAprovado);
            else
                ordemServico.ReprovarOrcamento(); // publica OrcamentoReprovado → compensação estorna o estoque

            await _ordemServicoRepository.Atualizar(ordemServico);
            bool notificado = await NotificarMudancaStatusAsync(ordemServico);

            var response = ToResponse(ordemServico);
            response.ClienteNotificado = notificado;
            return response;
        }

        public async Task<OrdemServicoResponse> ConfirmarEntrega(Guid id)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);

            if (ordemServico.Status == StatusOS.Entregue)
                throw new InvalidOperationException("A ordem de serviço já foi entregue.");

            if (ordemServico.Status != StatusOS.Finalizada)
                throw new InvalidOperationException("A ordem de serviço não pode ser entregue, pois não foi finalizada.");

            TimeSpan? finalizacaoDuration = ordemServico.DataConclusao.HasValue
                ? DateTime.UtcNow - ordemServico.DataConclusao.Value
                : null;

            ordemServico.AlterarStatus(StatusOS.Entregue);

            await _ordemServicoRepository.Atualizar(ordemServico);
            if (finalizacaoDuration.HasValue)
                RegistrarDuracaoEtapa(ordemServico.Id, "finalizacao", finalizacaoDuration.Value);

            return ToResponse(ordemServico);
        }

        public async Task<OrdemServicoResponse> ObterPorId(Guid id)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);
            return ToResponse(ordemServico);
        }

        public async Task<OrdemServicoProgressoResponse> ObterProgresso(Guid id)
        {
            Domain.Entities.OrdemServico ordemServico = await ObterEntidadePorId(id);
            return ToProgressoResponse(ordemServico);
        }

        public async Task<IReadOnlyCollection<OrdemServicoResponse>> ObterTodos()
        {
            IReadOnlyCollection<Domain.Entities.OrdemServico> ordensServico = await _ordemServicoRepository.ObterAtivas();

            // Mesma ordenação do repositório do monólito: prioridade do status, depois data de abertura.
            return [.. ordensServico
                .OrderBy(o => PrioridadeListagem(o.Status))
                .ThenBy(o => o.DataAbertura)
                .Select(ToResponse)];
        }

        private static int PrioridadeListagem(StatusOS status)
        {
            if (status == StatusOS.EmExecucao) return 0;
            if (status == StatusOS.AguardandoAprovacao) return 1;
            if (status == StatusOS.EmDiagnostico) return 2;
            if (status == StatusOS.Recebida) return 3;
            return 4;
        }

        private async Task<Domain.Entities.OrdemServico> ObterEntidadePorId(Guid id)
        {
            return await _ordemServicoRepository.ObterPorId(id) ?? throw new OrdemServicoNaoEncontradaException(id);
        }

        /// <summary>
        /// Status que não existem no fluxo do monólito: o estoque destas OS já foi devolvido, então
        /// novas peças nunca seriam estornadas e iniciar um serviço reabriria a OS.
        /// </summary>
        private static void GarantirNaoEncerradaPorCompensacao(Domain.Entities.OrdemServico ordemServico)
        {
            if (ordemServico.Status == StatusOS.Cancelada || ordemServico.Status == StatusOS.OrcamentoReprovado)
                throw new InvalidOperationException($"A ordem de serviço no status {ordemServico.Status.Descricao} não pode ser alterada.");
        }

        private static ItemServico ObterItemServico(Domain.Entities.OrdemServico ordemServico, Guid idServico)
        {
            return ordemServico.ItensServico.FirstOrDefault(item => item.IdServico == idServico)
                ?? throw new ItemServicoNaoEncontradoException(idServico);
        }

        private void RegistrarDuracaoEtapa(Guid idOrdemServico, string etapa, TimeSpan duracao)
        {
            // Substitui IObservabilityMetrics.RecordOrderStatusDuration: log estruturado consultável no CloudWatch Logs Insights.
            _logger.LogInformation("Etapa {Etapa} da OS {IdOrdemServico} concluída em {DuracaoSegundos} segundos",
                etapa, idOrdemServico, Math.Round(duracao.TotalSeconds, 2));
        }

        private async Task<bool> NotificarMudancaStatusAsync(Domain.Entities.OrdemServico ordemServico)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(ordemServico.ClienteEmail))
                {
                    string assunto = $"Status da sua Ordem de Serviço mudou para {ordemServico.Status.Descricao}";
                    string corpoHtml = $$"""
                        <!DOCTYPE html>
                        <html lang="pt-BR">
                        <head>
                          <meta charset="UTF-8">
                          <style>
                            body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background-color: #f4f7f6; margin: 0; padding: 0; }
                            .container { max-width: 600px; margin: 40px auto; background-color: #ffffff; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px rgba(0,0,0,0.1); }
                            .header { background-color: #0056b3; padding: 20px; text-align: center; color: #ffffff; }
                            .header h1 { margin: 0; font-size: 24px; font-weight: 600; letter-spacing: 0.5px; }
                            .content { padding: 30px; color: #333333; line-height: 1.6; font-size: 16px; }
                            .status-box { background-color: #e9f2fb; border-left: 4px solid #0056b3; padding: 20px; margin: 25px 0; border-radius: 0 4px 4px 0; text-align: center; }
                            .status-label { font-size: 14px; color: #555555; text-transform: uppercase; letter-spacing: 1px; }
                            .status-text { display: block; font-size: 22px; font-weight: bold; color: #0056b3; margin-top: 5px; text-transform: uppercase; }
                            .footer { background-color: #f9f9f9; padding: 20px; text-align: center; font-size: 13px; color: #777777; border-top: 1px solid #eeeeee; }
                          </style>
                        </head>
                        <body>
                          <div class="container">
                            <div class="header">
                              <h1>Atualização de Serviço</h1>
                            </div>
                            <div class="content">
                              <p>Olá, <strong>{{ordemServico.ClienteNome}}</strong>!</p>
                              <p>Passando para informar que temos uma atualização sobre o andamento da manutenção do seu veículo.</p>
                              <div class="status-box">
                                <span class="status-label">Status atual da sua ordem de serviço</span>
                                <span class="status-text">{{ordemServico.Status.Descricao}}</span>
                              </div>
                              <p>Agradecemos a confiança em nossos serviços. Em caso de dúvidas, não hesite em entrar em contato com a nossa equipe.</p>
                            </div>
                            <div class="footer">
                              Esta é uma mensagem automática, por favor não responda a este e-mail.<br/>
                              Tech Challenge Auto Center &copy; {{DateTime.Now.Year}}
                            </div>
                          </div>
                        </body>
                        </html>
                        """;
                    return await _emailService.EnviarEmailAsync(ordemServico.ClienteEmail, assunto, corpoHtml);
                }
            }
            catch (InvalidOperationException)
            {
                // Ignora falhas esperadas no envio de e-mail para não quebrar a transação ou retornar erro 500
            }
            catch (IOException)
            {
                // Ignora falhas de I/O no envio de e-mail para não quebrar a transação ou retornar erro 500
            }

            return false;
        }

        private static OrdemServicoResponse ToResponse(Domain.Entities.OrdemServico ordemServico)
        {
            return new OrdemServicoResponse(
                ordemServico.Id,
                ordemServico.IdCliente,
                ordemServico.IdVeiculo,
                ordemServico.Status.Id,
                ordemServico.ClienteNome,
                ordemServico.Status.Descricao,
                ordemServico.Observacao,
                ordemServico.DataAbertura,
                ordemServico.DataConclusao,
                ordemServico.ValorOrcamento,
                [.. ordemServico.ItensServico.Select(item => new OrdemServicoItemServicoResponse(
                    item.IdServico,
                    item.DataHoraInicio,
                    item.DataHoraFim,
                    item.Nome,
                    item.ValorUnitario))],
                [.. ordemServico.ItensPecaInsumo.Select(item => new OrdemServicoItemPecaInsumoResponse(
                    item.IdPecaInsumo,
                    item.Descricao,
                    item.ValorUnitario))]
            );
        }

        private static OrdemServicoProgressoResponse ToProgressoResponse(Domain.Entities.OrdemServico ordemServico)
        {
            int tempoTotalEstimado = ordemServico.ItensServico.Sum(item => item.TempoEstimadoMinutos);
            int progressoEmMinutos = ordemServico.ItensServico.Sum(CalcularProgressoItemEmMinutos);
            int percentualConcluido = tempoTotalEstimado == 0
                ? 100
                : Math.Clamp((int)Math.Round(progressoEmMinutos * 100m / tempoTotalEstimado), 0, 100);

            DateTime? previsaoConclusao = tempoTotalEstimado == 0
                ? null
                : ordemServico.DataAbertura.AddMinutes(tempoTotalEstimado);

            return new OrdemServicoProgressoResponse(
                ordemServico.Id,
                ordemServico.Status.Descricao,
                percentualConcluido,
                ordemServico.DataAbertura,
                previsaoConclusao,
                [.. ordemServico.ItensServico.Select(item => new OrdemServicoProgressoServicoResponse(
                    item.IdServico,
                    item.Nome,
                    ObterStatusItemServico(item),
                    item.TempoEstimadoMinutos,
                    item.ObterTempoExecutadoMinutos(),
                    item.DataHoraInicio,
                    item.DataHoraFim))]
            );
        }

        private static int CalcularProgressoItemEmMinutos(ItemServico item)
        {
            int tempoEstimado = item.TempoEstimadoMinutos;

            if (item.DataHoraFim != null)
                return tempoEstimado;

            if (item.DataHoraInicio == null)
                return 0;

            int tempoExecutado = item.ObterTempoExecutadoMinutos() ?? 0;
            return Math.Min(tempoExecutado, tempoEstimado);
        }

        private static string ObterStatusItemServico(ItemServico item)
        {
            if (item.DataHoraFim != null)
                return "Finalizado";

            if (item.DataHoraInicio != null)
                return "Em execução";

            return "Pendente";
        }

        private static void ValidarEntidadesEncontradas(
            IReadOnlyCollection<Guid> idsSolicitados,
            IEnumerable<Guid> idsEncontrados,
            Func<Guid, Exception> exceptionFactory)
        {
            HashSet<Guid> idsEncontradosSet = idsEncontrados.ToHashSet();

            foreach (Guid id in idsSolicitados.Distinct())
            {
                if (!idsEncontradosSet.Contains(id))
                    throw exceptionFactory(id);
            }
        }
    }
}
