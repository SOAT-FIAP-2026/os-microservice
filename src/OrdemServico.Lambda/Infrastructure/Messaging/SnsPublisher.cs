using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using OrdemServico.Lambda.Configuration;

namespace OrdemServico.Lambda.Infrastructure.Messaging
{
    public sealed record EventoOutbox(string IdEvento, string TipoEvento, string IdOrdemServico, string Payload);

    public class SnsPublisher(IAmazonSimpleNotificationService sns, LambdaSettings settings)
    {
        private readonly IAmazonSimpleNotificationService _sns = sns;
        private readonly LambdaSettings _settings = settings;

        public async Task Publicar(EventoOutbox evento)
        {
            await _sns.PublishAsync(new PublishRequest
            {
                TopicArn = _settings.EventosTopicArn,
                Message = evento.Payload,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["tipoEvento"] = Atributo(evento.TipoEvento),
                    ["origem"] = Atributo(_settings.EventoOrigem),
                    ["idOrdemServico"] = Atributo(evento.IdOrdemServico)
                }
            });
        }

        private static MessageAttributeValue Atributo(string valor) => new() { DataType = "String", StringValue = valor };
    }
}
