using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FenixAlliance.ABP.Proxy
{
    /// <summary>
    /// Proxy extensions for <see cref="IApplicationBuilder"/>.
    /// </summary>
    public static class ProxyExtensions
    {
        /// <summary>
        /// A <see cref="Controller"/> extension method which allows for a single, simple call to use a proxy
        /// in existing controllers.
        /// </summary>
        /// <param name="controller">The calling controller.</param>
        /// <param name="uri">The URI to proxy.</param>
        /// <param name="options">Extra options to apply during proxying.</param>
        /// <returns>
        /// A <see cref="Task"/> which, upon completion, has proxied the specified address and copied the response contents into
        /// the response for the <see cref="HttpContext"/>.
        /// </returns>
        public static Task ProxyAsync(this ControllerBase controller, string uri, ProxyOptions options = null)
        {
            return controller.HttpContext.ExecuteProxyOperationAsync(uri, options);
        }

        /// <summary>
        /// Adds the required services needed for proxying requests.
        /// </summary>
        /// <param name="services">The application service collection.</param>
        /// <param name="configureProxyClient">An <see cref="Action"/> that can override the underlying `HttpClient` used for proxied calls.</param>
        /// <returns>The same instance.</returns>
        public static IServiceCollection AddProxies(this IServiceCollection services, Action<HttpClient> configureProxyClient = null)
        {
            if(configureProxyClient != null)
                services.AddHttpClient(Helpers.HttpProxyClientName, configureProxyClient);
            else
                services.AddHttpClient(Helpers.HttpProxyClientName);

            return services;
        }

        /// <summary>
        /// Middleware which instructs the runtime to detect static methods with [<see cref="ProxyRouteAttribute"/>] and route them.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>  
        public static void UseProxies(this IApplicationBuilder app)
        {
            var methods = Helpers.GetReferencingAssemblies().SelectMany(a => a.GetTypes()).SelectMany(t => t.GetMethods()).Where(m => m.GetCustomAttributes(typeof(ProxyRouteAttribute), false).Length > 0);

            foreach(var method in methods)
            {
                var name = $"{method.DeclaringType}.{method.Name}";
                var attribute = method.GetCustomAttributes(typeof(ProxyRouteAttribute), false).First() as ProxyRouteAttribute;
                var parameters = method.GetParameters();

                if(method.ReturnType != typeof(Task<string>) && method.ReturnType != typeof(string))
                    throw new InvalidOperationException($"Proxied generator method ({name}) must return a `Task<string>` or `string`.");

                if(!method.IsStatic)
                    throw new InvalidOperationException($"Proxied generator method ({name}) must be static.");

                if (attribute != null)
                    app.UseProxy(attribute.Route, args =>
                    {
                        if (args.Count() != parameters.Count())
                            throw new InvalidOperationException(
                                $"Proxied generator method ({name}) parameter mismatch.");

                        var castedArgs = args.Zip(parameters,
                            (a, p) => new
                            {
                                ArgumentValue = a.Value.ToString(), ArgumentType = p.ParameterType,
                                ParameterName = p.Name
                            }).Select(z =>
                        {
                            try
                            {
                                return TypeDescriptor.GetConverter(z.ArgumentType).ConvertFromString(z.ArgumentValue);
                            }
                            catch (Exception)
                            {
                                throw new InvalidOperationException(
                                    $"Proxied generator method ({name}) cannot cast to {z.ArgumentType.FullName} for parameter {z.ParameterName}.");
                            }
                        });

                        // Make sure to always return a `Task<string>`, but allow methods that just return a `string`.

                        if (method.ReturnType == typeof(Task<string>))
                            return method.Invoke(null, castedArgs.ToArray()) as Task<string>;

                        return Task.FromResult(method.Invoke(null, castedArgs.ToArray()) as string);
                    });
            }
        }

        #region RunProxy Overloads

        /// <summary>
        /// Terminating middleware which creates a proxy over a specified endpoint.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="proxiedAddress">The proxied address.</param>
        /// <param name="options">Extra options to apply during proxying.</param>
        public static void RunProxy(this IApplicationBuilder app, string proxiedAddress, ProxyOptions options = null)
        {
            app.Run(context =>
            {
                return context.ExecuteProxyOperationAsync($"{proxiedAddress}{context.Request.Path}", options);
            });
        }

        /// <summary>
        /// Terminating middleware which creates a proxy over a specified endpoint.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="getProxiedAddress">A lambda { (context) => <see cref="string"/> } which returns the address to which the request is proxied.</param>
        /// <param name="options">Extra options to apply during proxying.</param>
        public static void RunProxy(this IApplicationBuilder app, Func<HttpContext, string> getProxiedAddress, ProxyOptions options = null)
        {
            app.Run(context =>
            {
                return context.ExecuteProxyOperationAsync($"{getProxiedAddress(context)}{context.Request.Path}", options);
            });
        }

        #endregion

        #region UseProxy Overloads

        /// <summary>
        /// Middleware which creates an ad hoc proxy over a specified endpoint.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="endpoint">The local route endpoint.</param>
        /// <param name="proxiedAddress">The proxied address.</param>
        /// <param name="options">Extra options to apply during proxying.</param>
        public static void UseProxy(this IApplicationBuilder app, string endpoint, string proxiedAddress, ProxyOptions options = null)
        {
            UseProxy_GpaSync(
                app, 
                endpoint, 
                (context, args) => proxiedAddress, 
                options);
        }

        /// <summary>
        /// Middleware which creates an ad hoc proxy over a specified endpoint.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="endpoint">The local route endpoint.</param>
        /// <param name="getProxiedAddress">A lambda { (context, args) => <see cref="Task{String}"/> } which returns the address to which the request is proxied.</param>
        /// <param name="options">Extra options to apply during proxying.</param>
        public static void UseProxy(this IApplicationBuilder app, string endpoint, Func<HttpContext, IDictionary<string, object>, Task<string>> getProxiedAddress, ProxyOptions options = null)
        {
            UseProxy_GpaAsync(
                app, 
                endpoint, 
                (context, args) => getProxiedAddress(context, args), 
                options);
        }

        /// <summary>
        /// Middleware which creates an ad hoc proxy over a specified endpoint.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="endpoint">The local route endpoint.</param>
        /// <param name="getProxiedAddress">A lambda { (args) => <see cref="Task{String}"/> } which returns the address to which the request is proxied.</param>
        /// <param name="options">Extra options to apply during proxying.</param>
        public static void UseProxy(this IApplicationBuilder app, string endpoint, Func<IDictionary<string, object>, Task<string>> getProxiedAddress, ProxyOptions options = null)
        {
            UseProxy_GpaAsync(
                app, 
                endpoint, 
                (context, args) => getProxiedAddress(args), 
                options);
        }

        /// <summary>
        /// Middleware which creates an ad hoc proxy over a specified endpoint.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="endpoint">The local route endpoint.</param>
        /// <param name="getProxiedAddress">A lambda { () => <see cref="Task{String}"/> } which returns the address to which the request is proxied.</param>
        /// <param name="options">Extra options to apply during proxying.</param>
        public static void UseProxy(this IApplicationBuilder app, string endpoint, Func<Task<string>> getProxiedAddress, ProxyOptions options = null)
        {
            UseProxy_GpaAsync(
                app, 
                endpoint, 
                (context, args) => getProxiedAddress(), 
                options);
        }

        /// <summary>
        /// Middleware which creates an ad hoc proxy over a specified endpoint.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="endpoint">The local route endpoint.</param>
        /// <param name="getProxiedAddress">A lambda { (context, args) => <see cref="string"/> } which returns the address to which the request is proxied.</param>
        /// <param name="options">Extra options to apply during proxying.</param>
        public static void UseProxy(this IApplicationBuilder app, string endpoint, Func<HttpContext, IDictionary<string, object>, string> getProxiedAddress, ProxyOptions options = null)
        {
            UseProxy_GpaSync(
                app, 
                endpoint, 
                (context, args) => getProxiedAddress(context, args), 
                options);
        }

        /// <summary>
        /// Middleware which creates an ad hoc proxy over a specified endpoint.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="endpoint">The local route endpoint.</param>
        /// <param name="getProxiedAddress">A lambda { (args) => <see cref="string"/> } which returns the address to which the request is proxied.</param>
        /// <param name="options">Extra options to apply during proxying.</param>
        public static void UseProxy(this IApplicationBuilder app, string endpoint, Func<IDictionary<string, object>, string> getProxiedAddress, ProxyOptions options = null)
        {
            UseProxy_GpaSync(
                app, 
                endpoint, 
                (context, args) => getProxiedAddress(args), 
                options);
        }

        /// <summary>
        /// Middleware which creates an ad hoc proxy over a specified endpoint.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="endpoint">The local route endpoint.</param>
        /// <param name="getProxiedAddress">A lambda { () => <see cref="string"/> } which returns the address to which the request is proxied.</param>
        /// <param name="options">Extra options to apply during proxying.</param>
        public static void UseProxy(this IApplicationBuilder app, string endpoint, Func<string> getProxiedAddress, ProxyOptions options = null)
        {
            UseProxy_GpaSync(
                app, 
                endpoint, 
                (context, args) => getProxiedAddress(), 
                options);
        }

        // Provides the "default" implementation for `UseProxy` where the proxied address is computed asynchronously.
        private static void UseProxy_GpaAsync(this IApplicationBuilder app, string endpoint, Func<HttpContext, IDictionary<string, object>, Task<string>> getProxiedAddress, ProxyOptions options = null)
        {
            app.UseRouter(builder => {
                builder.MapMiddlewareRoute(endpoint, proxyApp => {
                    proxyApp.Run(async context => {
                        var uri = await getProxiedAddress(context, context.GetRouteData().Values.ToDictionary(v => v.Key, v => v.Value)).ConfigureAwait(false);
                        await context.ExecuteProxyOperationAsync(uri, options);
                    });
                });
            });
        }

        // Provides the "default" implementation for `UseProxy` where the proxied address is computed synchronously.
        private static void UseProxy_GpaSync(this IApplicationBuilder app, string endpoint, Func<HttpContext, IDictionary<string, object>, string> getProxiedAddress, ProxyOptions options = null)
        {
            app.UseRouter(builder => {
                builder.MapMiddlewareRoute(endpoint, proxyApp => {
                    proxyApp.Run(async context => {
                        var uri = getProxiedAddress(context, context.GetRouteData().Values.ToDictionary(v => v.Key, v => v.Value));
                        await context.ExecuteProxyOperationAsync(uri, options);
                    });
                });
            });
        }

        #endregion
    }
}