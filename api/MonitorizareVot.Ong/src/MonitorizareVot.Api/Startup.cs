﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.PlatformAbstractions;
using MonitorizareVot.Ong.Api.Services;
using Serilog;
using SimpleInjector;
using SimpleInjector.Integration.AspNetCore;
using SimpleInjector.Integration.AspNetCore.Mvc;
using Swashbuckle.Swagger.Model;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using MonitorizareVot.Domain.Ong.Models;

namespace MonitorizareVot.Ong.Api
{
    public class Startup
    {
        private readonly Container container = new Container();

        public Startup(IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: true);

            if (env.EnvironmentName.EndsWith("Development", StringComparison.CurrentCultureIgnoreCase))
            {
                // For more details on using the user secret store see http://go.microsoft.com/fwlink/?LinkID=532709
                builder.AddUserSecrets();

                // This will push telemetry data through Application Insights pipeline faster, allowing you to view results immediately.
                builder.AddApplicationInsightsSettings(developerMode: true);
            }

            builder.AddEnvironmentVariables();
            Configuration = builder.Build();
        }

        public IConfigurationRoot Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container
        public void ConfigureServices(IServiceCollection services)
        {
            // Add framework services.
            services.AddApplicationInsightsTelemetry(Configuration);

            services.AddMvc();

            services.AddCors();

            services.AddSwaggerGen();

            services.ConfigureSwaggerGen(options =>
            {
                options.SingleApiVersion(new Info
                {
                    Version = "v1",
                    Title = "Monitorizare Vot - API ONG",
                    Description = "API care ofera suport portalului folosit de ONG.",
                    TermsOfService = "TBD",
                    Contact =
                        new Contact
                        {
                            Email = "info@monitorizarevot.ro",
                            Name = "Code for Romania",
                            Url = "http://monitorizarevot.ro"
                        },
                });

                var path = PlatformServices.Default.Application.ApplicationBasePath +
                           System.IO.Path.DirectorySeparatorChar + "MonitorizareVot.Ong.Api.xml";

                if (System.IO.File.Exists(path))
                    options.IncludeXmlComments(path);
            });

            ConfigureContainer(services);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory,
            IApplicationLifetime appLifetime, IDistributedCache cache)
        {
            loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            loggerFactory.AddDebug();

            loggerFactory.AddSerilog();
            Log.Logger = new LoggerConfiguration()
                .WriteTo
                .ApplicationInsightsTraces(Configuration["ApplicationInsights:InstrumentationKey"])
                .CreateLogger();

            // Shows UseCors with CorsPolicyBuilder.
            app.UseCors(builder =>
               builder.AllowAnyHeader().AllowAnyOrigin());

            app.UseApplicationInsightsRequestTelemetry();

            app.UseApplicationInsightsExceptionTelemetry();


            appLifetime.ApplicationStopped.Register(Log.CloseAndFlush);

            app.Use(async (context, next)=>
            {
                await next();

                if(context.Response.StatusCode == 404  && !Path.HasExtension(context.Request.Path.Value)){
                    context.Request.Path = "/index.html";
                    await next();
                } 
            });

            app.UseStaticFiles();

            app.UseExceptionHandler(
            builder =>
            {
                builder.Run(context =>
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    context.Response.ContentType = "application/json";
                    return Task.FromResult(0);
                }
                );
            });

            app.UseSimpleInjectorAspNetRequestScoping(container);

            container.Options.DefaultScopedLifestyle = new AspNetRequestLifestyle();

            ConfigureCache(env);

            RegisterServices();

            InitializeContainer(app);

            RegisterDbContext<OngContext>(Configuration.GetConnectionString("DefaultConnection"));

            RegisterAutomapper();

            BuildMediator();

            container.Verify();

            app.UseMvc();
            

            // Enable middleware to serve generated Swagger as a JSON endpoint
            app.UseSwagger();

            // Enable middleware to serve swagger-ui assets (HTML, JS, CSS etc.)
            app.UseSwaggerUi();
        }


        private void ConfigureCache(IHostingEnvironment env)
        {
            var enableCache = Configuration.GetValue<bool>("ApplicationCacheOptions:Enabled");

            if (!enableCache)
            {
                container.RegisterSingleton<ICacheService>(new NoCacheService());
                return;
            }

            var cacheProvider = Configuration.GetValue<string>("ApplicationCacheOptions:RedisCache");

            container.RegisterSingleton<ICacheService, CacheService>();

            switch (cacheProvider)
            {
                case "RedisCache":
                //    {
                //        container.RegisterSingleton<IOptions<RedisCacheOptions>>(
                //          new OptionsManager<RedisCacheOptions>(new List<IConfigureOptions<RedisCacheOptions>>
                //          {
                //                new ConfigureFromConfigurationOptions<RedisCacheOptions>(
                //                    Configuration.GetSection("RedisCacheOptions"))
                //          }));

                //        break;
                //    }

                default:
                case "MemoryDistributedCache":
                    {
                        container.RegisterSingleton<IDistributedCache>(new MemoryDistributedCache(new MemoryCache(new MemoryCacheOptions())));
                        break;
                    }
            }
        }

        private void ConfigureContainer(IServiceCollection services)
        {
            services.AddSingleton<IControllerActivator>(
                new SimpleInjectorControllerActivator(container));
            services.AddSingleton<IViewComponentActivator>(
                new SimpleInjectorViewComponentActivator(container));
        }

        private void RegisterServices()
        {
            //exemplu de servicii custom
            //container.Register<ISectieDeVotareService, SectieDevotareDBService>(Lifestyle.Scoped);
        }

        private void InitializeContainer(IApplicationBuilder app)
        {
            // Add application presentation components:
            container.RegisterMvcControllers(app);
            container.RegisterMvcViewComponents(app);

            // Add application services. For instance:
            //container.Register<IUserRepository, SqlUserRepository>(Lifestyle.Scoped);


            // Cross-wire ASP.NET services (if any). For instance:
            container.RegisterSingleton(app.ApplicationServices.GetService<ILoggerFactory>());
            container.RegisterConditional(
                typeof(ILogger),
                c => typeof(Logger<>).MakeGenericType(c.Consumer.ImplementationType),
                Lifestyle.Singleton,
                c => true);

            // NOTE: Prevent cross-wired instances as much as possible.
            // See: https://simpleinjector.org/blog/2016/07/
        }

        private void RegisterDbContext<TDbContext>(string connectionString = null)
            where TDbContext : DbContext
        {
            if (!string.IsNullOrEmpty(connectionString))
            {
                var optionsBuilder = new DbContextOptionsBuilder<TDbContext>();
                optionsBuilder.UseSqlServer(connectionString);

                container.RegisterSingleton(optionsBuilder.Options);

                container.Register<TDbContext>(Lifestyle.Scoped);
            }
            else
            {
                container.Register<TDbContext>(Lifestyle.Scoped);
            }
        }

        private IMediator BuildMediator()
        {
            var assemblies = GetAssemblies().ToArray();
            container.RegisterSingleton<IMediator, Mediator>();
            container.Register(typeof(IRequestHandler<,>), assemblies);
            container.Register(typeof(IAsyncRequestHandler<,>), assemblies);
            container.RegisterCollection(typeof(INotificationHandler<>), assemblies);
            container.RegisterCollection(typeof(IAsyncNotificationHandler<>), assemblies);
            container.RegisterSingleton(Console.Out);
            container.RegisterSingleton(new SingleInstanceFactory(container.GetInstance));
            container.RegisterSingleton(new MultiInstanceFactory(container.GetAllInstances));

            var mediator = container.GetInstance<IMediator>();

            return mediator;
        }

        private void RegisterAutomapper()
        {
            Mapper.Initialize(cfg => { cfg.AddProfiles(GetAssemblies()); });

            container.RegisterSingleton(Mapper.Configuration);
            container.Register<IMapper>(() => new Mapper(Mapper.Configuration), Lifestyle.Scoped);
        }

        private static IEnumerable<Assembly> GetAssemblies()
        {
            yield return typeof(IMediator).GetTypeInfo().Assembly;
            yield return typeof(Startup).GetTypeInfo().Assembly;
            yield return typeof(OngContext).GetTypeInfo().Assembly;
        }
    }
}