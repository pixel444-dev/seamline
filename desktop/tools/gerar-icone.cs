// Gera o ícone do Windows (seamline.ico) e uma imagem grande da logo (docs/logo.png) a partir de Assets/Logo.xaml.
// Rodar de dentro da pasta desktop:  dotnet run tools/gerar-icone.cs
// (Só precisa rodar de novo se a logo mudar; o .ico gerado fica no repositório.)
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var assets = Path.GetFullPath(Path.Combine("Seamline.Desktop", "Assets"));
var thread = new Thread(() =>
{
    using var xaml = File.OpenRead(Path.Combine(assets, "Logo.xaml"));
    var resources = (ResourceDictionary)XamlReader.Load(xaml);
    var logo = (DrawingImage)resources["LogoIcon"];

    // Os tamanhos que o Windows usa (barra de tarefas, Alt+Tab, Explorer, área de trabalho).
    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
    var images = sizes.Select(size => Render(logo, size)).ToList();
    WriteIco(Path.Combine(assets, "seamline.ico"), sizes, images);
    File.WriteAllBytes(Path.Combine("..", "docs", "logo.png"), Render(logo, 512));
    Console.WriteLine($"Ícone salvo em {assets}");
});
thread.SetApartmentState(ApartmentState.STA); // o WPF só desenha numa thread STA
thread.Start();
thread.Join();

static byte[] Render(ImageSource image, int size)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
        dc.DrawImage(image, new Rect(0, 0, size, size));
    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

// Formato .ico: cabeçalho + uma entrada por tamanho + as imagens PNG (aceito desde o Windows Vista).
static void WriteIco(string path, int[] sizes, List<byte[]> images)
{
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(images[i].Length);
        writer.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images)
        writer.Write(image);
}
