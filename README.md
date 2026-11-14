# Lambda — Ordem de Serviço

Migração do domínio **Ordem de Serviço** do monólito (`src/Fiap.TechChallenge.*`) para **AWS Lambda (.NET 8)**, com **DynamoDB**, **API Gateway (HTTP API)**, **SNS**, **SQS** e infraestrutura em **CloudFormation**. O código do monólito não foi alterado.

As rotas, os payloads de request/response, os status HTTP, as mensagens de erro e as regras de autorização são os mesmos do `OrdemServicoController`.

## Arquitetura

```mermaid
graph LR
    U[Cliente / Admin] -->|JWT Bearer| APIGW[API Gateway<br/>HTTP API]
    APIGW --> API[ApiFunction<br/>ASP.NET Core minimal API]
    API -->|TransactWriteItems<br/>OS + OUTBOX| DDB[(DynamoDB<br/>single-table)]
    API -->|GET cliente/veículo/serviço/peça<br/>PUT estoque| CORE[API core<br/>monólito no EKS]
    API -->|falha na saga| Q[[SQS<br/>compensacao]]
    DDB -->|Streams<br/>itens OUTBOX| OUT[OutboxPublisherFunction]
    OUT -->|Publish| EB{{SNS<br/>techchallenge-eventos-stage}}
    EB -->|filtro tipoEvento:<br/>OrcamentoReprovado<br/>OrdemServicoCancelada<br/>ReservaEstoqueFalhou<br/>PagamentoRecusado| CE[CompensacaoEventoFunction]
    Q --> CF[CompensacaoFilaFunction]
    CF -->|estorno| CORE
    CE -->|estorno / ajuste da OS| CORE
    CE --> DDB
    CF --> DDB
    Q -.->|5 tentativas| DLQ1[[DLQ]]
    CE -.->|retentativas esgotadas| DLQ2[[DLQ]]
    OUT -.-> DLQ3[[DLQ]]
```

Um único pacote, com quatro handlers:

| Função | Gatilho | Handler |
|---|---|---|
| `ApiFunction` | API Gateway HTTP API (payload 2.0) | `OrdemServico.Lambda` |
| `CompensacaoFilaFunction` | SQS `techchallenge-ordem-servico-compensacao-<stage>` | `OrdemServico.Lambda::OrdemServico.Lambda.Functions.CompensacaoFilaFunction::Handle` |
| `CompensacaoEventoFunction` | Assinatura do tópico SNS (filtro por `tipoEvento`) | `OrdemServico.Lambda::OrdemServico.Lambda.Functions.CompensacaoEventoFunction::Handle` |
| `OutboxPublisherFunction` | DynamoDB Streams (filtro: `tipoEntidade = OUTBOX`) | `OrdemServico.Lambda::OrdemServico.Lambda.Functions.OutboxPublisherFunction::Handle` |

## Documentação Interativa (Swagger UI)

O microsserviço disponibiliza a documentação interativa Swagger/OpenAPI v3:
- **Swagger UI:** [`http://localhost:8083/swagger`](http://localhost:8083/swagger)
- **JSON OpenAPI:** [`http://localhost:8083/swagger/v1/swagger.json`](http://localhost:8083/swagger/v1/swagger.json)
- **Autenticação JWT Bearer:** O Swagger UI possui suporte ao esquema Bearer. Basta autenticar na API Core (`POST /api/v1/auth/login`) e informar o token no botão **Authorize**.

## Endpoints (API Gateway → Lambda / Local K8s & Docker)

Base Local: `http://localhost:8083`  
Base AWS: `https://<api-id>.execute-api.<região>.amazonaws.com` (output `ApiUrl` da stack).

| Método | Rota | Auth / Perfil | Resposta |
|---|---|---|---|
| POST | `/api/v1/ordens-servico` | Administrador | 201 `{ id, clienteNotificado }` |
| GET | `/api/v1/ordens-servico` | Administrador, Mecanico | 200 `OrdemServicoResponse[]` (sem Finalizadas/Entregues, ordenadas por prioridade) |
| GET | `/api/v1/ordens-servico/{id}` | Administrador, Mecanico | 200 `OrdemServicoResponse` |
| GET | `/api/v1/ordens-servico/{id}/progresso` | pública (Cliente) | 200 `OrdemServicoProgressoResponse` |
| POST | `/api/v1/ordens-servico/{id}/servicos` | Administrador, Mecanico | 200 `OrdemServicoResponse` |
| POST | `/api/v1/ordens-servico/{id}/pecas-insumos` | Administrador, Mecanico | 200 `OrdemServicoResponse` (**saga**) |
| PATCH | `/api/v1/ordens-servico/{id}/iniciar-diagnostico` | Administrador, Mecanico | 200 |
| PATCH | `/api/v1/ordens-servico/{id}/finalizar-diagnostico` | Administrador, Mecanico | 200 |
| PATCH | `/api/v1/ordens-servico/{id}/aprovar-orcamento?aprovado=true\|false` | pública (Cliente) | 200 |
| PATCH | `/api/v1/ordens-servico/{id}/confirmar-entrega` | pública (Cliente) | 200 |
| PATCH | `/api/v1/ordens-servico/{id}/servicos/{idServico}/iniciar` | Administrador, Mecanico | 204 |
| PATCH | `/api/v1/ordens-servico/{id}/servicos/{idServico}/finalizar` | Administrador, Mecanico | 204 |
| DELETE | `/api/v1/ordens-servico/{id}/servicos/{idServico}` | Administrador, Mecanico | 204 |
| DELETE | `/api/v1/ordens-servico/{id}/pecas-insumos/{idPecaInsumo}` | Administrador, Mecanico | 204 |
| GET | `/api/v1/ordens-servico/health` | pública | 200 |

Erros seguem o contrato do monólito: `{ "statusCode": 400, "message": "...", "traceId": "..." }`.

O JWT (HMAC-SHA256, issuer `TechChallenge`, audience `techchallenge.com.br`) é validado **dentro da Lambda** com a mesma chave do SSM (`/techchallenge/prod/jwt_secret`). Assim, os tokens emitidos pela API core e pela `lambda-auth-function` continuam valendo. O JWT authorizer nativo do HTTP API não serve aqui porque só aceita chaves assimétricas publicadas via JWKS.

## Saga e compensação

### 1. Saga orquestrada: inclusão de peças/insumos (compensação por **fila**)

No monólito, `IncluirPecaInsumo` baixava o estoque e gravava a OS na mesma transação do EF Core. Agora o estoque fica em outro serviço, então o fluxo virou uma saga:

```mermaid
sequenceDiagram
    participant API as ApiFunction
    participant DDB as DynamoDB
    participant CORE as API core (estoque)
    participant SQS as SQS compensação
    participant CF as CompensacaoFilaFunction

    API->>DDB: SAGA#id = Iniciada
    loop cada peça nova
        API->>CORE: PUT /estoques/{peca}/remover-quantidade (1)
        API->>DDB: SAGA#id += peça reservada
    end
    alt tudo ok
        API->>DDB: TransactWrite(OS + OUTBOX PecasInsumoIncluidas)
        API->>DDB: SAGA#id = Concluida
    else falha em qualquer passo
        API->>DDB: SAGA#id = Compensando
        API->>SQS: ComandoCompensacao EstornarEstoque (só as peças já baixadas)
        API-->>API: devolve o erro original (ex.: 400 "Estoque insuficiente.")
        SQS->>CF: mensagem
        CF->>CORE: PUT /estoques/{peca}/adicionar-quantidade (1)
        CF->>DDB: SAGA#id = Compensada
    end
```

A fila tem 5 tentativas antes de ir para a DLQ e usa partial batch response. Ela também aceita comandos de outros serviços:

```json
{
  "idCompensacao": "id-unico-para-idempotencia",
  "tipo": "EstornarEstoque | RemoverPecaInsumo | CancelarOrdemServico",
  "idOrdemServico": "guid",
  "pecasInsumosIds": ["guid"],
  "motivo": "texto",
  "idSaga": "guid (opcional)"
}
```

### 2. Saga coreografada: compensação por **evento** (SNS)

Tudo passa pelo tópico `techchallenge-eventos-<stage>` (output `EventosTopicArn`). A mensagem é o JSON do evento, e o tipo vai nos atributos de mensagem:

| Atributo | Exemplo | Obrigatório |
|---|---|---|
| `tipoEvento` | `PagamentoRecusado` | Sim. O filtro da assinatura usa este atributo; sem ele a mensagem não é entregue |
| `origem` | `techchallenge.pagamento` | Não. Vai para o log e para `CompensacaoExecutada` |
| `idOrdemServico` | guid | Não. Publicado nos nossos eventos, para filtro de quem assina |

| `tipoEvento` | Publicado por | Compensação |
|---|---|---|
| `OrcamentoReprovado` | Ordem de Serviço | Estorna o estoque das peças da OS |
| `OrdemServicoCancelada` | Ordem de Serviço | Estorna o estoque das peças da OS |
| `ReservaEstoqueFalhou` | Estoque | Remove a peça da OS e recalcula o orçamento |
| `PagamentoRecusado` | Pagamento | Cancela a OS, que publica `OrdemServicoCancelada` e com isso estorna o estoque |

Exemplos de mensagem aceitos (de outros serviços):

```json
{ "idOrdemServico": "guid", "idPecaInsumo": "guid", "motivo": "Estoque insuficiente." }
{ "idOrdemServico": "guid", "motivo": "Cartão recusado" }
```

Outros serviços assinam o mesmo tópico com o próprio filtro de `tipoEvento`. O SNS não guarda histórico: quem precisa reprocessar eventos deve assinar com uma fila SQS.

### Garantias

- **Transactional outbox**: a OS e os seus eventos são gravados na mesma `TransactWriteItems`. O Streams entrega os itens `OUTBOX#` ao publicador, que repete a tentativa e, se não conseguir, manda para a DLQ. Estado e evento nunca divergem.
- **Idempotência**: cada compensação tem um `idCompensacao` (`saga-<id>`, `evt-<idEvento>` nos nossos eventos, `evt-<MessageId do SNS>` nos externos, ou o id enviado na fila). O registro `COMPENSACAO#id` guarda as peças já estornadas, então uma mensagem reentregue não devolve a mesma peça duas vezes.
- **Concorrência otimista**: o atributo `versao` é verificado na gravação. Se houver conflito, a API responde 409.
- **Observabilidade**: logs JSON com `correlation_id` (header `X-Correlation-ID`, que também é repassado à API core), X-Ray ativo, alarmes nas 3 DLQs e alarme de 5xx na API.

## Modelo DynamoDB (single-table)

| PK | SK | Conteúdo |
|---|---|---|
| `OS#{id}` | `METADATA` | Agregado completo (`dados`), `versao`, `GSI1PK = ORDEM_SERVICO#ATIVA \| #ENCERRADA`, `GSI1SK = dataAbertura#id` |
| `OS#{id}` | `SAGA#{idSaga}` | Log da saga (peças solicitadas/reservadas, status). TTL de 30 dias |
| `OUTBOX#{idEvento}` | `OUTBOX` | Evento pendente (`tipoEvento`, `payload`). TTL de 1 dia |
| `COMPENSACAO#{id}` | `COMPENSACAO` | Controle de idempotência. TTL de 30 dias |

O `GET /ordens-servico` consulta o GSI1 com `ORDEM_SERVICO#ATIVA`, o mesmo filtro do monólito (exclui Finalizada/Entregue).

## Deploy

### 1. AWS Lambda + CloudFormation

Pré-requisitos: AWS CLI autenticada, .NET 8 SDK e o parâmetro SSM da chave JWT já existente (é o mesmo do EKS).

```bash
cd os-microservice
./scripts/deploy.sh                                  # descobre o ALB da API core automaticamente
CORE_API_BASE_URL=http://meu-alb... ./scripts/deploy.sh
RESEND_SSM_PARAM=/techchallenge/prod/resend_api_key ./scripts/deploy.sh
```

O script roda os testes, faz `dotnet publish` para `linux-arm64`, envia o zip ao S3 e aplica `infra/template.yaml` via `aws cloudformation deploy`.

### 2. Container / Kubernetes (EKS via Kustomize)

Além do empacotamento serverless via CloudFormation, o serviço também é empacotado como container ASP.NET Core (porta `8080`, usuário non-root `$APP_UID` em [`Dockerfile`](Dockerfile)) e publicado no GHCR (`ghcr.io/soat-fiap-2026/os-microservice:latest`) pelo workflow [`.github/workflows/cd.yml`](.github/workflows/cd.yml).

Os manifestos Kubernetes em [`k8s/base/`](k8s/base/kustomization.yaml) provisionam:

- [`configmap.yaml`](k8s/base/configmap.yaml) (`os-microservice-config`): configura `TABLE_NAME`, `CORE_API_BASE_URL`, `OS_EVENTOS_QUEUE_URL`, `COMPENSACAO_QUEUE_URL`, `EVENTOS_TOPIC_ARN`, `EVENTO_ORIGEM`, `AWS_REGION`, `JWT_ISSUER`, `JWT_AUDIENCE`, `JWT_SECRET_SSM_PARAMETER` e `OTEL_SERVICE_NAME`.
- [`deployment.yaml`](k8s/base/deployment.yaml) (`os-microservice`): `Deployment` non-root com `livenessProbe` e `readinessProbe` em `/api/v1/ordens-servico/health` (porta `8080`), secret opcional `os-microservice-secrets` (`JWT_SECRET`) e fallback via AWS SSM Parameter Store. Também expõe `/health`, `/health/live`, `/health/ready` e `/metrics` (Prometheus).
- [`service.yaml`](k8s/base/service.yaml) (`os-microservice`): `ClusterIP` expondo a porta `8080`.

Para subir o ecossistema completo localmente em containers (com DynamoDB, SNS, SQS e SSM simulados via Floci, API disponível em `http://localhost:8083`):

```bash
docker compose -f ../shared/docker-compose.yml up -d --build
```

### 3. Consumo assíncrono em container: `OsSqsBackgroundWorker`

O [`OsSqsBackgroundWorker`](src/OrdemServico.Lambda/Infrastructure/Messaging/OsSqsBackgroundWorker.cs) é registrado via [`DependencyInjection.cs`](src/OrdemServico.Lambda/Configuration/DependencyInjection.cs) como `BackgroundService` para viabilizar o consumo de eventos da Saga tanto no modo container/Kubernetes quanto no modo AWS Lambda:

- **Detecção automática de ambiente:** se a variável `AWS_LAMBDA_FUNCTION_NAME` estiver definida, o worker encerra o loop imediatamente, mantendo o consumo via triggers nativos da AWS Lambda (`CompensacaoEventoFunction`, `CompensacaoFilaFunction`, `OutboxPublisherFunction`).
- **Polling SQS em modo container/K8s:** quando fora do ambiente Lambda, realiza long-polling na fila configurada em `OS_EVENTOS_QUEUE_URL` (ou `OS_QUEUE_URL` / `Aws:OsEventosQueueUrl`), propaga `X-Correlation-ID` e `idOrdemServico` via `SnsCorrelationHelper` / `CorrelationContext` e processa os eventos:
  - `DiagnosticoIniciadoEvent` → avança a OS para `EM_DIAGNOSTICO`.
  - `DiagnosticoTecnicoConcluidoEvent` → sincroniza itens sugeridos e avança a OS para `AGUARDANDO_APROVACAO`.
  - `OrcamentoGeradoEvent` → atualiza `ValorOrcamento` e avança a OS para `AGUARDANDO_APROVACAO`.
  - `PagamentoConfirmadoEvent` → avança a OS para `ORCAMENTO_APROVADO` e publica `OrdemServicoLiberadaParaExecucaoEvent` no SNS (`SnsPublisher`).
  - `ProducaoIniciadaEvent` e `ProducaoFinalizadaEvent` → atualizam itens/status da OS para `EM_EXECUCAO` e `FINALIZADA`.
  - Eventos de compensação (`PagamentoCanceladoCompensacaoEvent`, `PagamentoRecusadoEvent`, `ProducaoCanceladaEvent`, `OrcamentoReprovado`, `OrdemServicoCanceladaEvent`) → acionam `CompensacaoService` (`CancelarOrdemServico`) com chave idempotente `sqs-<eventId>`.

Testar as compensações manualmente:

```bash
# por fila
aws sqs send-message --queue-url <CompensacaoQueueUrl> --message-body file://events/sqs-estornar-estoque.json
# por evento
aws sns publish --topic-arn <EventosTopicArn> --cli-input-json file://events/sns-reserva-estoque-falhou.json
aws sns publish --topic-arn <EventosTopicArn> --cli-input-json file://events/sns-pagamento-recusado.json
```

## Evidências de Testes e Cobertura de Código (≥ 80%)

### Resultados verificados

Execução validada com [`coverlet.runsettings`](coverlet.runsettings) (excluindo `[*Tests*]*`, `[Fiap.TechChallenge.Contracts]*` e `**/Migrations/*.cs`) sobre o projeto [`OrdemServico.Lambda.Tests`](tests/OrdemServico.Lambda.Tests/OrdemServico.Lambda.Tests.csproj):

| Métrica | Valor observado |
|---|---|
| **Testes executados** | **103** (`Passed: 103`, `Failed: 0`, `Skipped: 0`) |
| **Cobertura de linhas (`line-rate`)** | **87,73%** (`0.8773` — `1795` de `2046` linhas válidas cobertas) |
| **Cobertura de branches (`branch-rate`)** | **74,67%** (`0.7467` — `625` de `837` branches cobertos) |

### Comandos para reprodução local

```bash
dotnet build src/OrdemServico.Lambda/OrdemServico.Lambda.csproj --configuration Release
dotnet build tests/OrdemServico.Lambda.Tests/OrdemServico.Lambda.Tests.csproj --configuration Release
dotnet test tests/OrdemServico.Lambda.Tests/OrdemServico.Lambda.Tests.csproj \
  --configuration Release \
  --no-build \
  --settings coverlet.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory /tmp/os-test-results
```

### Qualidade contínua (CI/CD e SonarCloud)

- **SonarCloud:** workflow [`.github/workflows/sonar.yml`](.github/workflows/sonar.yml) analisa o projeto `SOAT-FIAP-2026_os-microservice` (organização `soat-fiap-2026`), enviando `TestResults/**/coverage.opencover.xml`.
- **CI / CD:** [`.github/workflows/ci.yml`](.github/workflows/ci.yml) executa build, testes com cobertura e validação do build Docker; [`.github/workflows/cd.yml`](.github/workflows/cd.yml) publica a imagem container em `ghcr.io/soat-fiap-2026/os-microservice`.

## Diferenças em relação ao monólito

| Ponto | Monólito | Lambda |
|---|---|---|
| Baixa de estoque em `pecas-insumos` | Mesma transação do EF Core | Saga com compensação (estorno via SQS) |
| Orçamento reprovado | Só muda o status | Muda o status **e** estorna o estoque das peças (evento `OrcamentoReprovado`) |
| Status `ORCAMENTO_APROVADO/REPROVADO` | Não estão no `DatabaseSeed` (a aprovação falharia com "status não encontrado") | Catálogo em código com os mesmos Ids do seed, mais esses dois |
| Cliente, serviço e peça | Join no PostgreSQL | Lidos da API core e guardados como snapshot na OS (nome, valor, tempo estimado, e-mail) |
| OS cancelada ou com orçamento reprovado | Status não existe no fluxo | Não aceita incluir/remover itens nem iniciar/finalizar serviços (400) |
| Conflito de gravação concorrente | Último a gravar vence | 409 (concorrência otimista) |
| Destinatário do e-mail | Fixo no código | E-mail do cliente, ou `EMAIL_DESTINO_OVERRIDE` |
| Métricas Prometheus | `IObservabilityMetrics` | Logs estruturados (`Etapa ... concluída em N segundos`) consultáveis no CloudWatch Logs Insights |

Continua igual ao monólito: remover uma peça da OS **não** devolve o estoque. Também não foi migrado o `GET /api/v1/servicos/metricas-tempo`: ele pertence ao domínio de Serviço e ainda lê os itens de OS do PostgreSQL.
