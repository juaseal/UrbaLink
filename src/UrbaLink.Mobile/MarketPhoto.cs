using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

internal static class MarketPhoto
{
    public static async Task<byte[]?> Pick()
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Foto del objeto", FileTypes = FilePickerFileType.Images });
        if (file is null) return null;
        await using var stream = await file.OpenReadAsync();
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            if (memory.Length + read > 15_000_000) throw new InvalidOperationException("Elige una foto de menos de 15 MB.");
            memory.Write(buffer, 0, read);
        }
#if ANDROID
        var bytes = memory.ToArray();
        var bounds = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
        Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) throw new InvalidOperationException("No se pudo leer esta foto.");
        var sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > 800) sample *= 2;
        using var bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length,
            new Android.Graphics.BitmapFactory.Options { InSampleSize = sample });
        if (bitmap is null) throw new InvalidOperationException("No se pudo leer esta foto.");
        // Re-encoding strips EXIF metadata and keeps the authenticated catalog lightweight.
        for (var size = 640; size >= 160; size /= 2)
        {
            var ratio = Math.Min(1d, (double)size / Math.Max(bitmap.Width, bitmap.Height));
            using var scaled = Android.Graphics.Bitmap.CreateScaledBitmap(bitmap,
                Math.Max(1,(int)(bitmap.Width*ratio)),Math.Max(1,(int)(bitmap.Height*ratio)),true);
            using var output = new MemoryStream();
            scaled.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 65, output);
            if (output.Length <= MarketRules.MaxPhotoBytes) return output.ToArray();
        }
        throw new InvalidOperationException("No se pudo reducir esta foto. Prueba con otra.");
#else
        throw new InvalidOperationException("La selección de fotos está disponible en Android.");
#endif
    }
}
