using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Configuration;

namespace OrdemServico.Lambda.Infrastructure.Security
{
    public sealed record JwtSettings(string Secret, string? Issuer, string? Audience)
    {
        public const int MinimumLength = 32;

        /// <summary>
        /// Mesma precedência do JwtSecretResolver do monólito: JWT_SECRET tem prioridade.
        /// Na AWS a chave vem do SSM Parameter Store (JWT_SECRET_SSM_PARAMETER, SecureString),
        /// o mesmo parâmetro usado pelo deploy do EKS, para que os tokens da API core e da
        /// Lambda de autenticação sejam aceitos aqui.
        /// </summary>
        public static JwtSettings Resolve(IConfiguration configuration, IAmazonSimpleSystemsManagement ssm)
        {
            string? secret = configuration["JWT_SECRET"];

            if (string.IsNullOrWhiteSpace(secret) && !string.IsNullOrWhiteSpace(configuration["JWT_SECRET_SSM_PARAMETER"]))
            {
                GetParameterResponse response = ssm.GetParameterAsync(new GetParameterRequest
                {
                    Name = configuration["JWT_SECRET_SSM_PARAMETER"],
                    WithDecryption = true
                }).GetAwaiter().GetResult();

                secret = response.Parameter.Value;
            }

            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("Chave de assinatura do JWT não configurada. Defina JWT_SECRET ou JWT_SECRET_SSM_PARAMETER.");

            if (secret.Length < MinimumLength)
                throw new InvalidOperationException($"A chave de assinatura do JWT precisa ter pelo menos {MinimumLength} caracteres.");

            return new JwtSettings(secret, configuration["JWT_ISSUER"], configuration["JWT_AUDIENCE"]);
        }
    }
}
