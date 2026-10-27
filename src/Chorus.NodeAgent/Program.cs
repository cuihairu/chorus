using Chorus.NodeAgent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

await Host.CreateDefaultBuilder(args)
    .UseSystemd()
    .UseWindowsService()
    .ConfigureServices(services => services.AddHostedService<BootstrapAgent>())
    .Build()
    .RunAsync();
