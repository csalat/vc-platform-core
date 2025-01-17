using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VirtoCommerce.CoreModule.Core.Conditions;
using VirtoCommerce.CoreModule.Core.Conditions.Browse;
using VirtoCommerce.CoreModule.Core.Conditions.GeoConditions;
using VirtoCommerce.Platform.Core.Bus;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.ExportImport;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Platform.Data.Extensions;
using VirtoCommerce.PricingModule.Core;
using VirtoCommerce.PricingModule.Core.Events;
using VirtoCommerce.PricingModule.Core.Model;
using VirtoCommerce.PricingModule.Core.Model.Conditions;
using VirtoCommerce.PricingModule.Core.Services;
using VirtoCommerce.PricingModule.Data.ExportImport;
using VirtoCommerce.PricingModule.Data.Handlers;
using VirtoCommerce.PricingModule.Data.Repositories;
using VirtoCommerce.PricingModule.Data.Search;
using VirtoCommerce.PricingModule.Data.Services;
using VirtoCommerce.PricingModule.Web.JsonConverters;
using VirtoCommerce.SearchModule.Core.Model;
using VirtoCommerce.SearchModule.Core.Services;

namespace VirtoCommerce.PricingModule.Web
{
    public class Module : IModule, IExportSupport, IImportSupport
    {
        private IApplicationBuilder _applicationBuilder;
        private ServiceProvider _serviceProvider;

        #region IModule Members

        public ManifestModuleInfo ModuleInfo { get; set; }

        public void Initialize(IServiceCollection serviceCollection)
        {
            _serviceProvider = serviceCollection.BuildServiceProvider();
            var configuration = _serviceProvider.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString("VirtoCommerce.Pricing") ?? configuration.GetConnectionString("VirtoCommerce");
            serviceCollection.AddDbContext<PricingDbContext>(options => options.UseSqlServer(connectionString));
            serviceCollection.AddTransient<IPricingRepository, PricingRepositoryImpl>();
            serviceCollection.AddTransient<Func<IPricingRepository>>(provider => () => provider.CreateScope().ServiceProvider.GetRequiredService<IPricingRepository>());

            serviceCollection.AddTransient<IPricingService, PricingServiceImpl>();
            serviceCollection.AddTransient<IPricingSearchService, PricingSearchServiceImpl>();
            serviceCollection.AddTransient<PricingExportImport>();
            serviceCollection.AddTransient<PolymorphicPricingJsonConverter>();
            serviceCollection.AddTransient<ProductPriceDocumentChangesProvider>();
            serviceCollection.AddTransient<ProductPriceDocumentBuilder>();
            serviceCollection.AddTransient<LogChangesChangedEventHandler>();

        }

        public void PostInitialize(IApplicationBuilder appBuilder)
        {
            _applicationBuilder = appBuilder;
            var settingsManager = _serviceProvider.GetService<ISettingsManager>();

            var settingsRegistrar = appBuilder.ApplicationServices.GetRequiredService<ISettingsRegistrar>();
            settingsRegistrar.RegisterSettings(ModuleConstants.Settings.AllSettings, ModuleInfo.Id);

            var modulePermissions = ModuleConstants.Security.Permissions.AllPermissions.Select(p => new Permission
            {
                Name = p,
                GroupName = "Pricing",
                ModuleId = ModuleInfo.Id
            }).ToArray();
            var permissionsRegistrar = appBuilder.ApplicationServices.GetRequiredService<IPermissionsRegistrar>();
            permissionsRegistrar.RegisterPermissions(modulePermissions);

            using (var serviceScope = appBuilder.ApplicationServices.CreateScope())
            {
                var dbContext = serviceScope.ServiceProvider.GetRequiredService<PricingDbContext>();
                dbContext.Database.MigrateIfNotApplied(MigrationName.GetUpdateV2MigrationName(ModuleInfo.Id));
                dbContext.Database.EnsureCreated();
                dbContext.Database.Migrate();
            }

            // Next lines allow to use polymorph types in API controller methods
            var mvcJsonOptions = appBuilder.ApplicationServices.GetService<IOptions<MvcJsonOptions>>();
            mvcJsonOptions.Value.SerializerSettings.Converters.Add(appBuilder.ApplicationServices.GetService<PolymorphicPricingJsonConverter>());

            var priceIndexingEnabled = settingsManager.GetValue(ModuleConstants.Settings.General.PricingIndexing.Name, true);
            if (priceIndexingEnabled)
            {
                // Add price document source to the product indexing configuration
                var productIndexingConfigurations = _serviceProvider.GetService<IndexDocumentConfiguration[]>();

                if (productIndexingConfigurations != null)
                {
                    var productPriceDocumentSource = new IndexDocumentSource
                    {
                        ChangesProvider = _serviceProvider.GetService<IIndexDocumentChangesProvider>(),
                        DocumentBuilder = _serviceProvider.GetService<ProductPriceDocumentBuilder>(),
                    };

                    foreach (var configuration in productIndexingConfigurations.Where(c =>
                        c.DocumentType == KnownDocumentTypes.Product))
                    {
                        if (configuration.RelatedSources == null)
                        {
                            configuration.RelatedSources = new List<IndexDocumentSource>();
                        }

                        configuration.RelatedSources.Add(productPriceDocumentSource);
                    }
                }
            }

            var inProcessBus = appBuilder.ApplicationServices.GetService<IHandlerRegistrar>();
            inProcessBus.RegisterHandler<PriceChangedEvent>(async (message, token) => await appBuilder.ApplicationServices.GetService<LogChangesChangedEventHandler>().Handle(message));

            foreach (var conditionTree in AbstractTypeFactory<PriceConditionTreePrototype>.TryCreateInstance().Traverse<IConditionTree>(x => x.AvailableChildren))
            {
                AbstractTypeFactory<IConditionTree>.RegisterType(conditionTree.GetType());
            }
        }

        public void Uninstall()
        {
        }

        #endregion

        #region ISupportExportImportModule Members

        public async Task ExportAsync(Stream outStream, ExportImportOptions options, Action<ExportImportProgressInfo> progressCallback,
            ICancellationToken cancellationToken)
        {
            var exportJob = _applicationBuilder.ApplicationServices.GetRequiredService<PricingExportImport>();
            await exportJob.DoExportAsync(outStream, progressCallback, cancellationToken);
        }

        public async Task ImportAsync(Stream inputStream, ExportImportOptions options, Action<ExportImportProgressInfo> progressCallback,
            ICancellationToken cancellationToken)
        {
            var importJob = _applicationBuilder.ApplicationServices.GetRequiredService<PricingExportImport>();
            await importJob.DoImportAsync(inputStream, progressCallback, cancellationToken);
        }

        #endregion       
    }
}
