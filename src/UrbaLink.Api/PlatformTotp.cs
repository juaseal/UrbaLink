using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
namespace UrbaLink.Api;

internal static class PlatformTotp
{
    public static long? Verify(string? secret,string? code,DateTimeOffset now)
    {
        if(secret is null || code is null || code.Length!=6 || !code.All(char.IsAsciiDigit))return null;
        var key=new List<byte>();int bits=0,value=0;
        foreach(var c in secret.Trim().ToUpperInvariant().TrimEnd('='))
        {
            var digit="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c);if(digit<0)return null;
            value=(value<<5)|digit;bits+=5;if(bits>=8){bits-=8;key.Add((byte)(value>>bits));}
        }
        if(key.Count<20)return null;
        var step=now.ToUnixTimeSeconds()/30;
        for(var offset=-1;offset<=1;offset++)
        {
            var counter=new byte[8];BinaryPrimitives.WriteInt64BigEndian(counter,step+offset);
            var hash=HMACSHA1.HashData(key.ToArray(),counter);var pos=hash[^1]&15;
            var number=(BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(pos,4))&0x7fffffff)%1_000_000;
            var expected=number.ToString("D6",CultureInfo.InvariantCulture);
            if(CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(expected),System.Text.Encoding.ASCII.GetBytes(code)))return step+offset;
        }
        return null;
    }
}
