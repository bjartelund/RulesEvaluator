using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis").WithLifetime(ContainerLifetime.Persistent);
var orleans = builder.AddOrleans("default").WithClustering(redis).WithGrainStorage("Default", redis);

var openai = builder.AddOpenAI("openai");
var chat = openai.AddModel("chat", "gpt-5.6-luna");
var embeddings = openai.AddModel("embeddings", "text-embedding-3-large");

builder.AddProject<Silo>("silo").WithReference(orleans).WaitFor(redis)
    .WithReference(chat).WithReference(embeddings);

builder.Build().Run();