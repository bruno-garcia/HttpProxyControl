using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Swashbuckle.AspNetCore.Swagger;

namespace HttpProxyInfo
{
    public class Startup
    {
        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<Func<string, HttpClient>>(c =>
            {
                // Instance per proxy
                var clients = new ConcurrentDictionary<string, HttpClient>();
                return proxy =>
                    clients.GetOrAdd(proxy, p => new HttpClient(new HttpClientHandler
                    {
                        Proxy = new WebProxy(proxy, false)
                    }));
            });

            services.AddSingleton<HttpClient>(); // Proxyless client

            services.AddMonitoring(GetType().Assembly);

            services.AddMvc().AddJsonOptions(o => o.SerializerSettings.NullValueHandling = NullValueHandling.Ignore);
            services.AddCors(options =>
            {
                options.AddPolicy("AllowAnyGet",
                    builder => builder.AllowAnyOrigin()
                        .WithMethods("GET")
                        .AllowAnyHeader());
            });

            services.AddSwaggerGen(config =>
            {
                config.SwaggerDoc("v1", new Info
                {
                    Title = "HTTP Proxy information."
                });
                var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    Assembly.GetExecutingAssembly().GetName().Name + ".xml");
                config.IncludeXmlComments(filePath);
            });
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseCors("AllowAnyGet")
                .UseMonitoringEndpoint()
                .UseSwagger()
                .UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "HTTP Proxy Control"))
                .UseMvc();
        }
    }
}
