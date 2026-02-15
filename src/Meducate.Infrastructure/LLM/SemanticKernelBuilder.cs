using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;

namespace Meducate.Infrastructure.LLM;

internal static class SemanticKernelBuilder
{
    public static Kernel CreateKernel(IConfiguration config)
    {
        var openAiKey = config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(openAiKey))
            throw new InvalidOperationException("OpenAI API key is not configured.");

        var model = config["OpenAI:Model"] ?? "gpt-4o-mini";

        var builder = Kernel.CreateBuilder();

        builder.AddOpenAIChatCompletion(
            modelId: model,
            apiKey: openAiKey
        );

        return builder.Build();
    }
}
