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
        using var exifStream = new MemoryStream(bytes);
        using var exif = new AndroidX.ExifInterface.Media.ExifInterface(exifStream);
        var orientation = exif.GetAttributeInt(AndroidX.ExifInterface.Media.ExifInterface.TagOrientation, 1);
        using var matrix = new Android.Graphics.Matrix();
        switch (orientation)
        {
            case 2: matrix.PostScale(-1,1); break;
            case 3: matrix.PostRotate(180); break;
            case 4: matrix.PostScale(1,-1); break;
            case 5: matrix.PostRotate(90); matrix.PostScale(-1,1); break;
            case 6: matrix.PostRotate(90); break;
            case 7: matrix.PostRotate(270); matrix.PostScale(-1,1); break;
            case 8: matrix.PostRotate(270); break;
        }
        // Re-encoding strips EXIF metadata and keeps the authenticated catalog lightweight.
        for (var size = 640; size >= 160; size /= 2)
        {
            var ratio = Math.Min(1d, (double)size / Math.Max(bitmap.Width, bitmap.Height));
            var scaled = Android.Graphics.Bitmap.CreateScaledBitmap(bitmap,
                Math.Max(1,(int)(bitmap.Width*ratio)),Math.Max(1,(int)(bitmap.Height*ratio)),true);
            var oriented = Android.Graphics.Bitmap.CreateBitmap(scaled,0,0,scaled.Width,scaled.Height,matrix,true)!;
            try
            {
                using var output = new MemoryStream();
                oriented.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 65, output);
                if (output.Length <= MarketRules.MaxPhotoBytes) return output.ToArray();
            }
            finally
            {
                // Android may return the original bitmap when no transform is needed.
                if(oriented.Handle!=scaled.Handle) oriented.Dispose();
                if(scaled.Handle!=bitmap.Handle) scaled.Dispose();
            }
        }
        throw new InvalidOperationException("No se pudo reducir esta foto. Prueba con otra.");
#else
        throw new InvalidOperationException("La selección de fotos está disponible en Android.");
#endif
    }
}
