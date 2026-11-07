using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Fiap.TechChallenge.Contracts.Common;
using OrdemServico.Lambda.Configuration;

namespace OrdemServico.Lambda.Infrastructure.Messaging
{
    public sealed record EventoOutbox(
        string IdEvento,
        string TipoEvento,
        string IdOrdemServico,
        string Payload,
        string? CorrelationId = null);

    public class SnsPublisher(IAmazonSimpleNotificationService sns, LambdaSettings settings)
    {
        private readonly IAmazonSimpleNotificationService _sns = sns;
        private readonly LambdaSettings _settings = settings;

        public async Task Publicar(EventoOutbox evento)
        {
            string correlationId = CorrelationContext.Resolve(
                evento.CorrelationId,
                fallback: evento.IdOrdemServico);

            await _sns.PublishAsync(new PublishRequest
            {
                TopicArn = _settings.EventosTopicArn,
                Message = evento.Payload,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    [SnsMessageAttributes.TipoEvento] = Atributo(evento.TipoEvento),
                    [SnsMessageAttributes.Origem] = Atributo(_settings.EventoOrigem),
                    [SnsMessageAttributes.IdOrdemServico] = Atributo(evento.IdOrdemServico),
                    [SnsMessageAttributes.CorrelationId] = Atributo(correlationId)
                }
            });
        }

        private static MessageAttributeValue Atributo(string valor) => new() { DataType = "String", StringValue = valor };
    }
}
