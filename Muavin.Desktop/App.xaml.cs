// App.xaml.cs
using System;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using Muavin.Desktop.ViewModels;
using Muavin.Xml.Data;
using Npgsql;

namespace Muavin.Desktop
{
    public partial class App : Application
    {
        private readonly ContextState _context = new();
        private DbMuavinRepository _repo = null!;

        private static string BootLogPath =>
            Path.Combine(AppContext.BaseDirectory, "bootlog.txt");

        private static readonly object _bootLock = new();

        private static void BootLog(string msg)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "boot.log");
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}";
                lock (_bootLock) File.AppendAllText(path, line);
            }
            catch { /* asla crash ettirmesin */ }
        }

        private void RegisterGlobalExceptionHandlers()
        {
            // UI thread
            this.DispatcherUnhandledException += (s, e) =>
            {
                BootLog("DispatcherUnhandledException: " + e.Exception);
                MessageBox.Show(e.Exception.Message, "Beklenmeyen Hata (UI)");
                e.Handled = true;
                Shutdown(-1);
            };

            // non-UI thread
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                BootLog("UnhandledException: " + e.ExceptionObject);
                try { MessageBox.Show(e.ExceptionObject?.ToString() ?? "Unknown", "Beklenmeyen Hata"); } catch { }
                Environment.Exit(-1);
            };

            // async Task exceptions
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                BootLog("UnobservedTaskException: " + e.Exception);
                e.SetObserved();
            };
        }

        public App()
        {
            BootLog("App() ctor entered");
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // ✅ TR kültürü en başta set edelim (tüm binding/formatlar bundan etkilensin)
            var culture = new CultureInfo("tr-TR");

            // Default culture: yeni thread’ler için
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            // Current thread: WPF startup thread için (bazı senaryolarda gerekli)
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            // WPF binding/format dili
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(
                    XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

            base.OnStartup(e);

            RegisterGlobalExceptionHandlers();
            BootLog("OnStartup entered");
            BootLog("Culture set: " + culture.Name);

            // Hesap planı
            try
            {
                var planPath = Path.Combine(
                    AppContext.BaseDirectory,
                    "Assets",
                    "HesapPlani.txt");
                Muavin.Desktop.Util.AccountPlan.Load(planPath);
            }
            catch { }

            // Connection string
            var cs = ConfigurationManager
                .ConnectionStrings["MuavinDb"]
                ?.ConnectionString;

            BootLog("ConnectionString read. empty? " + string.IsNullOrWhiteSpace(cs));

            if (string.IsNullOrWhiteSpace(cs))
            {
                MessageBox.Show(
                    "MuavinDb connectionString bulunamadı",
                    "DB Ayarı Eksik",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            _repo = new DbMuavinRepository(cs);

            // DB test
            try
            {
                using var conn = new NpgsqlConnection(cs);
                conn.Open();
                using var cmd = new NpgsqlCommand("SELECT 1", conn);
                cmd.ExecuteScalar();
                BootLog("DB connection OK");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DB Hatası");
                Shutdown();
                return;
            }

            try
            {   
                BootLog("EnsureSchemaAsync starting");
                Task.Run(() => _repo.EnsureSchemaAsync()).GetAwaiter().GetResult();
                BootLog("EnsureSchemaAsync OK");
            }
            catch (Exception ex)
            {
                BootLog("EnsureSchemaAsync FAILED: " + ex);
                MessageBox.Show(
                    "Veritabanı şeması hazırlanamadı: \n" + ex.Message,
                    "DB Şeması Hatası",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }


            // =====================
            // CONTEXT WINDOW
            // =====================
            BootLog("Creating ContextWindow");
            var ctxWin = new ContextWindow(_context, _repo);

            BootLog("ShowDialog starting");
            var ok = ctxWin.ShowDialog();

            BootLog($"ShowDialog returned: {ok}, HasContext={_context.HasContext}");

            if (ok != true || !_context.HasContext)
            {
                Shutdown();
                return;
            }

            // =====================
            // MAIN WINDOW
            // =====================
            var vm = new MainViewModel(_context, _repo);
            var main = new MainWindow
            {
                DataContext = vm
            };

            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            main.Show();

            vm.LoadFromDatabaseCommand.Execute(null);
        }
    }
}
