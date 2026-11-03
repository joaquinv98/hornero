using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>La migración de impuestos sobre una base que ya tenía filas: lo que las columnas nuevas no pueden deducir se rellena.</summary>
[TestClass]
public sealed class MigrationBackfillTests
{
    [TestMethod]
    public async Task ImpuestosArca_RellenaElModoDePrecioYDelDominio_YElEmisorSigueResponsableInscripto_EnLasFilasQueYaExistian()
    {
        var (connectionString, drop) = await TestDatabase.Create();
        try
        {
            await using var provider = new ServiceCollection()
                .AddLogging()
                .AddSingleton(TimeProvider.System)
                .AddBillingCore(connectionString, o => o.PublicUrl = "https://billing.example.com", null)
                .BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var migrator = db.GetService<IMigrator>();

            // Hasta la migración anterior, con un emisor, un precio y un dominio cargados como estaban entonces.
            await migrator.MigrateAsync("20261028205124_Integracion");
            // Las llaves van dobles: ExecuteSqlRaw toma el texto como cadena de formato.
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO fiscal.issuers (tax_id, legal_name, point_of_sale, type_map, class_a_conditions, moment, settings, active)
                VALUES ('30712345671', 'Proveedor viejo', 3, '{{}}', '[1,6,13,16]', 'AtIssue', '{{}}', true);
                INSERT INTO catalog.products (name, group_name, kind, module_params, tax_treatment, visible) VALUES ('Hosting', '', 'Manual', '{{}}', 'Taxed', true);
                INSERT INTO catalog.plans (product_id, cycle_count, cycle_unit, active) VALUES (1, 1, 'Month', true);
                INSERT INTO catalog.plan_prices (plan_id, currency, amount, setup_fee, valid_from) VALUES (1, 'ARS', 12100, 0, '2026-01-01');
                INSERT INTO accounts.accounts (name, email, currency, language, status, created_at, no_fiscal_document, no_late_fees) VALUES ('Cliente', 'cliente@example.com', 'ARS', 'es', 'Active', now(), false, false);
                INSERT INTO platform.module_installations (name, base_url, manifest, signing_secret, created_at) VALUES ('registrar', 'https://registrar.example.com', '{{}}', '\x00', now());
                INSERT INTO platform.module_instances (installation_id, name, config, browser_hosts, created_at, state_version) VALUES (1, 'registrar', '{{}}', '[]', now(), 0);
                INSERT INTO domains.domains (account_id, name, registrar_instance_id, status, years, currency, recurring_amount, auto_renew, dns_management, email_forwarding, id_protection, nameservers, external_ids, created_at)
                VALUES (1, 'ejemplo.com.ar', 1, 'Active', 1, 'ARS', 1000, true, false, false, false, '[]', '{{}}', now());
                """);

            await migrator.MigrateAsync();

            var issuer = await db.IssuerProfiles.AsNoTracking().SingleAsync();
            Assert.AreEqual(1, issuer.VatCondition, "El emisor que ya existía era responsable inscripto, no la condición 0.");
            var price = await db.PlanPrices.AsNoTracking().SingleAsync();
            Assert.AreEqual(Domain.Billing.PriceMode.TaxIncluded, price.PriceMode);
            var raw = await db.Database.SqlQueryRaw<string>("SELECT price_mode AS \"Value\" FROM catalog.plan_prices").SingleAsync();
            Assert.AreEqual("TaxIncluded", raw, "En la base queda el nombre del miembro del enum, no una cadena vacía.");
            Assert.AreEqual("digital", await db.Database.SqlQueryRaw<string>("SELECT tax_category AS \"Value\" FROM catalog.products").SingleAsync());
            Assert.AreEqual("TaxIncluded", await db.Database.SqlQueryRaw<string>("SELECT price_mode AS \"Value\" FROM domains.domains").SingleAsync(), "El dominio importado renueva con IVA incluido, como sus servicios.");
        }
        finally
        {
            await drop();
        }
    }
}
