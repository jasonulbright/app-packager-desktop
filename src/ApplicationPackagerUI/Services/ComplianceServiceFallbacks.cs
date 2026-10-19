using System.Reflection;
using ApplicationPackagerEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

public static partial class ComplianceRegistration
{
    private const string Unavailable = "The compliance services are not part of this build.";

    static partial void AddServiceFallbacks(IServiceCollection services)
    {
        services.TryAddSingleton(_ => DispatchProxy.Create<IComplianceService, UnavailableProxy>());
        services.TryAddSingleton(_ => DispatchProxy.Create<IComplianceReportWriter, UnavailableProxy>());
        services.TryAddSingleton<ICatalogService, UnavailableCatalogService>();
    }

    /// <summary>Every call fails with a plain message; a task-returning call returns a faulted task.</summary>
    public class UnavailableProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var failure = new ComplianceServiceException(Unavailable);
            if (targetMethod?.ReturnType is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var fromException = typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(type.GetGenericArguments()[0]);
                return fromException.Invoke(null, [failure]);
            }
            throw failure;
        }
    }

    private sealed class UnavailableCatalogService : ICatalogService
    {
        public IReadOnlyList<CatalogEntry> All() => [];
        public CatalogEntry? Find(string id) => null;
        public string? Message => Unavailable;
    }
}
