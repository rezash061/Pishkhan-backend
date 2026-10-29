using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Pishkhan.Web.Hosting;

public static class RealtimeExtensions
{
    /// <summary>
    /// SignalR with the Redis backplane on the state instance, so a message sent by any Api replica reaches clients connected
    /// to every replica. Channels are prefixed with <c>Redis:KeyPrefix</c> so environments sharing a Redis never cross.
    /// The connection string is read when the host starts (tests and the OpenAPI export add configuration late).
    /// </summary>
    public static ISignalRServerBuilder AddPishkhanRealtime(this IServiceCollection services)
    {
        var builder = services
            .AddSignalR(o =>
            {
                o.MaximumReceiveMessageSize = 32 * 1024;
                o.KeepAliveInterval = TimeSpan.FromSeconds(15);
                o.ClientTimeoutInterval = TimeSpan.FromSeconds(40);
                o.EnableDetailedErrors = false;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase)
            .AddStackExchangeRedis();
        services.AddOptions<RedisOptions>().Configure<IConfiguration>((o, configuration) =>
        {
            var connectionString = configuration.GetConnectionString("RedisState") ?? "localhost";
            o.Configuration = ConfigurationOptions.Parse(connectionString);
            o.Configuration.AbortOnConnectFail = false;
            o.Configuration.ChannelPrefix = RedisChannel.Literal((configuration["Redis:KeyPrefix"] ?? "dev:v1") + ":signalr");
        });
        return builder;
    }
}
