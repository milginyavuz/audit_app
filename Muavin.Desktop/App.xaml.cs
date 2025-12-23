// App.xaml.cs
using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using Muavin.Desktop.ViewModels;
using Muavin.Xml.Data;

namespace Muavin.Desktop
{
    public partial class App : Application
    {
        private readonly ContextState _context = new();
        private readonly DbMuavinRepository _repo = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var culture = new CultureInfo("tr-TR");
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

            // ✅ Hesap planını yükle (yüklenemezse uygulama çalışmaya devam eder)
            try
            {
                var planPath = System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "Assets",
                    "HesapPlani.txt"
                );

                Muavin.Desktop.Util.AccountPlan.Load(planPath);
            }
            catch
            {
                // yüklenemezse sorun çıkarmasın; mizan fallback ile çalışır
            }

            var ctxWin = new ContextWindow(_context, _repo);
            var ok = ctxWin.ShowDialog();

            if (ok != true || !_context.HasContext)
            {
                Shutdown();
                return;
            }

            var vm = new MainViewModel(_context, _repo);
            var main = new MainWindow { DataContext = vm };

            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            main.Show();

            // DB ekranını açar açmaz yükle
            vm.LoadFromDatabaseCommand.Execute(null);
        }
    }
}
