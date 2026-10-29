using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NetArchTest.Rules;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.SharedKernel;

namespace Pishkhan.ArchitectureTests;

/// <summary>Module boundaries and platform rules from backend plan 1.3, checked on every build.</summary>
public class ArchitectureTests
{
    private static readonly string[] Modules = ["Identity", "Tenancy", "Backoffice", "Files", "Channels", "Conversations", "Agent", "Automation", "Content"];

    private static Assembly ModuleAssembly(string module) => Assembly.Load($"Pishkhan.Modules.{module}");

    private static Assembly ContractsAssembly(string module) => Assembly.Load($"Pishkhan.Modules.{module}.Contracts");

    public static TheoryData<string> AllModules() => new(Modules);

    [Theory]
    [MemberData(nameof(AllModules))]
    public void Module_uses_other_modules_only_through_their_contracts(string module)
    {
        // Every type of every other module's implementation assembly (its Contracts assembly is allowed). Full type names, because
        // NetArchTest matches by prefix and a module's root namespace is a prefix of its Contracts namespace.
        var forbidden = Modules.Where(m => m != module)
            .SelectMany(m => ModuleAssembly(m).GetTypes())
            .Where(t => t.Namespace is not null && !t.Name.StartsWith('<'))
            .Select(t => t.FullName!)
            .ToArray();
        var result = Types.InAssembly(ModuleAssembly(module)).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        var offenders = result.FailingTypeNames ?? [];
        offenders.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllModules))]
    public void Contracts_do_not_depend_on_infrastructure_or_ef(string module)
    {
        var result = Types.InAssembly(ContractsAssembly(module)).ShouldNot()
            .HaveDependencyOnAny("Pishkhan.Infrastructure", "Pishkhan.Web", "Microsoft.EntityFrameworkCore", "MassTransit", "Microsoft.AspNetCore")
            .GetResult();
        (result.FailingTypeNames ?? []).ShouldBeEmpty();
    }

    [Fact]
    public void Building_blocks_do_not_depend_on_modules()
    {
        foreach (var name in new[] { "Pishkhan.SharedKernel", "Pishkhan.Contracts", "Pishkhan.Persian", "Pishkhan.Infrastructure", "Pishkhan.Web", "Pishkhan.AI" })
        {
            var result = Types.InAssembly(Assembly.Load(name)).ShouldNot().HaveDependencyOn("Pishkhan.Modules").GetResult();
            (result.FailingTypeNames ?? []).ShouldBeEmpty(name);
        }
    }

    public static TheoryData<Type> ModuleContexts() => new(
        typeof(PlatformDbContext),
        typeof(Modules.Identity.Persistence.IdentityDbContext),
        typeof(Modules.Tenancy.Persistence.TenancyDbContext),
        typeof(Modules.Backoffice.Persistence.BackofficeDbContext),
        typeof(Modules.Files.Persistence.FilesDbContext),
        typeof(Modules.Channels.Persistence.ChannelsDbContext),
        typeof(Modules.Conversations.Persistence.ConversationsDbContext),
        typeof(Modules.Agent.Persistence.AgentDbContext),
        typeof(Modules.Automation.Persistence.AutomationDbContext),
        typeof(Modules.Content.Persistence.ContentDbContext));

    [Theory]
    [MemberData(nameof(ModuleContexts))]
    public void Every_tenant_owned_entity_has_the_tenant_filter(Type contextType)
    {
        using var db = CreateContext(contextType);
        var missing = db.Model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType) && !ModuleDbContext.HasTenantFilter(e))
            .Select(e => e.ClrType.Name)
            .ToList();
        missing.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ModuleContexts))]
    public void Context_owns_only_its_schema(Type contextType)
    {
        using var db = CreateContext(contextType);
        var schema = ((ModuleDbContext)db).Schema;
        var model = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var foreign = model.GetEntityTypes()
            .Where(e => !e.IsTableExcludedFromMigrations() && e.GetSchema() is { } s && s != schema)
            .Select(e => $"{e.ClrType.Name} in {e.GetSchema()}")
            .ToList();
        foreign.ShouldBeEmpty();
    }

    [Fact]
    public void Forbidden_libraries_are_not_referenced()
    {
        // Commercially licensed now (backend plan 3.1): MediatR, AutoMapper, FluentAssertions 8+, MassTransit 9+.
        var references = LoadedPishkhanAssemblies().SelectMany(a => a.GetReferencedAssemblies()).ToList();
        references.ShouldNotContain(r => r.Name == "MediatR" || r.Name == "AutoMapper" || r.Name == "FluentAssertions");
        references.Where(r => r.Name == "MassTransit").ShouldAllBe(r => r.Version!.Major < 9);
    }

    private static DbContext CreateContext(Type contextType)
    {
        var schema = contextType == typeof(PlatformDbContext) ? "platform" : (string)contextType.GetField("SchemaName")!.GetValue(null)!;
        var optionsType = typeof(DbContextOptions<>).MakeGenericType(contextType);
        var options = typeof(PersistenceExtensions).GetMethod(nameof(PersistenceExtensions.DesignTimeOptions))!.MakeGenericMethod(contextType).Invoke(null, [schema, null]);
        return (DbContext)Activator.CreateInstance(contextType, options, new TenantContext())!;
    }

    private static IEnumerable<Assembly> LoadedPishkhanAssemblies()
    {
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "Pishkhan.*.dll"))
        {
            yield return Assembly.LoadFrom(file);
        }
    }
}
