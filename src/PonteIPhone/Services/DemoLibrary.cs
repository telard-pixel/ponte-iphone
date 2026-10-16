using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Services;

/// <summary>
/// Telefono finto della modalità dimostrativa (--demo): foto, Live Photo, video e screenshot di quattro mesi,
/// con miniature disegnate al volo. Serve alle schermate della CI e a provare l'app senza iPhone.
/// </summary>
public static class DemoLibrary
{
    public static FakeBackend Create()
    {
        var fake = new FakeBackend
        {
            Device = new DeviceInfo("demo-iphone", "iPhone di Roberto", "Apple Inc.", "iPhone 15 Pro", "DEMO0001", "26.0.1"),
            Storage = new StorageInfo("Internal Storage", 128_000_000_000, 30_400_000_000),
            BytesPerSecond = 38L * 1024 * 1024,
        };
        const long G = 1_000_000_000, M = 1_000_000;
        fake.Breakdown = StorageBreakdown.Build(128 * G, 30_400 * M, 11_200 * M,
        [
            new AppUsage("WhatsApp", "net.whatsapp.WhatsApp", 310 * M, 14_800 * M),
            new AppUsage("Instagram", "com.burbn.instagram", 480 * M, 3_900 * M),
            new AppUsage("Spotify", "com.spotify.client", 210 * M, 4_100 * M),
            new AppUsage("Google Maps", "com.google.Maps", 390 * M, 1_250 * M),
            new AppUsage("TikTok", "com.zhiliaoapp.musically", 560 * M, 2_300 * M),
            new AppUsage("Netflix", "com.netflix.Netflix", 120 * M, 2_900 * M),
            new AppUsage("Telegram", "ph.telegra.Telegraph", 180 * M, 1_700 * M),
            new AppUsage("Amazon", "com.amazon.Amazon", 260 * M, 410 * M),
            new AppUsage("Satispay", "com.satispay.satispay", 140 * M, 95 * M),
        ], photos: 38_600 * M, music: 2_100 * M, books: 350 * M, notes: []);
        var rnd = new Random(7);
        // Foto con contenuto vero (JPEG grande), così anche l'anteprima e il pannello dettagli si vedono.
        var photos = new Dictionary<string, (int Seed, int Scene)>();
        int number = 4812;
        var months = new[] { (2026, 10, 26), (2026, 9, 34), (2026, 8, 24), (2026, 7, 18), (2026, 6, 12) };
        foreach (var (y, m, count) in months)
        {
            var folder = $"{y}{m:00}_a";
            for (int i = 0; i < count; i++)
            {
                var day = Math.Max(1, 28 - i * 27 / count);
                var when = new DateTime(y, m, day, 8 + rnd.Next(13), rnd.Next(60), rnd.Next(60));
                if (when > DateTime.Now) when = DateTime.Now.AddHours(-i);
                var n = (number--).ToString("0000");
                var r = rnd.NextDouble();
                var seed = rnd.Next();
                if (r < 0.14)
                {
                    fake.Add(folder, $"IMG_{n}.MOV", 6_000 + rnd.Next(30_000), when, thumbnail: Thumb(seed, rnd.Next(4)));
                }
                else if (r < 0.20)
                {
                    fake.Add(folder, $"IMG_{n}.PNG", 1_500 + rnd.Next(2_000), when, thumbnail: Thumb(seed, 4));
                    photos[$"{folder}/IMG_{n}.PNG"] = (seed, 4);
                }
                else if (r < 0.58)
                {
                    var scene = rnd.Next(3);
                    fake.Add(folder, $"IMG_{n}.HEIC", 2_000 + rnd.Next(3_000), when, thumbnail: Thumb(seed, scene));
                    photos[$"{folder}/IMG_{n}.HEIC"] = (seed, scene);
                    fake.Add(folder, $"IMG_{n}.MOV", 2_500 + rnd.Next(2_000), when);
                }
                else
                {
                    var scene = rnd.Next(3);
                    fake.Add(folder, $"IMG_{n}.HEIC", 1_800 + rnd.Next(3_500), when, thumbnail: Thumb(seed, scene));
                    photos[$"{folder}/IMG_{n}.HEIC"] = (seed, scene);
                    if (rnd.NextDouble() < 0.12)
                    {
                        fake.Add(folder, $"IMG_E{n}.HEIC", 1_800 + rnd.Next(3_500), when);
                        fake.Add(folder, $"IMG_{n}.AAE", 1, when);
                    }
                }
            }
        }
        // Dimensioni verosimili (video fino a 60 MB); il contenuto si genera solo quando viene "scaricato".
        foreach (var f in fake.Files)
        {
            long real = f.Info.Extension switch
            {
                "MOV" => Math.Min(60_000_000L, 8_000_000L + f.Info.Size * 1_500L),
                "PNG" => 900_000L + f.Info.Size * 100L,
                "AAE" => 900L,
                _ => 1_600_000L + f.Info.Size * 600L,
            };
            var key = f.Info.Folder + "/" + f.Info.Name;
            if (photos.TryGetValue(key, out var p))
            {
                var jpeg = Thumb(p.Seed, p.Scene, 1600, 1200);
                f.Info = f.Info with { Size = jpeg.Length };
                f.Content = () => jpeg;
                continue;
            }
            var size = (int)real;
            f.Info = f.Info with { Size = size };
            f.Content = () => FakeBackend.Deterministic(key, size);
        }
        return fake;
    }

    /// <summary>Miniatura disegnata: mare, tramonto, piatto, prato, schermata.</summary>
    private static byte[] Thumb(int seed, int scene, int width = 240, int height = 240)
    {
        var rnd = new Random(seed);
        const int W = 240, H = 240;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(width / (double)W, height / (double)H));
            Color C(double h, double s, double l) => FromHsl(h, s, l);
            switch (scene)
            {
                case 0:
                {
                    var hue = 190 + rnd.Next(40);
                    dc.DrawRectangle(new LinearGradientBrush(C(hue, .6, .78), C(hue + 10, .45, .86), 90), null, new Rect(0, 0, W, H * .56));
                    dc.DrawRectangle(new LinearGradientBrush(C(hue + 5, .55, .38), C(hue, .5, .24), 90), null, new Rect(0, H * .56, W, H * .44));
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), null, new Point(40 + rnd.Next(160), 55), 16, 16);
                    break;
                }
                case 1:
                {
                    dc.DrawRectangle(new LinearGradientBrush(C(20 + rnd.Next(25), .8, .65), C(285 + rnd.Next(25), .35, .35), 90), null, new Rect(0, 0, W, H));
                    for (int k = 0; k < 2; k++)
                    {
                        var g = new StreamGeometry();
                        using (var ctx = g.Open())
                        {
                            ctx.BeginFigure(new Point(0, H), true, true);
                            for (int x = 0; x <= W; x += W / 6) ctx.LineTo(new Point(x, H * (.45 + k * .15) + rnd.Next(40)), true, false);
                            ctx.LineTo(new Point(W, H), true, false);
                        }
                        dc.DrawGeometry(new SolidColorBrush(k == 0 ? Color.FromArgb(150, 70, 50, 80) : Color.FromArgb(220, 30, 25, 45)), null, g);
                    }
                    break;
                }
                case 2:
                {
                    dc.DrawRectangle(new SolidColorBrush(C(25 + rnd.Next(15), .3, .4)), null, new Rect(0, 0, W, H));
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(244, 241, 234)), null, new Point(W / 2, H / 2), 88, 88);
                    int[] hues = [5, 35, 95, 0, 45];
                    foreach (var h in hues)
                        dc.DrawEllipse(new SolidColorBrush(C(h, .7, .4 + rnd.NextDouble() * .2)), null,
                            new Point(90 + rnd.Next(60), 90 + rnd.Next(60)), 12 + rnd.Next(14), 12 + rnd.Next(14));
                    break;
                }
                case 3:
                {
                    dc.DrawRectangle(new LinearGradientBrush(C(100 + rnd.Next(40), .35, .5), C(130 + rnd.Next(30), .4, .22), 45), null, new Rect(0, 0, W, H));
                    var dark = new SolidColorBrush(Color.FromArgb(190, 25, 25, 30));
                    dc.DrawEllipse(dark, null, new Point(W / 2, H * .42), 28, 28);
                    dc.DrawEllipse(dark, null, new Point(W / 2, H * .98), 62, 70);
                    break;
                }
                default:
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(242, 242, 247)), null, new Rect(0, 0, W, H));
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(10, 132, 255)), null, new Rect(24, 20, 120, 14));
                    for (int i = 0; i < 6; i++)
                        dc.DrawRectangle(new SolidColorBrush(i % 2 == 0 ? Color.FromRgb(229, 229, 234) : Color.FromRgb(209, 209, 214)), null,
                            new Rect(24, 52 + i * 30, 120 + rnd.Next(80), 16));
                    break;
                }
            }
        }
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        double q = l < .5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double F(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < .5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
        return Color.FromRgb((byte)(F(h + 1.0 / 3) * 255), (byte)(F(h) * 255), (byte)(F(h - 1.0 / 3) * 255));
    }
}
