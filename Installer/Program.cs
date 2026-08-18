using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace KeysAutoclickerInstaller;
public partial class App : Application { [STAThread] public static void Main() => new App().Run(); protected override void OnStartup(StartupEventArgs e) { base.OnStartup(e); var target = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Keys Autoclicker"); Directory.CreateDirectory(target); var exe = System.IO.Path.Combine(target, "Keys Autoclicker.exe"); using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeysAutoclicker.exe")!; using var output = File.Create(exe); input.CopyTo(output); MessageBox.Show("Keys Autoclicker was installed for this user.\n\nLocation: " + target, "Keys Autoclicker Setup", MessageBoxButton.OK, MessageBoxImage.Information); Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); Shutdown(); } }
