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
using Muavin.Xml.Util;
using System.Text;

namespace Muavin.Desktop
{
    public partial class App : Application
    {
        private readonly ContextState _context = new();
        private DbMuavinRepository _repo = null!;

        private static readonly object _bootLock = new();

        private static void BootLog(string msg)
        {
            try
            {
                var dir = LogPaths.GetLogDirectory();
                var path = Path.Combine(dir, "boot.log");
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}";
                lock (_bootLock) File.AppendAllText(path, line);
            }
            catch { }
        }

        private void RegisterGlobalExceptionHandlers()
        {
            // UI thread
            this.DispatcherUnhandledException += (s, e) =>
            {
                BootLog("DispatcherUnhandledException: " + e.Exception);
                try { MessageBox.Show(e.Exception.Message, "Beklenmeyen Hata (UI)"); } catch { }
                e.Handled = true;
                Shutdown(-1);
            };

            // non-UI thread
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                BootLog("UnhandledException: " + e.ExceptionObject);

                try
                {
                    Dispatcher.Invoke(() =>
                        MessageBox.Show(e.ExceptionObject?.ToString() ?? "Unknown", "Beklenmeyen Hata"));
                }
                catch { }

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
            BootLog($"Machine={Environment.MachineName} User={Environment.UserName}");
            BootLog($"OS={Environment.OSVersion}");
            BootLog($"BaseDir={AppContext.BaseDirectory}");

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);


            try
            {
                var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "-";
                BootLog($"Version={ver}");
            }
            catch { }
        }

        // ✅ DEADLOCK FIX: async void OnStartup + await
        protected override async void OnStartup(StartupEventArgs e)
        {
            // ✅ TR kültürü en başta set edelim (tüm binding/formatlar bundan etkilensin)
            var culture = new CultureInfo("tr-TR");

            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

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
                BootLog("AccountPlan loaded: " + planPath);
            }
            catch (Exception ex)
            {
                BootLog("AccountPlan load failed: " + ex);
            }

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

            // ✅ ConnectionString içinden host/port/db/user gibi kritik bilgileri logla (şifreyi loglamıyoruz)
            try
            {
                var b = new NpgsqlConnectionStringBuilder(cs);
                BootLog($"CS parsed => Host={b.Host} Port={b.Port} Database={b.Database} Username={b.Username} SslMode={b.SslMode}");
            }
            catch (Exception ex)
            {
                BootLog("CS parse failed: " + ex);
            }

            _repo = new DbMuavinRepository(cs);

            // DB test + DB kimliği (pgAdmin ile karşılaştır)
            try
            {
                using var conn = new NpgsqlConnection(cs);
                conn.Open();

                using (var cmd = new NpgsqlCommand("SELECT 1", conn))
                    cmd.ExecuteScalar();

                BootLog("DB connection OK");

                const string sql =
                    "SELECT current_database() AS db, current_user AS usr, " +
                    "inet_server_addr() AS server_ip, inet_server_port() AS port, version() AS pg_version;";

                using var cmd2 = new NpgsqlCommand(sql, conn);
                using var rd = cmd2.ExecuteReader();

                if (rd.Read())
                {
                    var db = rd["db"]?.ToString() ?? "";
                    var usr = rd["usr"]?.ToString() ?? "";
                    var ip = rd["server_ip"]?.ToString() ?? "";
                    var port = rd["port"]?.ToString() ?? "";
                    var ver = rd["pg_version"]?.ToString() ?? "";

                    BootLog($"DB IDENTITY => db={db} user={usr} server_ip={ip} port={port}");
                    BootLog($"DB VERSION  => {ver}");
                }
            }
            catch (Exception ex)
            {
                BootLog("DB connection FAILED: " + ex);
                MessageBox.Show(ex.Message, "DB Hatası");
                Shutdown();
                return;
            }

            try
            {
                BootLog("EnsureSchemaAsync starting");
                await _repo.EnsureSchemaAsync();
                BootLog("EnsureSchemaAsync OK");
            }
            catch (Exception ex)
            {
                BootLog("EnsureSchemaAsync FAILED: " + ex);
                MessageBox.Show("Veritabanı şeması hazırlanamadı:\n" + ex.Message, "DB Şeması Hatası");
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

            try
            {
                using var conn = new NpgsqlConnection(cs);
                conn.Open();
                using var cmd = new NpgsqlCommand(
                    "SELECT current_database(), current_user, inet_server_addr(), inet_server_port();", conn);
                using var rd = cmd.ExecuteReader();
                if (rd.Read())
                {
                    BootLog($"DB AFTER CONTEXT => db={rd[0]} user={rd[1]} ip={rd[2]} port={rd[3]} " +
                            $"context={_context.CompanyCode}/{_context.Year}");
                }
            }
            catch (Exception ex)
            {
                BootLog("DB AFTER CONTEXT FAILED: " + ex);
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