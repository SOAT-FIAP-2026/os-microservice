using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using Fiap.TechChallenge.Contracts.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrdemServico.Lambda.Configuration;
using OrdemServico.Lambda.Infrastructure.Messaging;

namespace OrdemServico.Lambda.Functions
{
    /// <summary>
    /// Publicador do transactional outbox: DynamoDB Streams (itens OUTBOX#, filtrados no
    /// event source mapping) → SNS. Em caso de erro devolve o primeiro registro com
    /// falha; o Lambda reprocessa a partir dele, preservando a ordem dos eventos do shard.
    ///
    /// Handler: OrdemServico.Lambda::OrdemServico.Lambda.Functions.OutboxPublisherFunction::Handle
    /// </summary>
    public class OutboxPublisherFunction
    {
        private readonly IServiceProvider _serviceProvider;

        public OutboxPublisherFunction() : this(DependencyInjection.BuildWorkerProvider())
        {
        }

        public OutboxPublisherFunction(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task<StreamsEventResponse> Handle(DynamoDBEvent dynamoEvent, ILambdaContext context)
        {
            ILogger logger = _serviceProvider.GetRequiredService<ILogger<OutboxPublisherFunction>>();
            SnsPublisher publisher = _serviceProvider.GetRequiredService<SnsPublisher>();

            foreach (DynamoDBEvent.DynamodbStreamRecord registro in dynamoEvent.Records)
            {
                Dictionary<string, DynamoDBEvent.AttributeValue>? imagem = registro.Dynamodb.NewImage;

                if (registro.EventName != "INSERT" || imagem == null || !imagem.TryGetValue("payload", out var payload))
                    continue;

                try
                {
                    string idOrdemServico = imagem["idOrdemServico"].S;
                    string? correlationFromImage = imagem.TryGetValue("correlationId", out var corrAttr) ? corrAttr.S : null;
                    string correlationId = CorrelationContext.Resolve(
                        correlationFromImage,
                        fallback: SnsCorrelationHelper.ExtractCorrelationId(payload.S, fallbackId: idOrdemServico));

                    var evento = new EventoOutbox(
                        IdEvento: imagem["PK"].S.Replace("OUTBOX#", string.Empty),
                        TipoEvento: imagem["tipoEvento"].S,
                        IdOrdemServico: idOrdemServico,
                        Payload: payload.S,
                        CorrelationId: correlationId);

                    using (CorrelationContext.Push(correlationId))
                    using (logger.BeginScope(new Dictionary<string, object>
                    {
                        [CorrelationContext.LogScopeKey] = correlationId,
                        [CorrelationContext.OsIdScopeKey] = idOrdemServico
                    }))
                    {
                        await publisher.Publicar(evento);
                        logger.LogInformation("Evento {TipoEvento} {IdEvento} da OS {IdOrdemServico} publicado no SNS (CorrelationId: {CorrelationId})",
                            evento.TipoEvento, evento.IdEvento, evento.IdOrdemServico, correlationId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Falha ao publicar o evento do outbox {SequenceNumber}", registro.Dynamodb.SequenceNumber);
                    return new StreamsEventResponse
                    {
                        BatchItemFailures = [new StreamsEventResponse.BatchItemFailure { ItemIdentifier = registro.Dynamodb.SequenceNumber }]
                    };
                }
            }

            return new StreamsEventResponse { BatchItemFailures = [] };
        }
    }
}
