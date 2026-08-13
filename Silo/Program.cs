using Microsoft.Extensions.AI;
using OpenAI;
using Orleans.Dashboard;
using ServiceDefaults;
using Silo.Components;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Orleans.Clustering.Redis / Orleans.Persistence.Redis resolve their Redis connection via a
// keyed IConnectionMultiplexer - keyed "redis" to match the resource name AppHost gives the
// Redis container (see AppHost.cs: builder.AddRedis("redis")). Without this registration,
// AddRedisClustering's keyed service lookup fails at startup.
builder.AddKeyedRedisClient(name: "redis");

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.AddDashboard();
});

builder.AddKeyedOpenAIClient(name: "chat");
builder.AddKeyedOpenAIClient(name: "embeddings");

// Wrap the raw OpenAI chat client in Microsoft.Extensions.AI's IChatClient pipeline so it goes
// through UseOpenTelemetry(...) - the raw OpenAIClient/ChatClient's own native instrumentation is
// opt-in (OPENAI_EXPERIMENTAL_ENABLE_OPEN_TELEMETRY) and, even enabled, only emits gen_ai.* request
// metadata - never prompt/completion content. EnableSensitiveData=true is what actually logs full
// conversations as span events; keep it off outside dev if prompts/completions may carry PII.
builder.Services.AddKeyedSingleton<IChatClient>("chat", (sp, _) =>
    sp.GetRequiredKeyedService<OpenAIClient>("chat")
        .GetChatClient("gpt-5.6-luna")
        .AsIChatClient()
        .AsBuilder()
        .UseOpenTelemetry(sourceName: "RulesEvaluator.Chat", configure: o => o.EnableSensitiveData = true)
        .Build());

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapOrleansDashboard("/dashboard");
app.Run();