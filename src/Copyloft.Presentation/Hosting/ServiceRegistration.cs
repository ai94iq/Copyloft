using Copyloft.Core.Services;
using Copyloft.Data.Repositories;

namespace Copyloft.Presentation.Hosting;

public static class ServiceRegistration
{
    // Settings, data and the portable services. The head registers the platform implementations
    // (IKeyStore, IClipboardMonitor, IGlobalHotkey, IPasteBack, IForegroundApp, IStartupRegistration,
    // ISystemTheme, IUiDispatcher, ISessionEnd, IDataChangeNotifier, IThemeService) on top.
    public static IServiceCollection AddPresentationServices(
        this IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton<ISettingsStore, FileSettingsStore>();
        services.AddSingleton<SettingsService>();
        services.AddTransient(typeof(Lazy<>), typeof(LazyService<>));

        // Data: factory, initializer and repositories are Singletons.
        services.AddSingleton(sp => new DataOptions(
            AppPaths.Database, AppPaths.Backups,
            sp.GetRequiredService<IKeyStore>().LoadOrCreate(AppPaths.DatabaseKey)));
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IClipRepository, ClipRepository>();
        services.AddSingleton<ICategoryRepository, CategoryRepository>();
        services.AddSingleton<ISecureSettings, SecureSettingsRepository>();

        services.AddSingleton<ClipCaptureService>();
        services.AddSingleton<SessionCleanup>();
        services.AddSingleton<ICategoryLockService, CategoryLockService>();
        services.AddSingleton<ILockPasswordService, LockPasswordService>();
        services.AddSingleton<IClipExportService>(sp => new ClipExportService(
            sp.GetRequiredService<IClipRepository>(), sp.GetRequiredService<ICategoryLockService>()));
        services.AddSingleton<IDateFormatter, DateFormatter>();
        services.AddSingleton<IUpdateChecker, UpdateChecker>();
        services.AddSingleton<SettingsViewModel>();
        return services;
    }
}
