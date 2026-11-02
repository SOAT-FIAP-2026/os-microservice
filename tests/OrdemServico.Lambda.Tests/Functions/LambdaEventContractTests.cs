using System.Text;
using Amazon.Lambda.DynamoDBEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SNSEvents;
using Amazon.Lambda.SQSEvents;
using FluentAssertions;

namespace OrdemServico.Lambda.Tests.Functions
{
    /// <summary>Formato real que a AWS envia e espera, com o mesmo serializador usado em produção.</summary>
    public class LambdaEventContractTests
    {
        private static readonly DefaultLambdaJsonSerializer Serializer = new();

        private static T Ler<T>(string json) => Serializer.Deserialize<T>(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        private static string Escrever<T>(T valor)
        {
            using var stream = new MemoryStream();
            Serializer.Serialize(valor, stream);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        [Fact]
        public void Sns_DeveLerMensagemEAtributos()
        {
            SNSEvent evento = Ler<SNSEvent>("""
                {"Records":[{"EventSource":"aws:sns","EventVersion":"1.0","EventSubscriptionArn":"arn:aws:sns:sa-east-1:1:t:s",
                "Sns":{"Type":"Notification","MessageId":"m-1","TopicArn":"arn:aws:sns:sa-east-1:1:t","Subject":null,
                "Message":"{\"idOrdemServico\":\"11111111-1111-1111-1111-111111111111\"}","Timestamp":"2026-11-02T14:30:00.000Z",
                "SignatureVersion":"1","Signature":"x","SigningCertUrl":"u","UnsubscribeUrl":"u",
                "MessageAttributes":{"tipoEvento":{"Type":"String","Value":"PagamentoRecusado"}}}}]}
                """);

            var comando = Lambda.Functions.CompensacaoEventoFunction.Traduzir(evento.Records[0].Sns);

            comando!.IdCompensacao.Should().Be("evt-m-1");
            comando.IdOrdemServico.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        }

        [Fact]
        public void Stream_DeveLerImagemDoOutbox()
        {
            DynamoDBEvent evento = Ler<DynamoDBEvent>("""
                {"Records":[{"eventID":"1","eventName":"INSERT","eventVersion":"1.1","eventSource":"aws:dynamodb","awsRegion":"sa-east-1",
                "dynamodb":{"ApproximateCreationDateTime":1793629800,"Keys":{"PK":{"S":"OUTBOX#e"},"SK":{"S":"OUTBOX"}},
                "NewImage":{"PK":{"S":"OUTBOX#e"},"tipoEvento":{"S":"OrcamentoReprovado"},"idOrdemServico":{"S":"o"},"payload":{"S":"{}"},"expiraEm":{"N":"1"}},
                "SequenceNumber":"111","SizeBytes":26,"StreamViewType":"NEW_IMAGE"},"eventSourceARN":"arn"}]}
                """);

            var registro = evento.Records[0];
            registro.EventName.ToString().Should().Be("INSERT");
            registro.Dynamodb.SequenceNumber.Should().Be("111");
            registro.Dynamodb.NewImage["tipoEvento"].S.Should().Be("OrcamentoReprovado");
        }

        [Fact]
        public void Respostas_DevemUsarOsNomesQueOLambdaEspera()
        {
            Escrever(new SQSBatchResponse([new SQSBatchResponse.BatchItemFailure { ItemIdentifier = "a" }]))
                .Should().Contain("\"batchItemFailures\"").And.Contain("\"itemIdentifier\":\"a\"");

            Escrever(new StreamsEventResponse { BatchItemFailures = [new StreamsEventResponse.BatchItemFailure { ItemIdentifier = "b" }] })
                .Should().Contain("\"batchItemFailures\"").And.Contain("\"itemIdentifier\":\"b\"");
        }
    }
}
