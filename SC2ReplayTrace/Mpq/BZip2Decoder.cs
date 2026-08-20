using ICSharpCode.SharpZipLib.BZip2;

namespace Biz.Bizadm.SC2ReplayTrace.Mpq;

internal static class BZip2Decoder
{
    public static byte[] Decode(ReadOnlySpan<byte> input)
    {
        using var source = new MemoryStream(input.ToArray(), writable: false);
        using var bz2 = new BZip2InputStream(source);
        using var output = new MemoryStream();
        bz2.CopyTo(output);
        return output.ToArray();
    }
}
