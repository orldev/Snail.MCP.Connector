using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Extensions;

/// <summary>The queries feature: the session's copy, the sources in Vault, MongoDB, the journal and the table files.</summary>
public static class QueryRegistrationExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>Everything a tool reaches through <see cref="Queries"/>.</summary>
        public IServiceCollection AddQueries()
        {
            services.AddSingleton(TimeProvider.System);
            services.AddHttpContextAccessor();
            services.AddHttpClient(VaultSources.Client, client => client.Timeout = TimeSpan.FromSeconds(15));
            services.AddSingleton<SessionCopy>();
            services.AddSingleton<SessionGrant>();
            services.AddSingleton<ISources, VaultSources>();
            services.AddSingleton<IDocuments, MongoDocuments>();
            services.AddSingleton<ITables, SqlTables>();
            services.AddSingleton<IQueryJournal>(provider => FileQueryJournal.InDataFolder(provider.GetRequiredService<IOptions<ConnectorOptions>>()));
            services.AddSingleton<ITableFiles, TableFiles>();
            services.AddSingleton<IProposals>(provider => FileProposals.InDataFolder(provider.GetRequiredService<IOptions<ConnectorOptions>>()));
            services.AddSingleton<Queries>();
            services.AddSingleton<Writes>();

            return services;
        }
    }
}
