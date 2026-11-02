#!/usr/bin/env bash
# ==============================================================================
# deploy.sh — Build, empacotamento e deploy da Lambda de Ordem de Serviço
# ==============================================================================
# Uso:
#   ./scripts/deploy.sh
#
# Variáveis (todas opcionais):
#   STAGE              ambiente (padrão: prod)
#   AWS_REGION         região (padrão: sa-east-1)
#   ARTIFACTS_BUCKET   bucket S3 do pacote (padrão: techchallenge-lambda-artifacts-<conta>-<região>, criado se não existir)
#   CORE_API_BASE_URL  URL da API core no EKS (padrão: DNS do ALB descoberto via AWS CLI)
#   JWT_SSM_PARAM      parâmetro SSM da chave JWT (padrão: /techchallenge/prod/jwt_secret)
#   RESEND_SSM_PARAM   parâmetro SSM da API key do Resend (padrão: vazio = e-mail desativado)
#   EMAIL_DESTINO_OVERRIDE  destinatário fixo dos e-mails (padrão: vazio)
# ==============================================================================
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE="${STAGE:-prod}"
AWS_REGION="${AWS_REGION:-sa-east-1}"
STACK_NAME="techchallenge-ordem-servico-${STAGE}"
JWT_SSM_PARAM="${JWT_SSM_PARAM:-/techchallenge/prod/jwt_secret}"
RESEND_SSM_PARAM="${RESEND_SSM_PARAM:-}"
EMAIL_DESTINO_OVERRIDE="${EMAIL_DESTINO_OVERRIDE:-}"

ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
ARTIFACTS_BUCKET="${ARTIFACTS_BUCKET:-techchallenge-lambda-artifacts-${ACCOUNT_ID}-${AWS_REGION}}"

echo "🚀 Deploy da Lambda de Ordem de Serviço (stack: ${STACK_NAME}, região: ${AWS_REGION})"

# --- 1. URL da API core -------------------------------------------------------
if [ -z "${CORE_API_BASE_URL:-}" ]; then
  ALB_DNS=$(aws elbv2 describe-load-balancers --region "$AWS_REGION" \
    --query "LoadBalancers[?contains(LoadBalancerName, 'alb')].DNSName" \
    --output text 2>/dev/null | awk '{print $1}' || true)

  if [ -z "$ALB_DNS" ] || [ "$ALB_DNS" == "None" ]; then
    echo "❌ Não foi possível descobrir o ALB da API core. Defina CORE_API_BASE_URL."
    exit 1
  fi
  CORE_API_BASE_URL="http://${ALB_DNS}"
fi
echo "🔗 API core: ${CORE_API_BASE_URL}"

# --- 2. Testes, build e pacote ------------------------------------------------
echo "🧪 Executando testes..."
dotnet test "$ROOT_DIR/OrdemServico.Lambda.sln" --configuration Release --nologo -v q

echo "📦 Publicando para linux-arm64..."
PUBLISH_DIR="$ROOT_DIR/artifacts/publish"
rm -rf "$ROOT_DIR/artifacts"
dotnet publish "$ROOT_DIR/src/OrdemServico.Lambda/OrdemServico.Lambda.csproj" \
  --configuration Release --runtime linux-arm64 --self-contained false \
  --output "$PUBLISH_DIR" --nologo -v q

ZIP_FILE="$ROOT_DIR/artifacts/ordem-servico-lambda.zip"
(cd "$PUBLISH_DIR" && zip -qr "$ZIP_FILE" .)

# Chave com hash do conteúdo: cada build novo atualiza o código das funções.
CODE_HASH=$(shasum -a 256 "$ZIP_FILE" | cut -c1-16)
CODE_KEY="ordem-servico/${STAGE}/ordem-servico-lambda-${CODE_HASH}.zip"

# --- 3. Upload para o S3 ------------------------------------------------------
if ! aws s3api head-bucket --bucket "$ARTIFACTS_BUCKET" 2>/dev/null; then
  echo "🪣 Criando bucket ${ARTIFACTS_BUCKET}..."
  aws s3api create-bucket --bucket "$ARTIFACTS_BUCKET" --region "$AWS_REGION" \
    --create-bucket-configuration LocationConstraint="$AWS_REGION" >/dev/null
  aws s3api put-public-access-block --bucket "$ARTIFACTS_BUCKET" \
    --public-access-block-configuration BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
fi

echo "⬆️  Enviando s3://${ARTIFACTS_BUCKET}/${CODE_KEY}"
aws s3 cp "$ZIP_FILE" "s3://${ARTIFACTS_BUCKET}/${CODE_KEY}" --region "$AWS_REGION" --only-show-errors

# --- 4. CloudFormation --------------------------------------------------------
echo "☁️  Aplicando CloudFormation..."
aws cloudformation deploy \
  --region "$AWS_REGION" \
  --stack-name "$STACK_NAME" \
  --template-file "$ROOT_DIR/infra/template.yaml" \
  --capabilities CAPABILITY_IAM \
  --no-fail-on-empty-changeset \
  --parameter-overrides \
    Stage="$STAGE" \
    CodeS3Bucket="$ARTIFACTS_BUCKET" \
    CodeS3Key="$CODE_KEY" \
    CoreApiBaseUrl="$CORE_API_BASE_URL" \
    JwtSecretSsmParameterName="$JWT_SSM_PARAM" \
    ResendApiKeySsmParameterName="$RESEND_SSM_PARAM" \
    EmailDestinoOverride="$EMAIL_DESTINO_OVERRIDE" \
  --tags Project=techchallenge Domain=ordem-servico

echo ""
echo "✅ Deploy concluído!"
aws cloudformation describe-stacks --region "$AWS_REGION" --stack-name "$STACK_NAME" \
  --query "Stacks[0].Outputs[].[OutputKey,OutputValue]" --output table
