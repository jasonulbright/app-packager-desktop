using System.Reflection;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Services;

/// <summary>The life of one set of site pages. A retired set no longer saves its choices: they belong to the site that was active.</summary>
public sealed class SitePageLifetime
{
    public bool IsRetired { get; private set; }

    internal void Retire() => IsRetired = true;
}

/// <summary>
/// The tool pages that hold data of a site, with their view models and page services. They are built from their registrations and
/// kept while the site stays. A switch of site retires the whole set, so no list, selection, plan, undo or result of the old site is
/// left: the next visit of a page builds it new. Every other type comes from the application's own provider.
/// </summary>
public sealed class SitePages : IServiceProvider
{
    public static readonly IReadOnlySet<Type> Types = new HashSet<Type>
    {
        typeof(DeploymentPageServices), typeof(DeploymentHelperViewModel), typeof(DeploymentHelperPage),
        typeof(CollectionCompliancePageServices), typeof(CollectionComplianceViewModel), typeof(CollectionCompliancePage),
        typeof(CompliancePageServices), typeof(ComplianceViewModel),
        typeof(DpContentPageServices), typeof(DpContentManagerViewModel), typeof(DpContentManagerPage),
        typeof(MaintenanceWindowsViewModel), typeof(MaintenanceWindowsPage),
        typeof(PivotsScriptsPageServices), typeof(PivotsScriptsViewModel), typeof(PivotsScriptsPage),
        typeof(SiteHygienePageServices), typeof(SiteHygieneViewModel), typeof(SiteHygienePage),
        typeof(DetectionTesterViewModel), typeof(DetectionTesterPage)
    };

    private readonly IServiceProvider _root;
    private readonly Dictionary<Type, ServiceDescriptor> _own;
    private Dictionary<Type, object> _built = [];
    private SitePageLifetime _lifetime = new();

    public SitePages(IServiceProvider root, IEnumerable<ServiceDescriptor> registrations)
    {
        _root = root;
        _own = registrations.Where(d => Types.Contains(d.ServiceType)).GroupBy(d => d.ServiceType).ToDictionary(g => g.Key, g => g.Last());
    }

    /// <summary>True for a page, view model or page service that a switch of site builds new.</summary>
    public bool Holds(Type type) => _own.ContainsKey(type);

    /// <summary>The instance of the current set when it was built; null otherwise. It builds nothing.</summary>
    public T? Existing<T>() where T : class => _built.GetValueOrDefault(typeof(T)) as T;

    /// <summary>Drops the current set. The instances in it can still end their work, and they save nothing.</summary>
    public void Retire()
    {
        _lifetime.Retire();
        _lifetime = new SitePageLifetime();
        _built = [];
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(SitePageLifetime)) return _lifetime;
        if (serviceType == typeof(IServiceProvider)) return this;
        if (!_own.TryGetValue(serviceType, out var registration)) return _root.GetService(serviceType);
        if (_built.TryGetValue(serviceType, out var built)) return built;
        var instance = registration.ImplementationFactory?.Invoke(this) ?? registration.ImplementationInstance
            ?? Construct(registration.ImplementationType ?? serviceType);
        _built[serviceType] = instance;
        return instance;
    }

    // The rule of the container: the longest constructor whose every parameter has a service or a default value.
    private object Construct(Type type)
    {
        foreach (var constructor in type.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
        {
            var parameters = constructor.GetParameters();
            var values = new object?[parameters.Length];
            var complete = true;
            for (var i = 0; i < parameters.Length && complete; i++)
            {
                values[i] = GetService(parameters[i].ParameterType) ?? (parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null);
                complete = values[i] is not null || parameters[i].HasDefaultValue;
            }
            if (!complete) continue;
            try { return constructor.Invoke(values); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
        throw new InvalidOperationException($"No constructor of {type.Name} can be built from the registered services.");
    }
}
