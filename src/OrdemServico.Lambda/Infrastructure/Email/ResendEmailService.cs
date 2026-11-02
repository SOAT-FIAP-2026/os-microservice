using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Configuration;

namespace OrdemServico.Lambda.Infrastructure.Email
{
    /// <summary>
    /// Mesma integração do monólito (Resend, 3 tentativas). O destinatário fixo do plano gratuito
    /// virou configuração: EMAIL_DESTINO_OVERRIDE. Sem RESEND_API_KEY, não envia e retorna false.
    /// </summary>
    public class ResendEmailService(HttpClient httpClient, LambdaSettings settings, string? apiKey, ILogger<ResendEmailService> logger) : IEmailService
    {
        private readonly HttpClient _httpClient = httpClient;
        private readonly LambdaSettings _settings = settings;
        private readonly string? _apiKey = apiKey;
        private readonly ILogger<ResendEmailService> _logger = logger;

        public async Task<bool> EnviarEmailAsync(string para, string assunto, string corpoHtml)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                _logger.LogWarning("RESEND_API_KEY não configurada; notificação por e-mail ignorada.");
                return false;
            }

            var payload = new
            {
                from = _settings.EmailRemetente,
                to = string.IsNullOrWhiteSpace(_settings.EmailDestinoOverride) ? para : _settings.EmailDestinoOverride,
                subject = assunto,
                html = corpoHtml
            };

            int maxRetries = 3;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
                    {
                        Content = JsonContent.Create(payload)
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                    using HttpResponseMessage response = await _httpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    return true;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    if (i == maxRetries - 1)
                    {
                        _logger.LogError(exception, "Falha na integração com o Resend ao enviar e-mail");
                        return false;
                    }

                    await Task.Delay(1000);
                }
            }

            return false;
        }
    }
}
