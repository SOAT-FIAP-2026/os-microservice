using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Fiap.TechChallenge.Contracts.Common;
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
            string correlationId = CorrelationContext.Resolve(
                comando.CorrelationId,
                fallback: comando.IdOrdemServico.ToString());

            ComandoCompensacao comandoComCorrelacao = string.IsNullOrWhiteSpace(comando.CorrelationId)
                ? comando with { CorrelationId = correlationId }
                : comando;

            await _sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _queueUrl,
                MessageBody = JsonSerializer.Serialize(comandoComCorrelacao, JsonDefaults.Options),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["tipo"] = new() { DataType = "String", StringValue = comando.Tipo.ToString() },
                    [SnsMessageAttributes.IdOrdemServico] = new() { DataType = "String", StringValue = comando.IdOrdemServico.ToString() },
                    [SnsMessageAttributes.CorrelationId] = new() { DataType = "String", StringValue = correlationId }
                }
            });
        }
    }
}
