using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Configuration;
using static OrdemServico.Lambda.Infrastructure.Persistence.DynamoDbItem;

namespace OrdemServico.Lambda.Infrastructure.Persistence
{
    public class RegistroCompensacaoRepository(IAmazonDynamoDB dynamoDb, LambdaSettings settings) : IRegistroCompensacaoRepository
    {
        private static readonly TimeSpan Retencao = TimeSpan.FromDays(30);

        private readonly IAmazonDynamoDB _dynamoDb = dynamoDb;
        private readonly string _tableName = settings.TableName;

        public async Task<RegistroCompensacao?> Obter(string idCompensacao)
        {
            GetItemResponse response = await _dynamoDb.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = Chave(idCompensacao),
                ConsistentRead = true
            });

            return response.IsItemSet ? DoMapa<RegistroCompensacao>(response.Item[Dados]) : null;
        }

        public async Task Salvar(RegistroCompensacao registro)
        {
            Dictionary<string, AttributeValue> item = Chave(registro.IdCompensacao);
            item[TipoEntidade] = S("COMPENSACAO");
            item[Dados] = ParaMapa(registro);
            item[ExpiraEm] = Ttl(Retencao);

            await _dynamoDb.PutItemAsync(new PutItemRequest { TableName = _tableName, Item = item });
        }

        private static Dictionary<string, AttributeValue> Chave(string idCompensacao) => new()
        {
            [PK] = S($"COMPENSACAO#{idCompensacao}"),
            [SK] = S("COMPENSACAO")
        };
    }
}
