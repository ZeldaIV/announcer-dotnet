using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Announcer;

/// <summary>Registers <see cref="AnnouncerClient"/> with the DI container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AnnouncerClient"/> as a typed
    /// <see cref="IHttpClientFactory"/> client, so it gets pooled handlers and
    /// DNS refresh for free.
    ///
    /// <code>
    /// builder.Services.AddAnnouncer(options =>
    /// {
    ///     options.ApiKey = builder.Configuration["Announcer:ApiKey"];
    /// });
    /// </code>
    ///
    /// Leave <see cref="AnnouncerOptions.ApiKey"/> unset to read the
    /// <c>ANNOUNCER_API_KEY</c> environment variable instead. Then inject
    /// <see cref="AnnouncerClient"/> anywhere.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>
    /// The <see cref="IHttpClientBuilder"/>, so you can chain Polly handlers or
    /// a custom primary handler.
    /// </returns>
    public static IHttpClientBuilder AddAnnouncer(
        this IServiceCollection services,
        Action<AnnouncerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            // Still register the options so the IOptions<> constructor resolves;
            // the client then falls back to the environment for everything.
            services.Configure<AnnouncerOptions>(_ => { });
        }

        // The factory is spelled out rather than left to the activator: it makes
        // the constructor choice explicit at registration instead of resolved
        // by reflection at first use.
        return services
            .AddHttpClient<AnnouncerClient>()
            .AddTypedClient((httpClient, provider) => new AnnouncerClient(
                provider.GetRequiredService<IOptions<AnnouncerOptions>>().Value,
                httpClient));
    }
}
