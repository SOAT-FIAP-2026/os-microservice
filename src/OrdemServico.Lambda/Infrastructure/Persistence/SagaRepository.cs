using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Configuration;
using static OrdemServico.Lambda.Infrastructure.Persistence.DynamoDbItem;

namespace OrdemServico.Lambda.Infrastructure.Persistence
{
    public class SagaRepository(IAmazonDynamoDB dynamoDb, LambdaSettings settings) : ISagaRepository
    {
        private static readonly TimeSpan Retencao = TimeSpan.FromDays(30);

        private readonly IAmazonDynamoDB _dynamoDb = dynamoDb;
        private readonly string _tableName = settings.TableName;

        public async Task Salvar(SagaReservaEstoque saga)
        {
            Dictionary<string, AttributeValue> item = Chave(saga.IdOrdemServico, saga.IdSaga);
            item[TipoEntidade] = S("SAGA");
            item["statusSaga"] = S(saga.Status.ToString());
            item[Dados] = ParaMapa(saga);
            item[ExpiraEm] = Ttl(Retencao);

            await _dynamoDb.PutItemAsync(new PutItemRequest { TableName = _tableName, Item = item });
        }

        public async Task<SagaReservaEstoque?> Obter(Guid idOrdemServico, Guid idSaga)
        {
            GetItemResponse response = await _dynamoDb.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = Chave(idOrdemServico, idSaga),
                ConsistentRead = true
            });

            return response.IsItemSet ? DoMapa<SagaReservaEstoque>(response.Item[Dados]) : null;
        }

        private static Dictionary<string, AttributeValue> Chave(Guid idOrdemServico, Guid idSaga) => new()
        {
            [PK] = S($"OS#{idOrdemServico}"),
            [SK] = S($"SAGA#{idSaga}")
        };
    }
}
