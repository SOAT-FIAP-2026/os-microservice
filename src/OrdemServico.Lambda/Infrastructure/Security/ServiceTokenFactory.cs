using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace OrdemServico.Lambda.Infrastructure.Security
{
    /// <summary>
    /// Emite um JWT de serviço (curta duração) para chamadas à API core feitas fora de uma
    /// requisição de usuário, como as compensações disparadas por fila ou evento.
    /// Usa o mesmo formato de claims do TokenService do monólito.
    /// </summary>
    public class ServiceTokenFactory(JwtSettings settings)
    {
        private readonly JwtSettings _settings = settings;

        public string Gerar()
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            byte[] key = Encoding.UTF8.GetBytes(_settings.Secret);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "lambda-ordem-servico"),
                    new Claim("UsuarioId", Guid.Empty.ToString()),
                    new Claim(ClaimTypes.Role, "Administrador")
                ]),
                Expires = DateTime.UtcNow.AddMinutes(5),
                Issuer = _settings.Issuer,
                Audience = _settings.Audience,
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };

            return tokenHandler.WriteToken(tokenHandler.CreateToken(tokenDescriptor));
        }
    }
}
