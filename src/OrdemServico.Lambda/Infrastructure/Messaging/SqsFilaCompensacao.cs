using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Configuration;

namespace OrdemServico.Lambda.Infrastructure.Messaging
{
    public class SqsFilaCompensacao(IAmazonSQS sqs, LambdaSettings settings) : IFilaCompensacao
    {
        private readonly IAmazonSQS _sqs = sqs;
        private readonly string _queueUrl = settings.CompensacaoQueueUrl;

        public async Task Enviar(ComandoCompensacao comando)
        {
            await _sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _queueUrl,
                MessageBody = JsonSerializer.Serialize(comando, JsonDefaults.Options),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["tipo"] = new() { DataType = "String", StringValue = comando.Tipo.ToString() },
                    ["idOrdemServico"] = new() { DataType = "String", StringValue = comando.IdOrdemServico.ToString() }
                }
            });
        }
    }
}
