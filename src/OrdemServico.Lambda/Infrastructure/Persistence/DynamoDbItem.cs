using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using OrdemServico.Lambda.Configuration;

namespace OrdemServico.Lambda.Infrastructure.Persistence
{
    /// <summary>
    /// Modelo single-table:
    ///   OS#{id}            | METADATA      → agregado Ordem de Serviço (GSI1 lista as ativas)
    ///   OS#{id}            | SAGA#{idSaga} → log da saga de reserva de estoque
    ///   OUTBOX#{idEvento}  | OUTBOX        → evento pendente de publicação (DynamoDB Streams → SNS)
    ///   COMPENSACAO#{id}   | COMPENSACAO   → controle de idempotência das compensações
    /// </summary>
    internal static class DynamoDbItem
    {
        public const string PK = "PK";
        public const string SK = "SK";
        public const string GSI1 = "GSI1";
        public const string GSI1PK = "GSI1PK";
        public const string GSI1SK = "GSI1SK";
        public const string TipoEntidade = "tipoEntidade";
        public const string Dados = "dados";
        public const string Versao = "versao";
        public const string ExpiraEm = "expiraEm";

        public static AttributeValue S(string valor) => new() { S = valor };

        public static AttributeValue N(long valor) => new() { N = valor.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        public static AttributeValue Ttl(TimeSpan duracao) => N(DateTimeOffset.UtcNow.Add(duracao).ToUnixTimeSeconds());

        /// <summary>Serializa um objeto como mapa (M) do DynamoDB, mantendo os tipos (N, S, BOOL, L, M).</summary>
        public static AttributeValue ParaMapa<T>(T valor)
        {
            string json = JsonSerializer.Serialize(valor, JsonDefaults.Options);
            return new AttributeValue { M = Document.FromJson(json).ToAttributeMap(DynamoDBEntryConversion.V2) };
        }

        public static T DoMapa<T>(AttributeValue valor)
        {
            string json = Document.FromAttributeMap(valor.M).ToJson();
            return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options)
                ?? throw new InvalidOperationException($"Não foi possível desserializar {typeof(T).Name} do DynamoDB.");
        }
    }
}
