using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ScreenReceiver.Server.Services;

/// <summary>
/// Stitches multiple JPEG frames vertically into one long JPEG image.
/// Used for multi-screen questions (data analysis, long passages).
/// </summary>
public static class ImageStitcher
{
    public static byte[] StitchVertical(List<byte[]> jpegs, int jpegQuality = 80)
    {
        if (jpegs.Count == 0) return Array.Empty<byte>();
        if (jpegs.Count == 1) return jpegs[0];

        using var images = new List<Bitmap>();
        foreach (var j in jpegs)
        {
            using var ms = new MemoryStream(j);
            images.Add(new Bitmap(Image.FromStream(ms)));
        }

        var width = images.Max(i => i.Width);
        var height = images.Sum(i => i.Height);

        // Cap total height to avoid huge images
        if (height > 4000)
        {
            var ratio = 4000.0 / height;
            width = (int)(width * ratio);
            height = 4000;
        }

        using var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            int y = 0;
            foreach (var img in images)
            {
                g.DrawImage(img, 0, y, width, (int)(img.Height * (width / (double)img.Width)));
                y += (int)(img.Height * (width / (double)img.Width));
            }
        }

        using var outMs = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)jpegQuality);
        bmp.Save(outMs, codec, encParams);
        return outMs.ToArray();
    }
}
