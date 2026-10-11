namespace UrbaLink.Contracts;

public static class MarketPhotoRules
{
    // Inspect the JPEG frame before letting clients decode it. Small compressed files can
    // otherwise advertise very large images. Mobile uploads are re-encoded to <= 800 px.
    public static bool IsValid(byte[] photo)
    {
        if(photo.Length is <4 or >MarketRules.MaxPhotoBytes || photo[0]!=255 || photo[1]!=216 || photo[^2]!=255 || photo[^1]!=217)
            return false;
        var offset=2;
        var frame=false;
        while(offset+3<photo.Length)
        {
            if(photo[offset++]!=255) return false;
            while(offset<photo.Length && photo[offset]==255) offset++;
            if(offset>=photo.Length) return false;
            var marker=photo[offset++];
            if(marker==0xDA) return frame; // Compressed scan follows; platform decoder reads pixels.
            if(marker is 0xD8 or 0xD9 or 0x00) return false;
            if(offset+2>photo.Length) return false;
            var length=photo[offset]*256+photo[offset+1];
            if(length<2 || offset+length>photo.Length) return false;
            if(marker is 0xC0 or 0xC1 or 0xC2)
            {
                if(length<8) return false;
                var height=photo[offset+3]*256+photo[offset+4];
                var width=photo[offset+5]*256+photo[offset+6];
                if(width is <1 or >1024 || height is <1 or >1024) return false;
                frame=true;
            }
            offset+=length;
        }
        return false;
    }
}
