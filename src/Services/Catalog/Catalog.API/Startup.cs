﻿namespace Microsoft.eShopOnContainers.Services.Catalog.API
{
    using Autofac;
    using Autofac.Extensions.DependencyInjection;
    using global::Catalog.API.Infrastructure.Filters;
    using global::Catalog.API.IntegrationEvents;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Infrastructure;
    using Microsoft.eShopOnContainers.BuildingBlocks.EventBus;
    using Microsoft.eShopOnContainers.BuildingBlocks.EventBus.Abstractions;
    using Microsoft.eShopOnContainers.BuildingBlocks.EventBusRabbitMQ;
    using Microsoft.eShopOnContainers.BuildingBlocks.IntegrationEventLogEF;
    using Microsoft.eShopOnContainers.BuildingBlocks.IntegrationEventLogEF.Services;
    using Microsoft.eShopOnContainers.Services.Catalog.API.Infrastructure;
    using Microsoft.eShopOnContainers.Services.Catalog.API.IntegrationCommands.Commands;
    using Microsoft.eShopOnContainers.Services.Catalog.API.IntegrationEvents;
    using Microsoft.eShopOnContainers.Services.Catalog.API.IntegrationCommands.CommandsHandlers;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.HealthChecks;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using RabbitMQ.Client;
    using System;
    using System.Data.Common;
    using System.Data.SqlClient;
    using System.Reflection;
    
    public class Startup
    {
        public IConfigurationRoot Configuration { get; }

        public Startup(IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile($"settings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"settings.{env.EnvironmentName}.json", optional: true);

            if (env.IsDevelopment())
            {
                builder.AddUserSecrets(typeof(Startup).GetTypeInfo().Assembly);
            }

            builder.AddEnvironmentVariables();

            Configuration = builder.Build();
        }

        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            // Add framework services.

            services.AddHealthChecks(checks =>
            {
                checks.AddSqlCheck("CatalogDb", Configuration["ConnectionString"]);
            });

            services.AddMvc(options =>
            {
                options.Filters.Add(typeof(HttpGlobalExceptionFilter));
            }).AddControllersAsServices();

            services.AddDbContext<CatalogContext>(options =>
            {
                options.UseSqlServer(Configuration["ConnectionString"],
                                     sqlServerOptionsAction: sqlOptions =>
                                     {
                                         sqlOptions.MigrationsAssembly(typeof(Startup).GetTypeInfo().Assembly.GetName().Name);
                                         //Configuring Connection Resiliency: https://docs.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency 
                                         sqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                                     });

                // Changing default behavior when client evaluation occurs to throw. 
                // Default in EF Core would be to log a warning when client evaluation is performed.
                options.ConfigureWarnings(warnings => warnings.Throw(RelationalEventId.QueryClientEvaluationWarning));
                //Check Client vs. Server evaluation: https://docs.microsoft.com/en-us/ef/core/querying/client-eval
            });

            services.Configure<CatalogSettings>(Configuration);

            // Add framework services.
            services.AddSwaggerGen();
            services.ConfigureSwaggerGen(options =>
            {
                options.DescribeAllEnumsAsStrings();
                options.SingleApiVersion(new Swashbuckle.Swagger.Model.Info()
                {
                    Title = "eShopOnContainers - Catalog HTTP API",
                    Version = "v1",
                    Description = "The Catalog Microservice HTTP API. This is a Data-Driven/CRUD microservice sample",
                    TermsOfService = "Terms Of Service"
                });
            });

            services.AddCors(options =>
            {
                options.AddPolicy("CorsPolicy",
                    builder => builder.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials());
            });

            services.AddTransient<Func<DbConnection, IIntegrationEventLogService>>(
                sp => (DbConnection c) => new IntegrationEventLogService(c));

            services.AddTransient<ICatalogIntegrationEventService, CatalogIntegrationEventService>();

            services.AddSingleton<IRabbitMQPersistentConnection>(sp =>
            {
                var settings = sp.GetRequiredService<IOptions<CatalogSettings>>().Value;
                var logger = sp.GetRequiredService<ILogger<DefaultRabbitMQPersistentConnection>>();
                var factory = new ConnectionFactory()
                {
                    HostName = settings.EventBusConnection
                };

                return new DefaultRabbitMQPersistentConnection(factory, logger);
            });

            services.AddSingleton<IEventBusSubscriptionsManager, InMemoryEventBusSubscriptionsManager>();
            services.AddSingleton<IEventBus, EventBusRabbitMQ>();
            services.AddTransient<IIntegrationEventHandler<ConfirmOrderStockCommandMsg>, ConfirmOrderStockCommandMsgHandler>();
            services.AddTransient<IIntegrationEventHandler<DecrementOrderStockCommandMsg>, DecrementOrderStockCommandMsgHandler>();

            var container = new ContainerBuilder();
            container.Populate(services);
            return new AutofacServiceProvider(container.Build());
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            //Configure logs

            loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            loggerFactory.AddDebug();

            app.UseCors("CorsPolicy");

            app.UseMvcWithDefaultRoute();

            app.UseSwagger()
              .UseSwaggerUi();

            var context = (CatalogContext)app
                        .ApplicationServices.GetService(typeof(CatalogContext));

            WaitForSqlAvailability(context, loggerFactory);

            //Seed Data
            CatalogContextSeed.SeedAsync(app, loggerFactory)
                .Wait();

            ConfigureEventBus(app);

            var integrationEventLogContext = new IntegrationEventLogContext(
                new DbContextOptionsBuilder<IntegrationEventLogContext>()
                .UseSqlServer(Configuration["ConnectionString"], b => b.MigrationsAssembly("Catalog.API"))
                .Options);

            integrationEventLogContext.Database.Migrate();
        }

        private void WaitForSqlAvailability(CatalogContext ctx, ILoggerFactory loggerFactory, int? retry = 0)
        {
            int retryForAvailability = retry.Value;

            try
            {
                ctx.Database.OpenConnection();
            }
            catch (SqlException ex)
            {
                if (retryForAvailability < 10)
                {
                    retryForAvailability++;
                    var log = loggerFactory.CreateLogger(nameof(Startup));
                    log.LogError(ex.Message);
                    WaitForSqlAvailability(ctx, loggerFactory, retryForAvailability);
                }
            }
            finally
            {
                ctx.Database.CloseConnection();
            }
        }

        private void ConfigureEventBus(IApplicationBuilder app)
        {
            var eventBus = app.ApplicationServices.GetRequiredService<IEventBus>();

            eventBus.Subscribe<ConfirmOrderStockCommandMsg, IIntegrationEventHandler<ConfirmOrderStockCommandMsg>>();
            eventBus.Subscribe<DecrementOrderStockCommandMsg, IIntegrationEventHandler<DecrementOrderStockCommandMsg>>();
        }
    }
}
