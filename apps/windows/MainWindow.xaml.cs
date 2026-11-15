using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NopeView.App.Native;

namespace NopeView.App;

public sealed partial class MainWindow : Window
{
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        Activated += MainWindow_Activated;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        VerifySlice();
    }

    private void VerifySlice()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "native", "x64", "test.png");
        try
        {
            DetailText.Text = $"DLL path: {path}";
            var version = PngCodec.Version();

            if (!PngCodec.TryDecode(path, out var image))
            {
                StatusText.Text = $"C ABI v{version} - decode failed";
                WriteResult($"FAIL decode v{version}");
                return;
            }

            try
            {
                var samples = new byte[8];
                Marshal.Copy(image.Pixels, samples, 0, samples.Length);
                var hex = Convert.ToHexString(samples);
                StatusText.Text = $"C ABI v{version} - decoded OK: {image.Width}x{image.Height}, {image.Channels} ch";
                DetailText.Text = $"First 4 RGBA pixels: {hex}  |  native ptr 0x{image.Pixels.ToInt64():X}";
                WriteResult($"OK v{version} {image.Width}x{image.Height} ch={image.Channels} ptr=0x{image.Pixels.ToInt64():X} px={hex}");
            }
            finally
            {
                PngCodec.ImageFree(ref image);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
            DetailText.Text = ex.ToString();
            WriteResult("EXCEPTION " + ex);
        }
    }

    private static void WriteResult(string line)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "slice_result.txt");
            File.WriteAllText(path, $"{DateTime.Now:O} {line}");
        }
        catch
        {
            // Diagnostic output failure must not affect the app.
        }
    }
}
