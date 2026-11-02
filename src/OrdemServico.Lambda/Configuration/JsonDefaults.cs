using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrdemServico.Lambda.Configuration
{
    public static class JsonDefaults
    {
        /// <summary>camelCase, igual ao padrão do ASP.NET Core usado pela API do monólito.</summary>
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
