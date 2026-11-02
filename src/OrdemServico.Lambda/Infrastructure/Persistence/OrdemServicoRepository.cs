using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Configuration;
using OrdemServico.Lambda.Domain.Entities;
using OrdemServico.Lambda.Domain.Events;
using OrdemServico.Lambda.Domain.Exceptions;
using static OrdemServico.Lambda.Infrastructure.Persistence.DynamoDbItem;

namespace OrdemServico.Lambda.Infrastructure.Persistence
{
    /// <summary>
    /// Repositório DynamoDB da OS. Cada gravação é uma TransactWriteItems com:
    ///   - o agregado, condicionado à versão lida (concorrência otimista);
    ///   - um item OUTBOX por evento de domínio pendente.
    /// O DynamoDB Streams entrega os itens OUTBOX à OutboxPublisherFunction, que publica no
    /// SNS. Assim, estado e eventos nunca divergem (transactional outbox).
    /// </summary>
    public class OrdemServicoRepository(IAmazonDynamoDB dynamoDb, LambdaSettings settings) : IOrdemServicoRepository
    {
        private const string IndiceAtivas = "ORDEM_SERVICO#ATIVA";
        private const string IndiceEncerradas = "ORDEM_SERVICO#ENCERRADA";
        private static readonly TimeSpan RetencaoOutbox = TimeSpan.FromDays(1);

        private readonly IAmazonDynamoDB _dynamoDb = dynamoDb;
        private readonly string _tableName = settings.TableName;

        public Task Adicionar(Domain.Entities.OrdemServico ordemServico)
        {
            return Gravar(ordemServico, "attribute_not_exists(PK)", null);
        }

        public Task Atualizar(Domain.Entities.OrdemServico ordemServico)
        {
            return Gravar(ordemServico, "versao = :versaoLida", new Dictionary<string, AttributeValue>
            {
                [":versaoLida"] = N(ordemServico.Versao)
            });
        }

        public async Task<Domain.Entities.OrdemServico?> ObterPorId(Guid id)
        {
            GetItemResponse response = await _dynamoDb.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = Chave(id),
                ConsistentRead = true
            });

            return response.IsItemSet ? ParaDominio(response.Item) : null;
        }

        public async Task<IReadOnlyCollection<Domain.Entities.OrdemServico>> ObterAtivas()
        {
            List<Domain.Entities.OrdemServico> ordens = [];
            Dictionary<string, AttributeValue>? ultimaChave = null;

            do
            {
                QueryResponse response = await _dynamoDb.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = GSI1,
                    KeyConditionExpression = "GSI1PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = S(IndiceAtivas) },
                    ExclusiveStartKey = ultimaChave
                });

                ordens.AddRange(response.Items.Select(ParaDominio));
                ultimaChave = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
            }
            while (ultimaChave != null);

            return ordens;
        }

        private async Task Gravar(
            Domain.Entities.OrdemServico ordemServico,
            string condicao,
            Dictionary<string, AttributeValue>? valoresCondicao)
        {
            long novaVersao = ordemServico.Versao + 1;

            List<TransactWriteItem> itens =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = _tableName,
                        Item = ParaItem(ordemServico, novaVersao),
                        ConditionExpression = condicao,
                        ExpressionAttributeValues = valoresCondicao
                    }
                }
            ];

            itens.AddRange(ordemServico.Eventos.Select(evento => new TransactWriteItem
            {
                Put = new Put { TableName = _tableName, Item = ParaItemOutbox(evento) }
            }));

            try
            {
                await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = itens });
            }
            catch (TransactionCanceledException ex)
                when (ex.CancellationReasons?.FirstOrDefault()?.Code is "ConditionalCheckFailed" or "TransactionConflict")
            {
                throw new ConcorrenciaOrdemServicoException(ordemServico.Id);
            }

            ordemServico.DefinirVersao(novaVersao);
            ordemServico.LimparEventos();
        }

        private static Dictionary<string, AttributeValue> Chave(Guid id) => new()
        {
            [PK] = S($"OS#{id}"),
            [SK] = S("METADATA")
        };

        private static Dictionary<string, AttributeValue> ParaItem(Domain.Entities.OrdemServico ordemServico, long versao)
        {
            bool encerrada = ordemServico.Status == StatusOS.Finalizada || ordemServico.Status == StatusOS.Entregue;

            Dictionary<string, AttributeValue> item = Chave(ordemServico.Id);
            item[TipoEntidade] = S("ORDEM_SERVICO");
            item[GSI1PK] = S(encerrada ? IndiceEncerradas : IndiceAtivas);
            item[GSI1SK] = S($"{ordemServico.DataAbertura:O}#{ordemServico.Id}");
            item["statusCodigo"] = S(ordemServico.Status.Codigo);
            item[Versao] = N(versao);
            item[Dados] = ParaMapa(OrdemServicoDocumento.DeDominio(ordemServico));
            return item;
        }

        private static Domain.Entities.OrdemServico ParaDominio(Dictionary<string, AttributeValue> item)
        {
            long versao = long.Parse(item[Versao].N, System.Globalization.CultureInfo.InvariantCulture);
            return DoMapa<OrdemServicoDocumento>(item[Dados]).ParaDominio(versao);
        }

        private static Dictionary<string, AttributeValue> ParaItemOutbox(EventoDominio evento) => new()
        {
            [PK] = S($"OUTBOX#{evento.IdEvento}"),
            [SK] = S("OUTBOX"),
            [TipoEntidade] = S("OUTBOX"),
            ["tipoEvento"] = S(evento.TipoEvento),
            ["idOrdemServico"] = S(evento.IdOrdemServico.ToString()),
            ["ocorridoEm"] = S(evento.OcorridoEm.ToString("O")),
            ["payload"] = S(JsonSerializer.Serialize(evento, evento.GetType(), JsonDefaults.Options)),
            [ExpiraEm] = Ttl(RetencaoOutbox)
        };
    }
}
