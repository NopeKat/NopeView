using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NopeView.App.Native;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NopeView.App;

public sealed partial class MainWindow : Window
{
    private readonly SoftwareBitmapSource _source = new();
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        ImageView.Source = _source;
        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await LoadAndRenderAsync();
    }

    private async Task LoadAndRenderAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "native", "x64", "test.png");
        var context = IntPtr.Zero;
        try
        {
            DetailText.Text = $"File: {path}";
            var version = RouterApi.Version();

            if (!RouterApi.TryCreate(null, out context))
            {
                StatusText.Text = "Router create failed";
                WriteResult("FAIL router create");
                return;
            }

            var formats = RouterApi.ListFormats(context);
            var formatList = formats.Count == 0 ? "(none)" : string.Join(", ", formats);

            if (!RouterApi.TryDecodeFile(context, path, out var image, out var decodeStatus))
            {
                StatusText.Text = $"Router ABI v{version} - decode failed ({decodeStatus})";
                WriteResult($"FAIL decode v{version} status={decodeStatus} formats={formatList}");
                return;
            }

            SoftwareBitmap bitmap;
            try
            {
                bitmap = ToSoftwareBitmap(ref image);
            }
            finally
            {
                RouterApi.FreeImage(ref image);
            }

            await _source.SetBitmapAsync(bitmap);

            await SaveDiagnosticPngAsync(bitmap);

            StatusText.Text = $"Rendered OK: {bitmap.PixelWidth} x {bitmap.PixelHeight}";
            DetailText.Text = $"Router ABI v{version} - formats: {formatList}";
            WriteResult($"RENDER OK v{version} {bitmap.PixelWidth}x{bitmap.PixelHeight} formats={formatList}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
            DetailText.Text = ex.ToString();
            WriteResult("EXCEPTION " + ex);
        }
        finally
        {
            RouterApi.Close(context);
        }
    }

    /// <summary>
    /// Copies decoded RGBA pixels into a SoftwareBitmap.
    /// Uses DataWriter.DetachBuffer so the buffer is fully managed and no COM
    /// byte-access interface is required.
    /// </summary>
    private static SoftwareBitmap ToSoftwareBitmap(ref NvRouterImage image)
    {
        var pixelCount = image.Width * image.Height * 4;
        var bgra = new byte[pixelCount];

        // Copy RGBA from the codec, then convert to BGRA and premultiply alpha.
        Marshal.Copy(image.Pixels, bgra, 0, pixelCount);
        for (var i = 0; i < pixelCount; i += 4)
        {
            var r = bgra[i];
            var g = bgra[i + 1];
            var b = bgra[i + 2];
            var a = bgra[i + 3];

            // Premultiply so the bitmap matches BitmapAlphaMode.Premultiplied.
            bgra[i] = (byte)(b * a / 255);
            bgra[i + 1] = (byte)(g * a / 255);
            bgra[i + 2] = (byte)(r * a / 255);
            bgra[i + 3] = a;
        }

        using var writer = new DataWriter();
        writer.WriteBytes(bgra);
        var buffer = writer.DetachBuffer();

        return SoftwareBitmap.CreateCopyFromBuffer(
            buffer,
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Premultiplied);
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

    /// <summary>
    /// Writes the rendered bitmap to a PNG next to the diagnostics file, so the
    /// result can be inspected without a screen capture.
    /// </summary>
    private static async Task SaveDiagnosticPngAsync(SoftwareBitmap bitmap)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "render_check.png");

        var memory = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        memory.Seek(0);
        var length = (uint)memory.Size;
        using var reader = new DataReader(memory.GetInputStreamAt(0));
        await reader.LoadAsync(length);
        var bytes = new byte[length];
        reader.ReadBytes(bytes);

        await File.WriteAllBytesAsync(path, bytes);
    }
}
