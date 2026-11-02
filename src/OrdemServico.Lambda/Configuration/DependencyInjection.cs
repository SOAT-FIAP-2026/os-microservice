using Amazon.DynamoDBv2;
using Amazon.SimpleNotificationService;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.SQS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrdemServico.Lambda.Application.Ports;
using OrdemServico.Lambda.Application.Saga;
using OrdemServico.Lambda.Application.Services;
using OrdemServico.Lambda.Infrastructure.Email;
using OrdemServico.Lambda.Infrastructure.Http;
using OrdemServico.Lambda.Infrastructure.Messaging;
using OrdemServico.Lambda.Infrastructure.Persistence;
using OrdemServico.Lambda.Infrastructure.Security;

namespace OrdemServico.Lambda.Configuration
{
    /// <summary>
    /// Composição compartilhada pelas quatro funções do pacote (API, compensação via fila,
    /// compensação via evento e publicador do outbox).
    /// </summary>
    public static class DependencyInjection
    {
        public static IServiceCollection AddOrdemServicoLambda(this IServiceCollection services, IConfiguration configuration)
        {
            LambdaSettings settings = LambdaSettings.FromConfiguration(configuration);
            services.AddSingleton(settings);

            services.AddDefaultAWSOptions(configuration.GetAWSOptions());
            services.AddAWSService<IAmazonDynamoDB>();
            services.AddAWSService<IAmazonSQS>();
            services.AddAWSService<IAmazonSimpleNotificationService>();
            services.AddAWSService<IAmazonSimpleSystemsManagement>();

            // Segredos resolvidos sob demanda: a OutboxPublisherFunction não precisa deles.
            services.AddSingleton(sp => JwtSettings.Resolve(configuration, sp.GetRequiredService<IAmazonSimpleSystemsManagement>()));
            services.AddSingleton<ServiceTokenFactory>();
            services.AddSingleton(sp => new Lazy<ServiceTokenFactory>(sp.GetRequiredService<ServiceTokenFactory>));

            services.AddHttpContextAccessor();
            services.AddSingleton<ICoreApiAuthorization, CoreApiAuthorization>();
            services.AddHttpClient<CoreApiClient>(client =>
            {
                if (!string.IsNullOrWhiteSpace(settings.CoreApiBaseUrl))
                    client.BaseAddress = new Uri(settings.CoreApiBaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(10);
            });
            services.AddTransient<ICatalogoGateway>(sp => sp.GetRequiredService<CoreApiClient>());
            services.AddTransient<IEstoqueGateway>(sp => sp.GetRequiredService<CoreApiClient>());

            // 3 tentativas precisam caber nos 29 s da API: o e-mail sai depois de a OS já ter sido gravada.
            services.AddHttpClient(nameof(ResendEmailService), client => client.Timeout = TimeSpan.FromSeconds(5));
            services.AddSingleton<IEmailService>(sp => new ResendEmailService(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ResendEmailService)),
                settings,
                ResolverResendApiKey(configuration, sp.GetRequiredService<IAmazonSimpleSystemsManagement>()),
                sp.GetRequiredService<ILogger<ResendEmailService>>()));

            services.AddSingleton<IOrdemServicoRepository, OrdemServicoRepository>();
            services.AddSingleton<ISagaRepository, SagaRepository>();
            services.AddSingleton<IRegistroCompensacaoRepository, RegistroCompensacaoRepository>();
            services.AddSingleton<IFilaCompensacao, SqsFilaCompensacao>();
            services.AddSingleton<SnsPublisher>();

            services.AddScoped<ReservaEstoqueSaga>();
            services.AddScoped<CompensacaoService>();
            services.AddScoped<IOrdemServicoService, OrdemServicoService>();

            return services;
        }

        /// <summary>Provider para as funções que não usam o host do ASP.NET Core.</summary>
        public static ServiceProvider BuildWorkerProvider()
        {
            IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddJsonConsole(options => options.IncludeScopes = true));
            services.AddOrdemServicoLambda(configuration);
            return services.BuildServiceProvider();
        }

        private static string? ResolverResendApiKey(IConfiguration configuration, IAmazonSimpleSystemsManagement ssm)
        {
            string? apiKey = configuration["RESEND_API_KEY"];
            string? parametro = configuration["RESEND_API_KEY_SSM_PARAMETER"];

            if (!string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(parametro))
                return apiKey;

            GetParameterResponse response = ssm.GetParameterAsync(new GetParameterRequest { Name = parametro, WithDecryption = true })
                .GetAwaiter().GetResult();
            return response.Parameter.Value;
        }
    }
}
